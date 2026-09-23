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
                while (Console.KeyAvailable) Console.ReadKey(true);
                CheckForConsoleResize();
                Thread.Sleep(20);
            }
            while (Console.KeyAvailable) Console.ReadKey(true);
            while (true)
            {
                CheckForConsoleResize();
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Enter)
                    return;
                Thread.Sleep(20);
            }
        }
    }
}
