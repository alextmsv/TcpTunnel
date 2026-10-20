using System;
using System.Text;
using System.Threading;

namespace TCPTunnel
{
    public partial class Menu
    {
        internal static string ReadChatNickname()
        {
            ConsoleGraphic.SetMenuScreen(true);
            new ConsoleGraphic().Clear(0, 0);
            if (ConsoleGraphic.Enabled)
            {
                ConsoleGraphic.WriteCenteredLine(Lang.Get(TextId.EnterNewNickname), ConsoleGraphic.ContentTop + 2, ConsoleColor.Cyan);
                ConsoleGraphic.WriteCenteredLine(Lang.Get(TextId.NicknameRules), ConsoleGraphic.ContentTop + 4, ConsoleColor.DarkGray);
                ConsoleGraphic.TrySetContentCursor(2, ConsoleGraphic.ContentTop + 6);
            }
            else { Console.WriteLine(Lang.Get(TextId.EnterNewNickname)); }
            return ReadPromptLine();
        }
        private static string ReadPromptLine() => ConsoleInput.ReadLine(ChatMenuAlive, 128);

        private static void WaitPromptAcknowledgement()
        {
            if (ChatMenuAlive == null) { ConsoleInput.ReadKey(); return; }
            while (ChatMenuAlive())
            {
                if (ConsoleInput.TryReadKey(out _)) return;
                Thread.Sleep(20);
            }
        }
    }
}
