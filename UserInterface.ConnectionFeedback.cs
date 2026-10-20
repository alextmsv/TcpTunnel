using System;

namespace TCPTunnel;

public partial class UserInterface
{
    private static void ShowConnectionFailure(string reason)
    {
        if (Console.IsInputRedirected) { ConsoleGraphic.WriteContentLine(reason); return; }
        ClearConnectionInput();
        new Menu().ShowConnectionFeedback(SanitizeForConsole(reason), false);
    }
    private static bool ConfirmPlaintextOnce()
    {
        if (Console.IsInputRedirected) return false;
        ClearConnectionInput();
        return new Menu().ShowConnectionFeedback(Lang.Get(TextId.ConnectionPinnedWarning), true);
    }
    private static void ClearConnectionInput()
    {
        do { while (ConsoleInput.TryReadKey(out _)) { } if ((GetAsyncKeyState(0x0D) & 0x8000) == 0) break; System.Threading.Thread.Sleep(20); } while (true);
    }
}
