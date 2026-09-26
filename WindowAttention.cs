using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TCPTunnel
{
    internal static class WindowAttention
    {
        private const uint FlashStop = 0x00000000;
        private const uint FlashTray = 0x00000002;
        private const uint FlashTimerNoForeground = 0x0000000C;

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        private const uint OwnerWindow = 4;
        private const int MaxOwnerDepth = 8;

        internal static bool IsForeground
        {
            get
            {
                try { IntPtr window = GetWindowHandle(); return window != IntPtr.Zero && window == GetForegroundWindow(); }
                catch { return false; }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FlashInfo info);

        public static bool IsMinimized
        {
            get
            {
                IntPtr window = GetWindowHandle();
                if (window == IntPtr.Zero)
                    return false;

                try { return IsIconic(window); }
                catch { return false; }
            }
        }

        public static void FlashTaskbarUntilForeground()
        {
            Flash(FlashTray | FlashTimerNoForeground, UInt32.MaxValue);
        }

        public static void StopFlashing()
        {
            Flash(FlashStop, 0);
        }

        private static void Flash(uint flags, uint count)
        {
            IntPtr window = GetWindowHandle();
            if (window == IntPtr.Zero)
                return;

            try
            {
                var info = new FlashInfo
                {
                    Size = (uint)Marshal.SizeOf(typeof(FlashInfo)),
                    Window = window,
                    Flags = flags,
                    Count = count,
                    Timeout = 500
                };
                FlashWindowEx(ref info);
            }
            catch
            {
            }
        }

        private static IntPtr GetWindowHandle()
        {
            IntPtr console = IntPtr.Zero;
            try { console = GetConsoleWindow(); } catch { }
            if (console != IntPtr.Zero)
                return FindVisibleOwner(console);

            try
            {
                using (Process process = Process.GetCurrentProcess())
                    return process.MainWindowHandle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        internal static IntPtr FindVisibleOwner(IntPtr console)
        {
            try
            {
                IntPtr window = console;
                for (int depth = 0; window != IntPtr.Zero && depth < MaxOwnerDepth; depth++)
                {
                    if (IsWindowVisible(window))
                        return window;
                    window = GetWindow(window, OwnerWindow);
                }
            }
            catch
            {
            }
            return console;
        }
    }
}
