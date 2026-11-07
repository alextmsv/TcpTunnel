using System.Reflection;
using System.Text;
using TCPTunnel;

internal static class ChatLayoutProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Ui = typeof(UserInterface);
    private static void Set(string field, object value) => Ui.GetField(field, Flags)!.SetValue(null, value);
    private static object? Call(string method, params object?[] args) => Ui.GetMethod(method, Flags)!.Invoke(null, args);
    private static string[] Rows() => ((ValueTuple<string[], ushort[]>)typeof(MenuRenderProbe).GetMethod("Capture", Flags)!.Invoke(null, null)!).Item1;

    private static void ExpectBufferedUpdate(Action update)
    {
        lock (ConsoleGraphic.borderAnimationLock)
        {
            var original = Console.Out;
            using var writes = new StringWriter();
            try
            {
                Console.SetOut(writes);
                update();
                if (writes.GetStringBuilder().Length != 0)
                    throw new Exception("Graphical chat emitted intermediate console writes");
            }
            finally { Console.SetOut(original); }
        }
    }

    internal static bool Run()
    {
        PseudoConsoleTests.OpenConsoleOutput();
        Lang.Set(AppLanguage.English); NetWorker.nickname = "layoutUser";
        ConsoleGraphic.Enabled = true;
        ConsoleGraphic.ConfigureConsole(100, 30);
        ApplicationSettings.Current.InputPinnedBottom = false;
        Call("ResetChatSessionLayout");
        Set("inputActive", true); Set("inputPrompt", "<<< [layoutUser]: ");
        var buffer = (StringBuilder)Ui.GetField("inputBuffer", Flags)!.GetValue(null)!;
        buffer.Clear().Append("draft"); Set("inputCursorIndex", 5);
        Call("WriteChatLine", ">>> [peer]: hello", null, false, false, null);
        string[] flow = Rows();
        int row = (int)Ui.GetField("inputStartRow", Flags)!.GetValue(null)!;
        if (row != ConsoleGraphic.ContentTop + 1 || !flow[row].Contains("draft") || !flow[row - 1].Contains("hello")) return false;
        ApplicationSettings.Current.InputPinnedBottom = true;
        Call("RedrawChatLayoutLocked", (object?)null);
        int bottom = (int)Ui.GetField("inputStartRow", Flags)!.GetValue(null)!;
        if (bottom != ConsoleGraphic.ContentBottom || !Rows()[bottom].Contains("draft")) return false;
        ApplicationSettings.Current.InputPinnedBottom = false;
        Call("RedrawChatLayoutLocked", (object?)null);
        if (Rows()[bottom].Contains("draft")) return false;

        for (int i = 1; i <= 45; i++) Call("WriteChatLine", ">>> [peer]: frame-" + i, null, false, false, null);
        ExpectBufferedUpdate(() => Call("WriteChatLine", ">>> [peer]: buffered-arrival", null, false, false, null));
        ExpectBufferedUpdate(() => Call("WriteChatLine", "<<< [layoutUser]: buffered-send", null, false, false, null));
        if (!Rows().Any(line => line.Contains("buffered-arrival")) || !Rows().Any(line => line.Contains("draft"))) return false;
        buffer.Clear().Append('/'); Set("inputCursorIndex", 1);
        Call("UpdateMentionSuggestionsLocked");
        ExpectBufferedUpdate(() => Call("RedrawInputAreaLocked"));
        int popupStart = (int)Ui.GetField("inputStartRow", Flags)!.GetValue(null)!;
        if ((int)Ui.GetField("renderedInputRows", Flags)!.GetValue(null)! < 3 ||
            Console.CursorTop != popupStart || Console.CursorLeft != ConsoleGraphic.ContentLeft + "<<< [layoutUser]: /".Length) return false;
        Call("ClearMentionState");
        ExpectBufferedUpdate(() => Call("RedrawInputAreaLocked"));
        if (Rows().Any(line => line.Contains("/help —")) || !Rows().Any(line => line.Contains("buffered-send"))) return false;

        ConsoleGraphic.Enabled = false; ApplicationSettings.Current.InputPinnedBottom = true;
        Call("ResetChatSessionLayout"); Set("inputActive", true); Set("inputPrompt", "<<< [layoutUser]: ");
        buffer.Clear().Append("draft"); Set("inputCursorIndex", 5);
        for (int i = 1; i <= 45; i++) Call("WriteChatLine", ">>> [peer]: message-" + i, null, false, false, null);
        buffer.Clear().Append('/'); Set("inputCursorIndex", 1);
        Call("UpdateMentionSuggestionsLocked"); Call("RedrawInputAreaLocked");
        if (!Rows().Any(line => line.Contains("message-45"))) return false;
        Call("ClearMentionState"); Call("RedrawInputAreaLocked");
        string[] closed = Rows();
        return closed.Count(line => line.Contains("message-45")) == 1 && !closed.Any(line => line.Contains("/help —"));
    }
}
