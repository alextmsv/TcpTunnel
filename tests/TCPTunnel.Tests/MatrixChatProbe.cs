using System.Collections;
using System.Reflection;
using System.Text;
using TCPTunnel;

internal static class MatrixChatProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Ui = typeof(UserInterface);
    private static void Set(string name, object value) => Ui.GetField(name, Flags)!.SetValue(null, value);
    private static object? Call(string name, params object?[] args) => Ui.GetMethod(name, Flags)!.Invoke(null, args);
    private static string[] Rows() => ((ValueTuple<string[], ushort[]>)typeof(MenuRenderProbe).GetMethod("Capture", Flags)!.Invoke(null, null)!).Item1;
    internal static bool Run()
    {
        PseudoConsoleTests.OpenConsoleOutput(); ConsoleGraphic.Enabled = true; ConsoleGraphic.ConfigureConsole(100, 30);
        NetWorker.nickname = "matrixUser"; NetWorker.connected = true; Lang.Set(AppLanguage.English);
        ApplicationSettings.Current.MatrixText = true; ApplicationSettings.Current.InputPinnedBottom = false;
        object sync = Ui.GetField("consoleLock", Flags)!.GetValue(null)!;
        try
        {
            Call("ResetChatSessionLayout");
            Set("inputActive", true); Set("inputPrompt", "<<< [matrixUser]: ");
            var buffer = (StringBuilder)Ui.GetField("inputBuffer", Flags)!.GetValue(null)!; buffer.Clear().Append("draft-kept"); Set("inputCursorIndex", buffer.Length);
            var animation = new AnimatedImagePacket { Width = 4, Height = 2, PackedFrames = new[] { new byte[4], new byte[4] }, FrameDelays = new ushort[] { 90, 110 }, Sender = "peer" };
            FrozenAnimation frozen = ImageRenderer.Freeze(animation, 98, 20, "peer", false);
            Call("WriteChatAnimation", frozen, null, -1);
            Call("WriteChatLine", ">>> [peer]: @matrixUser hello", null, true, false, null);
            Call("WriteSystemChatLine", "SYSTEM-CHECK");
            Thread.Sleep(950);
            lock (sync)
            {
                string[] rows = Rows();
                if (!rows.Any(row => row.Contains("@matrixUser hello")) || !rows.Any(row => row.Contains("SYSTEM-CHECK")) || !rows.Any(row => row.Contains("draft-kept"))) return false;
                var history = (IEnumerable)Ui.GetField("chatHistory", Flags)!.GetValue(null)!;
                if (history.Cast<object>().Any(entry => entry.GetType().GetProperty("Matrix")!.GetValue(entry) != null)) return false;
            }
            bool modalPreserved = false;
            Call("RunChatModal", (Action)(() => {
                ConsoleGraphic.SetMenuScreen(true); new ConsoleGraphic().Clear(0, 0);
                ConsoleGraphic.WriteCenteredLine("MODAL-CHECK", 5, ConsoleColor.Cyan);
                Call("WriteChatLine", ">>> [peer]: received-during-menu", null, true, false, null);
                Call("WriteChatAnimation", frozen, null, -1);
                lock (sync) modalPreserved = Rows().Any(row => row.Contains("MODAL-CHECK")) && !Rows().Any(row => row.Contains("received-during-menu"));
            }));
            lock (sync) return modalPreserved && Rows().Any(row => row.Contains("received-during-menu")) && Rows().Any(row => row.Contains("draft-kept"));
        }
        finally { NetWorker.connected = false; }
    }
}
