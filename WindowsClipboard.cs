using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TCPTunnel
{
    internal static class WindowsClipboard
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);

        internal static bool TryCopy(string text, out string error)
        {
            error = null; IntPtr memory = IntPtr.Zero; bool opened = false;
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows clipboard is required.");
                if (text == null || text.Length > 4096 || text.IndexOf('\0') >= 0) throw new ArgumentException("Invalid clipboard text.");
                IntPtr window = GetConsoleWindow();
                if (window == IntPtr.Zero) throw new InvalidOperationException("A console window is unavailable.");
                byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                memory = GlobalAlloc(0x0002, (nuint)bytes.Length);
                if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr data = GlobalLock(memory);
                if (data == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { Marshal.Copy(bytes, 0, data, bytes.Length); }
                finally { GlobalUnlock(memory); }
                for (int attempt = 0; attempt < 10 && !opened; attempt++)
                { opened = OpenClipboard(window); if (!opened) Thread.Sleep(15); }
                if (!opened || !EmptyClipboard() || SetClipboardData(13, memory) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                // WARNING: Windows owns the buffer after SetClipboardData succeeds; freeing it would invalidate the clipboard.
                memory = IntPtr.Zero; return true;
            }
            catch (Exception failure) when (failure is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
            { error = failure.Message; return false; }
            finally { if (opened) CloseClipboard(); if (memory != IntPtr.Zero) GlobalFree(memory); }
        }
    }
}
