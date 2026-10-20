using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;

namespace TCPTunnel
{
    internal enum ConsoleInputKind { Key, Move, Click, Wheel, ModeChanged }
    internal readonly record struct ConsoleInputEvent(ConsoleInputKind Kind, ConsoleKeyInfo Key = default, int X = 0, int Y = 0, int Direction = 0);

    internal static class ConsoleInput
    {
        // WARNING: These offsets are the Windows INPUT_RECORD ABI; changing them corrupts keyboard and mouse events.
        [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)]
        internal struct InputRecord
        {
            [FieldOffset(0)] internal ushort Type;
            [FieldOffset(4)] internal int KeyDown;
            [FieldOffset(8)] internal ushort Repeat;
            [FieldOffset(10)] internal ushort VirtualKey;
            [FieldOffset(14)] internal char Character;
            [FieldOffset(16)] internal uint KeyModifiers;
            [FieldOffset(4)] internal short X;
            [FieldOffset(6)] internal short Y;
            [FieldOffset(8)] internal uint Buttons;
            [FieldOffset(12)] internal uint MouseModifiers;
            [FieldOffset(16)] internal uint MouseFlags;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int id);
        [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
        [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
        [DllImport("kernel32.dll")] private static extern bool GetNumberOfConsoleInputEvents(IntPtr handle, out uint count);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "PeekConsoleInputW")]
        private static extern bool PeekConsoleInput(IntPtr handle, [Out] InputRecord[] buffer, uint count, out uint read);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleInputW")]
        private static extern bool ReadConsoleInput(IntPtr handle, [Out] InputRecord[] buffer, uint count, out uint read);
        private static readonly InputRecord[] buffer = new InputRecord[1];
        private static readonly HashSet<ConsoleKey> heldArrows = new();
        private static readonly NavigationMode navigation = new();
        private static IntPtr input;
        private static uint originalMode;
        private static bool native;
        private static bool leftDown;
        private static ConsoleKeyInfo repeatedKey;
        private static int repeats, wheelRemainder;
        internal static bool MouseMode => navigation.Mouse;
        internal static bool HasPending
        {
            get
            {
                if (native) return GetNumberOfConsoleInputEvents(input, out uint count) && count > 0;
                try { return !Console.IsInputRedirected && Console.KeyAvailable; } catch (InvalidOperationException) { return false; }
            }
        }

        private static void Initialize()
        {
            IntPtr current = GetStdHandle(-10);
            if (current == input) return;
            Restore(); input = current;
            native = current != IntPtr.Zero && current != new IntPtr(-1) && GetConsoleMode(current, out originalMode) &&
                SetConsoleMode(current, (originalMode | 0x0098u) & ~0x0240u);
        }

        internal static bool TryRead(out ConsoleInputEvent result)
        {
            result = default; Initialize();
            if (repeats > 0) { repeats--; result = new ConsoleInputEvent(ConsoleInputKind.Key, repeatedKey); return true; }
            if (!native)
            {
                if (!Console.KeyAvailable) return false;
                result = new ConsoleInputEvent(ConsoleInputKind.Key, Console.ReadKey(true)); return true;
            }
            for (int inspected = 0; inspected < 64; inspected++)
            {
                if (!PeekConsoleInput(input, buffer, 1, out uint available) || available == 0) return false;
                if (!ReadConsoleInput(input, buffer, 1, out uint read) || read == 0) return false;
                InputRecord record = buffer[0];
                if (record.Type == 1)
                {
                    ConsoleKey key = (ConsoleKey)record.VirtualKey;
                    // WARNING: ConPTY can synthesize printable '#'/'%' with navigation virtual keys; preserve their Unicode text.
                    if (record.Character != '\0' && !Char.IsControl(record.Character) && key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.Delete or ConsoleKey.Insert or ConsoleKey.PageUp or ConsoleKey.PageDown)
                        key = (ConsoleKey)0;
                    bool arrow = key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.UpArrow or ConsoleKey.DownArrow;
                    if (record.KeyDown == 0)
                    {
                        heldArrows.Remove(key);
                        // WARNING: Windows and ConPTY emit Alt+numpad Unicode text only when Alt is released.
                        if (record.VirtualKey != 18 || record.Character == '\0' || Char.IsControl(record.Character)) continue;
                        key = (ConsoleKey)0;
                    }
                    if (arrow)
                    {
                        bool fresh = heldArrows.Add(key), wasMouse = navigation.Mouse;
                        if (!wasMouse) navigation.Arrow();
                        if (wasMouse && fresh && navigation.Arrow()) { result = new ConsoleInputEvent(ConsoleInputKind.ModeChanged); return true; }
                        if (wasMouse) continue;
                    }
                    uint modifiers = record.KeyModifiers;
                    var info = new ConsoleKeyInfo(record.Character, key, (modifiers & 0x10) != 0, (modifiers & 3) != 0, (modifiers & 12) != 0);
                    repeats = Math.Min(32, Math.Max(0, record.Repeat - 1)); repeatedKey = info;
                    result = new ConsoleInputEvent(ConsoleInputKind.Key, info); return true;
                }
                if (record.Type != 2) continue;
                bool pressed = (record.Buttons & 1) != 0;
                bool leftPressed = pressed && !leftDown; leftDown = pressed;
                int x = record.X - Console.WindowLeft, y = record.Y - Console.WindowTop;
                int margin = ConsoleGraphic.Enabled ? 1 : 0;
                bool inside = x >= margin && y >= margin && x < Console.WindowWidth - margin && y < Console.WindowHeight - margin;
                if (!inside) continue;
                if ((record.MouseFlags == 0 || record.MouseFlags == 2) && leftPressed)
                {
                    if (navigation.Click(true)) { result = new ConsoleInputEvent(ConsoleInputKind.ModeChanged, X: x, Y: y); return true; }
                    if (!navigation.Mouse) continue;
                    result = new ConsoleInputEvent(ConsoleInputKind.Click, X: x, Y: y); return true;
                }
                if (!navigation.Mouse) continue;
                if (record.MouseFlags == 1) { result = new ConsoleInputEvent(ConsoleInputKind.Move, X: x, Y: y); return true; }
                if (record.MouseFlags == 4)
                {
                    navigation.Wheel(); wheelRemainder += (short)(record.Buttons >> 16);
                    if (Math.Abs(wheelRemainder) < 120) continue;
                    int direction = -Math.Sign(wheelRemainder); wheelRemainder %= 120;
                    result = new ConsoleInputEvent(ConsoleInputKind.Wheel, X: x, Y: y, Direction: direction); return true;
                }
            }
            return false;
        }

