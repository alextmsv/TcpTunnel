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
                    Timeout = 0
                };
                FlashWindowEx(ref info);
            }
            catch
            {
            }
        }

        private static IntPtr GetWindowHandle()
        {
            IntPtr window = IntPtr.Zero;
            try { window = GetConsoleWindow(); } catch { }
            if (window != IntPtr.Zero)
                return window;

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
    }
}
