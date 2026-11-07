using System.Collections;
using System.Reflection;
using System.Text;
using TCPTunnel;

internal static class ScrollbackProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Ui = typeof(UserInterface);
    private static object? Call(string name, params object?[] args) => Ui.GetMethod(name, Flags)!.Invoke(null, args);
    private static object? Get(string name) => Ui.GetField(name, Flags)!.GetValue(null);
    private static void Set(string name, object value) => Ui.GetField(name, Flags)!.SetValue(null, value);
    private static string[] Capture() => ((ValueTuple<string[], ushort[]>)typeof(MenuRenderProbe).GetMethod("Capture", Flags)!.Invoke(null, null)!).Item1;
    private static bool Key(ConsoleKey key, bool control = false) => (bool)Call("HandleScrollbackInputLocked", new ConsoleInputEvent(ConsoleInputKind.Key, new ConsoleKeyInfo('\0', key, false, false, control)))!;
    private static void ExpectNoRedraw(ConsoleKey key, bool control = false)
    {
        var original = Console.Out;
        using var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            if (!Key(key, control) || captured.GetStringBuilder().Length != 0)
                throw new Exception("History boundary triggered a redraw");
        }
        finally { Console.SetOut(original); }
    }

    internal static bool Run()
    {
        PseudoConsoleTests.OpenConsoleOutput(); ConsoleGraphic.ConfigureConsole(100, 30); Lang.Set(AppLanguage.English);
        NetWorker.nickname = "scrollUser"; NetWorker.connected = false;
        object gate = Get("consoleLock")!;
        foreach (bool graphical in new[] { true, false })
        lock (gate)
        {
            ConsoleGraphic.Enabled = graphical; ApplicationSettings.Current.MatrixText = false; ApplicationSettings.Current.InputPinnedBottom = false;
            Call("ResetChatSessionLayout");
            Set("inputActive", true); Set("inputPrompt", "<<< [scrollUser]: ");
            var buffer = (StringBuilder)Get("inputBuffer")!; buffer.Append("DRAFT-STAYS"); Set("inputCursorIndex", buffer.Length);
            for (int i = 0; i < 80; i++) Call("AppendChatHistoryLocked", "history-message-" + i.ToString("D3"), null, null, false);
            Call("RedrawChatLayoutLocked", (object?)null);
            if (!Capture().Any(row => row.Contains("history-message-079"))) throw new Exception("Tail missing");
            int bar = ConsoleGraphic.ContentLeft + (int)Call("GetContentWidth")!;
            if (graphical && Capture()[(int)Get("scrollViewportBottomExclusive")! - 1][bar] != '#')
                throw new Exception("Scrollbar thumb did not reach bottom");
            ExpectNoRedraw(ConsoleKey.PageDown);
            if (!Key(ConsoleKey.Home, true) || !Capture()[ConsoleGraphic.ContentTop].Contains("history-message-000"))
                throw new Exception("Ctrl+Home did not reach oldest retained message");
            if (graphical && Capture()[ConsoleGraphic.ContentTop][bar] != '#')
                throw new Exception("Scrollbar thumb did not reach top");
            ExpectNoRedraw(ConsoleKey.PageUp);
            ExpectNoRedraw(ConsoleKey.Home, true);
            Key(ConsoleKey.End, true);
            if (!Key(ConsoleKey.PageUp)) throw new Exception("PageUp unhandled");
            object anchor = Get("scrollAnchorEntry")!;
            if (anchor == null || Capture().Any(row => row.Contains("history-message-079"))) throw new Exception("PageUp stayed at tail");
            var before = Capture(); int viewportTop = (int)Get("scrollViewportTop")!;
            int textEnd = ConsoleGraphic.ContentLeft + (int)Call("GetContentWidth")!;
            string firstRow = before[viewportTop][..textEnd]; int left = Console.CursorLeft, top = Console.CursorTop;
            Call("WriteChatLine", "new-during-scroll", null, false, true, null);
            if (!ReferenceEquals(anchor, Get("scrollAnchorEntry")) || Capture()[viewportTop][..textEnd] != firstRow || (int)Get("scrollUnreadCount")! != 1)
                throw new Exception("A new message moved the history viewport");
            if (buffer.ToString() != "DRAFT-STAYS" || Console.CursorLeft != left || Console.CursorTop != top || !Capture().Any(row => row.Contains("DRAFT-STAYS")))
                throw new Exception("Scrolling modified the editable draft/cursor");
            var navigation = (NavigationMode)typeof(ConsoleInput).GetField("navigation", Flags)!.GetValue(null)!;
            if (!navigation.Mouse) { navigation.Click(true); navigation.Click(true); }
            if ((bool)Call("HandleScrollbackInputLocked", new ConsoleInputEvent(ConsoleInputKind.Wheel, X: ConsoleGraphic.ContentLeft, Y: (int)Get("inputStartRow")!, Direction: -1))!)
                throw new Exception("Wheel over input scrolled history");
            if (!(bool)Call("HandleScrollbackInputLocked", new ConsoleInputEvent(ConsoleInputKind.Wheel, X: ConsoleGraphic.ContentLeft, Y: viewportTop, Direction: -1))! || ReferenceEquals(anchor, Get("scrollAnchorEntry")))
                throw new Exception("Wheel over messages did not scroll");
            navigation.Arrow(); navigation.Arrow();
            if (!Key(ConsoleKey.End, true) || Get("scrollAnchorEntry") != null || (int)Get("scrollUnreadCount")! != 0 || !Capture().Any(row => row.Contains("new-during-scroll")))
                throw new Exception("Ctrl+End did not follow latest messages");
            Key(ConsoleKey.PageUp);
            for (int i = 0; i < 240; i++) Call("AppendChatHistoryLocked", "bounded-message-" + i, null, null, false);
            Call("RedrawChatLayoutLocked", (object?)null);
            var history = ((IEnumerable)Get("chatHistory")!).Cast<object>().ToArray();
            if (history.Length != 200 || !ReferenceEquals(history[0], Get("scrollAnchorEntry")) || !(bool)Get("scrollHistoryTrimmed")!)
                throw new Exception("Trimmed viewport was not clamped to the oldest retained entry");
            if (!Capture().Any(row => row.Contains("Earlier history discarded"))) throw new Exception("Missing trim notice");
            Call("ClearChatLocally");
            if (Get("scrollAnchorEntry") != null || ((IEnumerable)Get("chatHistory")!).Cast<object>().Any()) throw new Exception("Clear did not reset scroll state");
            TestReflowAndAnimation(graphical);
        }
        return true;
    }

    private static void TestReflowAndAnimation(bool graphical)
    {
        Call("ResetChatSessionLayout"); Set("inputActive", true); Set("inputPrompt", "draft: ");
        var buffer = (StringBuilder)Get("inputBuffer")!; buffer.Append("KEEP"); Set("inputCursorIndex", buffer.Length);
        object entry = Call("AppendChatHistoryLocked", String.Concat(Enumerable.Range(0, 1000).Select(i => "line-" + i.ToString("D3") + " ")), null, null, false)!;
        Set("scrollAnchorEntry", entry); Set("scrollAnchorRow", 3); Set("scrollAnchorWidth", (int)Call("GetContentWidth")! * 2);
        int expected = 6;
        Call("RedrawChatLayoutLocked", (object?)null);
        if ((int)Get("scrollAnchorRow")! != expected) throw new Exception("Anchor did not reflow");
        var animation = new FrozenAnimation
        {
            Width = 12, Height = 3, ToneMaps = new[] { new byte[16], new byte[16] }, PackedFrames = new[] { new byte[18], new byte[18] },
            FrameEndMilliseconds = new[] { 90, 200 }, FrameDelays = new ushort[] { 90, 110 }, DurationMilliseconds = 200, Sender = "peer"
        };
        object animationEntry = Call("AppendAnimationHistoryLocked", animation)!;
        object matrixEntry = Call("AppendChatHistoryLocked", ">>> [peer]: offscreen animated text", null, null, false)!;
        matrixEntry.GetType().GetProperty("Matrix")!.SetValue(matrixEntry, new MatrixTextEffect(">>> [peer]: offscreen animated text", 100));
        Call("RedrawChatLayoutLocked", (object?)null);
        if ((int)animationEntry.GetType().GetProperty("AnimationTop")!.GetValue(animationEntry)! >= 0)
            throw new Exception("Offscreen GIF retained a paint target");
        if (((IEnumerable)matrixEntry.GetType().GetProperty("MatrixFragments")!.GetValue(matrixEntry)!).Cast<object>().Any())
            throw new Exception("Offscreen Matrix retained a paint target");
        Key(ConsoleKey.End, true);
        Key(ConsoleKey.PageUp);
        if (buffer.ToString() != "KEEP") throw new Exception("Media scroll lost draft");
    }
}
