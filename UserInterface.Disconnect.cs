using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TCPTunnel
{
    public partial class UserInterface
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private static void AcknowledgeDisconnect(string reason)
        {
            WriteSystemChatLine(reason);
            WriteSystemChatLine(Lang.Get(TextId.DisconnectReturn));
            if (Console.IsInputRedirected) return;

            while ((GetAsyncKeyState(0x0D) & 0x8000) != 0)
            {
                while (ConsoleInput.TryReadKey(out _)) { }
                CheckForConsoleResize();
                Thread.Sleep(20);
            }
            while (ConsoleInput.TryReadKey(out _)) { }
            while (true)
            {
                CheckForConsoleResize();
                if (ConsoleInput.TryReadKey(out var key) && key.Key == ConsoleKey.Enter)
                    return;
                Thread.Sleep(20);
            }
        }
    }
}
