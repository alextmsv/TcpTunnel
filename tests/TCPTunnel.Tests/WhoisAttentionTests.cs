using TCPTunnel;
using System.Reflection;
using System.Collections;

internal static class WhoisAttentionTests
{
    public static bool WhoisHiddenHistorySelfTest()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        Type ui = typeof(UserInterface);
        Type entryType = ui.GetNestedType("ChatHistoryEntry", BindingFlags.NonPublic)!;
        var history = (IList)ui.GetField("chatHistory", flags)!.GetValue(null)!;
        var summary = (WhoisUnreadSummary)ui.GetField("trimmedWhoisNotices", flags)!.GetValue(null)!;
        object[] saved = history.Cast<object>().ToArray();
        try
        {
            history.Clear(); summary.Drain();
            object entry = Activator.CreateInstance(entryType, new object?[] { "@tester used /whois", null, null, true })!;
            entryType.GetProperty("WhoisAttention")!.SetValue(entry, new WhoisUnreadState(0));
            entryType.GetProperty("WhoisRequesters")!.SetValue(entry, new[] { "tester" });
            history.Add(entry);
            for (int i = 0; i < 10; i++) history.Add(Activator.CreateInstance(entryType, new object?[] { "message", null, null, false })!);
            object[] layout = { 40, 5, 0, 0 };
            ui.GetMethod("FindVisibleHistoryStart", flags)!.Invoke(null, layout);
            int first = (int)layout[2];
            bool hasVisibleUnread = (bool)ui.GetMethod("CollectHiddenWhoisNotices", flags)!.Invoke(null, new object[] { first })!;
            return first > 0 && !hasVisibleUnread && summary.Drain().SequenceEqual(new[] { "tester" }) &&
                !(bool)entryType.GetProperty("WhoisUnread")!.GetValue(entry)!;
        }
        finally { history.Clear(); foreach (object entry in saved) history.Add(entry); summary.Drain(); }
    }

    public static bool WhoisReadVisibilitySelfTest()
    {
        var notice = new WhoisUnreadState(0);
        notice.Observe(false, true, 0, 1000);
        notice.Observe(false, true, 1, 2000);
        notice.Observe(true, false, 1, 3000);
        if (!notice.Unread) return false;
        notice.Observe(true, true, 1, 4000);
        notice.Observe(true, true, 1, 4500);
        if (!notice.Unread) return false;
        notice.Observe(true, true, 1, 4600);
        var terminal = new WhoisUnreadState(1);
        terminal.Observe(true, false, 2, 5000);
        return !notice.Unread && !terminal.Unread;
    }

    public static bool WhoisTrimSummarySelfTest()
    {
        var summary = new WhoisUnreadSummary();
        for (int i = 0; i < 512; i++) summary.Add(new[] { "user" + i }, 4);
        if (summary.Count != 64 || !summary.HasMore) return false;
        for (int i = 0; i < 10; i++)
            if (summary.Take(0).Length != 0 || summary.Count != 64) return false;
        string[] names = summary.Drain();
        return names.Length == 64 && summary.Count == 0 && !summary.HasMore;
    }
}
