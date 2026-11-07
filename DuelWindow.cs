using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TCPTunnel;

internal sealed class DuelWindow
{
    private readonly object gate = new();
    private readonly DuelPacket round;
    private readonly Action<DuelPacket> send;
    private readonly DuelPracticeRound practice;
    private readonly Action<int> practiceFinish;
    private int best, bestHits, bestStreak;
    private bool practiceFinished, recordSaved;
    private readonly int radius, lifetime;
    private IntPtr window;
    private bool closed;
    private DuelPacket target;
    private long shown;
    private readonly Dictionary<int, (DuelPacket Target, long Shown)> targets = new();
    private readonly List<(int Id, int X, int Y, int Radius)> drawn = new();
    private (int X, int Y, int Radius, int Streak, int Delta, long At)? effect;
    private int you, them, seconds;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int fixedWidth, fixedHeight;
    private const uint Update = 0x8001, End = 0x8002;

    internal DuelWindow(DuelPacket round, Action<DuelPacket> send, DuelPracticeRound practice = null, int best = 0, Action<int> practiceFinish = null)
    {
        this.round = round; this.send = send;
        this.practice = practice; this.best = best; this.practiceFinish = practiceFinish;
        (radius, _, lifetime) = DuelProtocol.Rules(round.Level);
        seconds = round.Minutes * 60;
    }

    internal void Start(bool visible = true) => new Thread(() => Run(visible)) { IsBackground = true, Name = "TCPTunnel duel" }.Start();

    internal void SetPracticeRecord(int record, bool saved, int hits = 0, int streak = 0)
    {
        lock (gate)
        {
            best = record; bestHits = hits; bestStreak = streak; recordSaved = saved;
            if (!closed && window != IntPtr.Zero) PostMessage(window, Update, IntPtr.Zero, IntPtr.Zero);
        }
    }