        internal static bool TryReadKey(out ConsoleKeyInfo key)
        {
            key = default;
            while (TryRead(out var inputEvent))
                if (inputEvent.Kind == ConsoleInputKind.Key) { key = inputEvent.Key; return true; }
            return false;
        }

        internal static ConsoleKeyInfo ReadKey(Func<bool> alive = null)
        {
            while (alive == null || alive())
            {
                if (TryReadKey(out var key)) return key;
                Thread.Sleep(20);
            }
            return new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false);
        }

        internal static string ReadLine(Func<bool> alive = null, int maximum = 4096)
        {
            if (Console.IsInputRedirected) return Console.ReadLine();
            var text = new System.Text.StringBuilder(); int cursor = 0;
            int anchorLeft = Console.CursorLeft, anchorTop = Console.CursorTop, rendered = 0;
            while (alive == null || alive())
            {
                if (!TryReadKey(out ConsoleKeyInfo key)) { Thread.Sleep(20); continue; }
                if (key.Key == ConsoleKey.Escape) return null;
                if (key.Key == ConsoleKey.Enter) { if (!ConsoleGraphic.Enabled) Console.WriteLine(); return text.ToString(); }
                if (key.Key == ConsoleKey.LeftArrow) cursor = Math.Max(0, cursor - 1);
                else if (key.Key == ConsoleKey.RightArrow) cursor = Math.Min(text.Length, cursor + 1);
                else if (key.Key == ConsoleKey.Home) cursor = 0;
                else if (key.Key == ConsoleKey.End) cursor = text.Length;
                else if (key.Key == ConsoleKey.Backspace && cursor > 0) text.Remove(--cursor, 1);
                else if (key.Key == ConsoleKey.Delete && cursor < text.Length) text.Remove(cursor, 1);
                else if (!Char.IsControl(key.KeyChar) && text.Length < maximum) text.Insert(cursor++, key.KeyChar);
                try
                {
                    int width = Math.Min(Console.BufferWidth, Console.WindowWidth), height = Math.Min(Console.BufferHeight, Console.WindowHeight);
                    int margin = ConsoleGraphic.Enabled ? 1 : 0;
                    if (width < 3 || height < 3) continue;
                    int left = Math.Clamp(anchorLeft, margin, width - margin - 2);
                    int bottom = ConsoleGraphic.Enabled ? Math.Min(height - 2, ConsoleGraphic.ContentBottom) : height - 1;
                    int top = Math.Clamp(anchorTop, margin, Math.Max(margin, bottom));
                    int capacity = Math.Max(1, width - margin - left - 1), start = Math.Max(0, cursor - capacity + 1);
                    int length = Math.Min(capacity, text.Length - start);
                    Console.SetCursorPosition(left, top); Console.Write(new string(' ', Math.Min(capacity, Math.Max(rendered, length))));
                    Console.SetCursorPosition(left, top); Console.Write(text.ToString(start, length));
                    Console.SetCursorPosition(left + Math.Min(capacity - 1, cursor - start), top); rendered = length;
                }
                catch (ArgumentOutOfRangeException) { rendered = 0; }
                catch (System.IO.IOException) { rendered = 0; }
            }
            return null;
        }

        internal static void Restore()
        {
            if (native) SetConsoleMode(input, originalMode);
            native = false; input = IntPtr.Zero; repeats = 0; leftDown = false; heldArrows.Clear();
        }
    }
}
