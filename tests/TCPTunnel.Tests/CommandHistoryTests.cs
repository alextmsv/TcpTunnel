using TCPTunnel;

internal static class CommandHistoryTests
{
    internal static bool CommandHistoryNavigationSelfTest()
    {
        var history = new CommandHistory();
        history.Submit("/status");
        history.Submit("ordinary chat message");
        history.Submit(@"\\server\share\image.png");
        history.Submit("/unknown-command");
        history.Submit("/duel @pushkin hard 2m blitz");
        history.Submit("/duel @pushkin hard 2m blitz");
        history.Submit(@"\LANG ru");

        const string draft = "unfinished text";
        if (history.Move(false, draft, 4, out _, out _)) return false;
        if (!history.Move(true, draft, 4, out string text, out int cursor) || text != @"\LANG ru" || cursor != text.Length) return false;
        if (!history.Move(true, text, cursor, out text, out cursor) || text != "/duel @pushkin hard 2m blitz") return false;
        if (!history.Move(true, text, cursor, out text, out cursor) || text != "/status") return false;
        if (history.Move(true, text, cursor, out _, out _)) return false;
        if (!history.Move(false, text, cursor, out text, out cursor) || text != "/duel @pushkin hard 2m blitz") return false;
        if (!history.Move(false, "/duel edited", 3, out text, out cursor) || text != @"\LANG ru") return false;
        if (!history.Move(false, text, cursor, out text, out cursor) || text != draft || cursor != 4) return false;
        history.Clear();
        if (history.Move(true, draft, 4, out _, out _)) return false;

        for (int i = 0; i < 70; i++) history.Submit("/ping example-" + i);
        int recalled = 0;
        while (history.Move(true, text, cursor, out text, out cursor)) recalled++;
        return recalled == 64 && text == "/ping example-6";
    }
}
