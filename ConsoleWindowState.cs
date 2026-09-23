using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TCPTunnel
{
    internal readonly record struct WindowSize(int Width, int Height, bool Maximized);

    internal static class ConsoleWindowState
    {
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Placement
        {
            public int Length, Flags, Show;
            public Point MinPosition, MaxPosition;
            public Rect Normal;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor, Work;
            public uint Flags;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
        private const int ExtendedFrameBounds = 9;

        private static bool TryGetVisualRect(IntPtr window, out Rect rect) =>
            DwmGetWindowAttribute(window, ExtendedFrameBounds, out rect, Marshal.SizeOf<Rect>()) == 0 ||
            GetWindowRect(window, out rect);

        private static Timer timer;
        private static int sampling;
        private static WindowSize? pending;

        internal static IntPtr ClassicWindow
        {
            get
            {
                try
                {
                    if (Console.IsOutputRedirected) return IntPtr.Zero;
                    IntPtr window = GetConsoleWindow();
                    if (window == IntPtr.Zero || !IsWindowVisible(window)) return IntPtr.Zero;
                    var name = new StringBuilder(64);
                    return GetClassName(window, name, name.Capacity) != 0 && name.ToString() == "ConsoleWindowClass"
                        ? window : IntPtr.Zero;
                }
                catch { return IntPtr.Zero; }
            }
        }

        internal static bool TryCapture(out WindowSize size)
        {
            size = default;
            IntPtr window = ClassicWindow;
            if (window == IntPtr.Zero) return false;
            IntPtr previousDpi = IntPtr.Zero;
            try
            {
                if (IsIconic(window)) return false;
                previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
                var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
                if (!GetWindowPlacement(window, ref placement)) return false;
                size = new WindowSize(placement.Normal.Right - placement.Normal.Left,
                    placement.Normal.Bottom - placement.Normal.Top, placement.Show == 3);
                return size.Width > 0 && size.Height > 0;
            }
            catch { return false; }
            finally { if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi); }
        }

        private static IntPtr FindHostingWindow()
        {
            string title;
            try { title = Console.Title; } catch { return IntPtr.Zero; }
            if (String.IsNullOrEmpty(title)) return IntPtr.Zero;

            IntPtr match = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window)) return true;
                int length = GetWindowTextLength(window);
                if (length != title.Length) return true;
                var text = new StringBuilder(length + 1);
                GetWindowText(window, text, text.Capacity);
                if (text.ToString() != title) return true;
                match = window;
                return false;
            }, IntPtr.Zero);
            return match;
        }

        internal static (int Width, int Height) CurrentPixelSize()
        {
            IntPtr window = ClassicWindow;
            if (window == IntPtr.Zero) window = FindHostingWindow();
            if (window == IntPtr.Zero) return (0, 0);
            IntPtr previousDpi = IntPtr.Zero;
            try
            {
                if (IsIconic(window)) return (0, 0);
                previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
                return TryGetVisualRect(window, out Rect rect) ? (rect.Right - rect.Left, rect.Bottom - rect.Top) : (0, 0);
            }
            catch { return (0, 0); }
            finally { if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi); }
        }

        internal static bool Restore(AppProfile profile)
        {
            IntPtr window = ClassicWindow;
            if (window == IntPtr.Zero || profile.WindowWidth <= 0 || profile.WindowHeight <= 0) return false;
            IntPtr previousDpi = IntPtr.Zero;
            try
            {
                previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
                var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetWindowPlacement(window, ref placement) ||
                    !GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor)) return false;
                int width = Math.Clamp(profile.WindowWidth, 200, Math.Max(200, monitor.Work.Right - monitor.Work.Left));
                int height = Math.Clamp(profile.WindowHeight, 120, Math.Max(120, monitor.Work.Bottom - monitor.Work.Top));
                int x = Math.Clamp(placement.Normal.Left, monitor.Monitor.Left, monitor.Monitor.Left + monitor.Work.Right - monitor.Work.Left - width);
                int y = Math.Clamp(placement.Normal.Top, monitor.Monitor.Top, monitor.Monitor.Top + monitor.Work.Bottom - monitor.Work.Top - height);
                placement.Normal = new Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
                placement.Flags = 0;
                placement.Show = profile.WindowMaximized ? 3 : 1;
                return SetWindowPlacement(window, ref placement);
            }
            catch { return false; }
            finally { if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi); }
        }

        internal static void StartTracking()
        {
            timer ??= new Timer(_ => Sample(), null, 500, 500);
        }

        private static void Sample()
        {
            if (Interlocked.Exchange(ref sampling, 1) != 0) return;
            try
            {
                if (!TryCapture(out WindowSize size)) { pending = null; return; }
                if (pending == size) ApplicationSettings.RememberWindow(size);
                pending = size;
            }
            finally { Volatile.Write(ref sampling, 0); }
        }

        internal static void StopTracking()
        {
            Timer active = Interlocked.Exchange(ref timer, null);
            if (active != null) active.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (TryCapture(out WindowSize size)) ApplicationSettings.RememberWindow(size);
        }
    }
}