    internal void Receive(DuelPacket packet)
    {
        lock (gate)
        {
            if (closed) return;
            if (round.Version == 2)
            {
                if (packet.Kind == "target")
                {
                    if (packet.Version != 2 || packet.Slot < 0 || packet.Radius is not (12 or 21 or 30) ||
                        packet.Motion is < 0 or > 3 || packet.Seed is < 1 or > 1000000 ||
                        packet.X < packet.Radius || packet.X >= DuelProtocol.Width - packet.Radius ||
                        packet.Y < 110 + packet.Radius || packet.Y >= DuelProtocol.Height - packet.Radius ||
                        targets.Count >= (round.Mode == "minefield" ? round.Count : 3)) return;
                    if (round.Mode != "minefield") targets.Clear();
                    targets[packet.Slot] = (packet, Stopwatch.GetTimestamp());
                }
                else if (packet.Version == 2)
                {
                    you = packet.You; them = packet.Them;
                    if (packet.Clear == 1 && targets.Remove(packet.Slot, out var hit) && packet.Delta > 0)
                    {
                        var p = DuelArena.Position(hit.Target, round.Level, (long)Stopwatch.GetElapsedTime(hit.Shown).TotalMilliseconds);
                        Feedback(p.X, p.Y, hit.Target.Radius, packet.Streak, packet.Delta);
                    }
                }
                if (window != IntPtr.Zero) PostMessage(window, Update, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            if (packet.Kind == "target")
            {
                if (packet.Slot < 0 || !DuelProtocol.Hits(packet.X, packet.Y, packet.X, packet.Y, radius) ||
                    packet.X < radius || packet.X + radius >= DuelProtocol.Width ||
                    packet.Y < 45 + radius || packet.Y + radius >= DuelProtocol.Height) return;
                target = packet; shown = Stopwatch.GetTimestamp();
            }
            else
            {
                if (you < packet.You && target?.Slot == packet.Slot)
                    Feedback(target.X, target.Y, radius, 0, 1);
                you = packet.You; them = packet.Them;
                if (packet.X == 1 && target?.Slot == packet.Slot) target = null;
            }
            if (window != IntPtr.Zero) PostMessage(window, Update, IntPtr.Zero, IntPtr.Zero);
        }
    }

    internal void Close()
    {
        lock (gate)
        {
            closed = true;
            if (window != IntPtr.Zero) PostMessage(window, End, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void Run(bool visible)
    {
        var instance = GetModuleHandle(null);
        string className = "TCPTunnel.Duel." + round.Id.ToString("N");
        WindowProc callback = Handle;
        var info = new WindowClass { Procedure = callback, Instance = instance, Name = className, Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32515)) };
        bool registered = false, failed = false;
        try
        {
            registered = RegisterClass(ref info) != 0;
            if (!registered) { failed = true; return; }
            IntPtr foreground = GetForegroundWindow();
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(foreground, 2), ref monitor)) { failed = true; return; }
            double scale = Math.Min(1.0, Math.Min((monitor.Work.Right - monitor.Work.Left) / (double)DuelProtocol.Width,
                (monitor.Work.Bottom - monitor.Work.Top) / (double)DuelProtocol.Height));
            int width = fixedWidth = (int)(DuelProtocol.Width * scale);
            int height = fixedHeight = (int)(DuelProtocol.Height * scale);
            if (width <= 0 || height <= 0) { failed = true; return; }
            var handle = CreateWindowEx(0, className, "TCPTunnel Duel", 0x80000000,
                monitor.Work.Left + (monitor.Work.Right - monitor.Work.Left - width) / 2,
                monitor.Work.Top + (monitor.Work.Bottom - monitor.Work.Top - height) / 2,
                width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (handle == IntPtr.Zero) { failed = true; return; }
            lock (gate)
            {
                window = handle;
                if (closed) { DestroyWindow(handle); return; }
            }
            if (practice != null) clock.Restart();
            if (visible) ShowWindow(handle, 5);
            if (SetTimer(handle, new UIntPtr(1), 30, IntPtr.Zero) == UIntPtr.Zero) { failed = true; return; }
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        finally
        {
            lock (gate)
            {
                if (window != IntPtr.Zero) DestroyWindow(window);
                failed |= !closed;
                closed = true; window = IntPtr.Zero;
            }
            if (registered) UnregisterClass(className, instance);
            GC.KeepAlive(callback);
            if (failed) send(new("quit", round.Id));
        }
    }

    private IntPtr Handle(IntPtr hwnd, uint message, IntPtr w, IntPtr l)
    {
        switch (message)
        {
            case 0x0046:
                var position = Marshal.PtrToStructure<WindowPosition>(l);
                if ((position.Flags & 1) == 0)
                {
                    position.Width = fixedWidth; position.Height = fixedHeight;
                    Marshal.StructureToPtr(position, l, false);
                }
                break;
            case 0x000F: Paint(hwnd); return IntPtr.Zero;
            case 0x0014: return new IntPtr(1);
            case Update: InvalidateRect(hwnd, IntPtr.Zero, false); return IntPtr.Zero;
            case 0x0113:
                if (practice != null) { TickPractice(hwnd); return IntPtr.Zero; }
                lock (gate)
                {
                    int left = (int)Math.Clamp((round.Minutes * 60000L + 2499 - clock.ElapsedMilliseconds) / 1000, 0, round.Minutes * 60);
                    if (left != seconds) { seconds = left; InvalidateRect(hwnd, IntPtr.Zero, false); }
                    if (target != null && Stopwatch.GetElapsedTime(shown).TotalMilliseconds >= lifetime)
                    { target = null; InvalidateRect(hwnd, IntPtr.Zero, false); }
                    if (round.Version == 2 && round.Mode != "minefield")
                    {
                        foreach (var entry in new List<KeyValuePair<int, (DuelPacket Target, long Shown)>>(targets))
                            if (Stopwatch.GetElapsedTime(entry.Value.Shown).TotalMilliseconds >= lifetime) targets.Remove(entry.Key);
                        if (drawn.Count > targets.Count || (round.Mode == "moving" && targets.Count > 0))
                            InvalidateRect(hwnd, IntPtr.Zero, false);
                    }
                    TickFeedback(hwnd);
                }
                return IntPtr.Zero;
            case 0x0201:
                DuelPacket shot = null;
                lock (gate)
                {
                    if (!closed && round.Version == 2 && drawn.Count > 0 && GetClientRect(hwnd, out var field))
                    {
                        int x = unchecked((short)l.ToInt64()), y = unchecked((short)(l.ToInt64() >> 16));
                        if (x >= 0 && y >= 0 && x < field.Right && y < field.Bottom)
                        {
                            x = x * DuelProtocol.Width / field.Right; y = y * DuelProtocol.Height / field.Bottom;
                            int id = -1;
                            bool visible = false;
                            foreach (var item in drawn)
                            {
                                if (!targets.TryGetValue(item.Id, out var active) ||
                                    (round.Mode != "minefield" && Stopwatch.GetElapsedTime(active.Shown).TotalMilliseconds >= lifetime)) continue;
                                visible = true;
                                if (DuelProtocol.Hits(x, y, item.X, item.Y, item.Radius)) { id = item.Id; break; }
                            }
                            if (visible) shot = new("shot", round.Id, Slot: id, X: x, Y: y, Version: 2);
                        }
                    }
                    else if (!closed && target != null && Stopwatch.GetElapsedTime(shown).TotalMilliseconds < lifetime && GetClientRect(hwnd, out var rect))
                    {
                        int x = unchecked((short)l.ToInt64()), y = unchecked((short)(l.ToInt64() >> 16));
                        if (x >= 0 && y >= 0 && x < rect.Right && y < rect.Bottom)
                        {
                            x = x * DuelProtocol.Width / rect.Right; y = y * DuelProtocol.Height / rect.Bottom;
                            var candidate = new DuelPacket("shot", round.Id, Slot: target.Slot, X: x, Y: y);
                            if (practice == null) shot = candidate;
                            else
                            {
                                if (practice.Shoot(candidate, clock.ElapsedMilliseconds))
                                {
                                    Feedback(target.X, target.Y, radius, practice.Streak, practice.Streak > 1 ? 2 : 1);
                                    target = null;
                                }
                                you = practice.Score;
                                InvalidateRect(hwnd, IntPtr.Zero, false);
                            }
                        }
                    }
                }
                if (shot != null) send(shot);
                return IntPtr.Zero;
            case 0x0100 when w.ToInt64() == 27:
            case 0x0010:
                lock (gate) { if (!closed) { closed = true; send(new("quit", round.Id)); } }
                DestroyWindow(hwnd); return IntPtr.Zero;
            case End: DestroyWindow(hwnd); return IntPtr.Zero;
            case 0x0002: PostQuitMessage(0); return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, message, w, l);
    }

    private void TickPractice(IntPtr hwnd)
    {
        int completed = -1;
        lock (gate)
        {
            if (closed || practiceFinished) return;
            long elapsed = clock.ElapsedMilliseconds;
            int remaining = (int)Math.Max(0, (practice.Duration - elapsed + 999) / 1000);
            bool changed = practice.Advance(elapsed);
            target = practice.Target;
            if (changed) shown = Stopwatch.GetTimestamp();
            if (elapsed >= practice.Duration)
            {
                practiceFinished = true;
                completed = practice.Score;
                KillTimer(hwnd, new UIntPtr(1));
            }
            if (changed || seconds != remaining || practiceFinished)
                InvalidateRect(hwnd, IntPtr.Zero, false);
            TickFeedback(hwnd);
            seconds = remaining;
        }
        if (completed >= 0) practiceFinish?.Invoke(completed);
    }

    private void Feedback(int x, int y, int radius, int streak, int delta)
    {
        if (!ConsoleGraphic.Enabled) return;
        if (ApplicationSettings.Current.DuelEffects)
            effect = (x, y, radius, streak, delta, Stopwatch.GetTimestamp());
        if (ApplicationSettings.Current.DuelSound) DuelSound.Play(Math.Max(1, streak));
    }

    private void TickFeedback(IntPtr hwnd)
    {
        if (effect is not { } hit) return;
        if (Stopwatch.GetElapsedTime(hit.At).TotalMilliseconds >= 680) effect = null;
        InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static void DrawTarget(IntPtr output, Rect bounds, int x, int y, int r)
    {
        IntPtr oldBrush = SelectObject(output, GetStockObject(0));
        IntPtr oldPen = SelectObject(output, GetStockObject(8));
        Ellipse(output, (x-r)*bounds.Right/DuelProtocol.Width, (y-r)*bounds.Bottom/DuelProtocol.Height,
            (x+r)*bounds.Right/DuelProtocol.Width, (y+r)*bounds.Bottom/DuelProtocol.Height);
        SelectObject(output, oldPen); SelectObject(output, oldBrush);
    }

    private void DrawFeedback(IntPtr output, Rect bounds)
    {
        if (effect is not { } hit) return;
        double age = Stopwatch.GetElapsedTime(hit.At).TotalMilliseconds;
        if (age >= 680) return;
        int x = hit.X * bounds.Right / DuelProtocol.Width;
        int y = hit.Y * bounds.Bottom / DuelProtocol.Height;
        if (age < 290)
        {
            double step = age / 290;
            uint glow = (uint)(180 * (1-step)) * 0x010101;
            int reach = (int)(hit.Radius * 0.6 + 42 * (1 - (1-step)*(1-step)));
            int ring = Math.Max(1, (int)(hit.Radius * (0.75 + step * 1.25) * bounds.Right / DuelProtocol.Width));
            IntPtr pen = CreatePen(0, 1, glow);
            if (pen != IntPtr.Zero)
            {
                IntPtr previousPen = SelectObject(output, pen);
                IntPtr previousBrush = SelectObject(output, GetStockObject(5));
                Ellipse(output, x-ring, y-ring, x+ring, y+ring);
                SelectObject(output, previousBrush); SelectObject(output, previousPen);
                DeleteObject(pen);
            }
            for (int i = 0; i < 10; i++)
            {
                double angle = i * 2 * Math.PI / 10;
                int sx = x + (int)(reach * Math.Cos(angle)), sy = y + (int)(reach * Math.Sin(angle));
                SetPixel(output, sx, sy, glow); SetPixel(output, sx+1, sy, glow);
            }
        }
        double fade = Math.Pow(1 - age / 680, 1.65);
        string label = hit.Streak is 3 or 5 or 10 ? $"{hit.Streak} HIT STREAK" : hit.Delta == 1 ? "+1 POINT" : "+2 POINTS";
        int left = Math.Clamp(x - label.Length * 4, 0, Math.Max(0, bounds.Right - label.Length * 9));
        int top = Math.Max(30, y - hit.Radius - 24 - (int)(50*(1-Math.Pow(1-age/680,1.6))));
        SetBkMode(output, 1); SetTextColor(output, (uint)(42*fade) * 0x010101);
        TextOut(output,left-2,top+5,label,label.Length);
        TextOut(output,left+3,top+7,label,label.Length);
        SetBkMode(output, 1); SetTextColor(output, 0x1C1512);
        TextOut(output,left+1,top+1,label,label.Length);
        SetTextColor(output, (uint)((int)(209*fade)<<16 | (int)(188*fade)<<8 | (int)(161*fade)));
        TextOut(output,left,top,label,label.Length);
    }

    private void Paint(IntPtr hwnd)
    {
        IntPtr dc = BeginPaint(hwnd, out var paint);
        try
        {
            if (dc == IntPtr.Zero || !GetClientRect(hwnd, out var bounds) || bounds.Right <= 0 || bounds.Bottom <= 0) return;
            IntPtr memory = CreateCompatibleDC(dc), bitmap = CreateCompatibleBitmap(dc, bounds.Right, bounds.Bottom);
            try
            {
                IntPtr previous = memory != IntPtr.Zero && bitmap != IntPtr.Zero ? SelectObject(memory, bitmap) : IntPtr.Zero;
                IntPtr output = previous == IntPtr.Zero ? dc : memory;
                FillRect(output, ref bounds, GetStockObject(4));
                lock (gate)
                {
                    if (target != null)
                        DrawTarget(output, bounds, target.X, target.Y, radius);
                    drawn.Clear();
                    foreach (var entry in targets.Values)
                    {
                        long age = (long)Stopwatch.GetElapsedTime(entry.Shown).TotalMilliseconds;
                        if (round.Mode != "minefield" && age >= lifetime) continue;
                        var p = DuelArena.Position(entry.Target, round.Level, age);
                        DrawTarget(output, bounds, p.X, p.Y, entry.Target.Radius);
                        drawn.Add((entry.Target.Slot, p.X, p.Y, entry.Target.Radius));
                    }
                    DrawFeedback(output, bounds);
                    SetBkMode(output, 1); SetTextColor(output, 0xFFFFFF);
                    string status = practice != null
                        ? practiceFinished
                            ? $"PRACTICE DONE  {you} PTS / {practice.Hits} H / STREAK {practice.BestStreak}{(recordSaved ? "" : " (NOT SAVED)")}   Esc"
                            : $"PRACTICE  {you} PTS / {practice.Hits} H   {seconds / 60}:{seconds % 60:D2}   Esc"
                        : $"{(round.Blitz ? "BLITZ" : "DUEL")}  {you}:{them} {(round.Blitz ? "H" : round.Version == 2 ? "PTS" : "H")}   {seconds / 60}:{seconds % 60:D2}   Esc";
                    TextOut(output, 12, 10, status, status.Length);
                    if (practice != null)
                    {
                        string records = $"BEST: {best} PTS / {bestHits} H / STREAK {bestStreak}";
                        TextOut(output, 12, 27, records, records.Length);
                    }
                }
                if (output == memory) { BitBlt(dc, 0, 0, bounds.Right, bounds.Bottom, memory, 0, 0, 0x00CC0020); SelectObject(memory, previous); }
            }
            finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (memory != IntPtr.Zero) DeleteDC(memory); }
        }
        finally { EndPaint(hwnd, ref paint); }
    }

    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    { public uint Style; public WindowProc Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string Menu, Name; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Window; public uint Code; public IntPtr W, L; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition { public IntPtr Window, After; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct PaintInfo { public IntPtr Dc; public int Erase; public Rect Bounds; public int Restore, Update; public long R1, R2, R3, R4; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr arg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr module, IntPtr resource);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint interval, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintInfo paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PaintInfo paint);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int id);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(IntPtr dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern uint SetPixel(IntPtr dc, int x, int y, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int sx, int sy, uint operation);
}
