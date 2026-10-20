using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using TCPTunnel;

internal static class IndependentMatrixProbe
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Ui = typeof(UserInterface);
    private static object? Call(string name, params object?[] args) => Ui.GetMethod(name, Flags)!.Invoke(null, args);
    private static void Set(string name, object value) => Ui.GetField(name, Flags)!.SetValue(null, value);
    internal static bool Run()
    {
        PseudoConsoleTests.OpenConsoleOutput(); ConsoleGraphic.ConfigureConsole(100, 30); NetWorker.nickname = "matrixUser"; Lang.Set(AppLanguage.English);
        object gate = Ui.GetField("consoleLock", Flags)!.GetValue(null)!;
        try
        {
            foreach (bool graphical in new[] { true, false })
            {
                NetWorker.connected = false; ConsoleGraphic.Enabled = graphical; ApplicationSettings.Current.MatrixText = true; ApplicationSettings.Current.InputPinnedBottom = false;
                Call("ResetChatSessionLayout");
                Set("inputActive", true); Set("inputPrompt", "<<< [matrixUser]: ");
                var buffer = (StringBuilder)Ui.GetField("inputBuffer", Flags)!.GetValue(null)!; buffer.Clear().Append("DRAFT-STAYS"); Set("inputCursorIndex", buffer.Length);
                NetWorker.connected = true; ApplicationSettings.Current.MatrixCharacterDelay = 100;
                Call("WriteChatLine", ">>> [peer]: первое сообщение дописывается", null, true, false, null);
                Thread.Sleep(150); ApplicationSettings.Current.MatrixCharacterDelay = 200;
                Call("WriteChatLine", ">>> [peer]: второе сообщение одновременно", null, true, false, null);
                int version; int left, top; object[] entries;
                lock (gate)
                {
                    entries = ((IEnumerable)Ui.GetField("chatHistory", Flags)!.GetValue(null)!).Cast<object>().ToArray();
                    if (entries.Length != 2 || entries.Any(entry => entry.GetType().GetProperty("Matrix")!.GetValue(entry) is not MatrixTextEffect { Active: true })) return false;
                    version = (int)Ui.GetField("matrixLayoutVersion", Flags)!.GetValue(null)!; left = Console.CursorLeft; top = Console.CursorTop;
                }
                Thread.Sleep(250);
                lock (gate)
                {
                    if (version != (int)Ui.GetField("matrixLayoutVersion", Flags)!.GetValue(null)! || Console.CursorLeft != left || Console.CursorTop != top) return false;
                    if (entries.Any(entry => entry.GetType().GetProperty("Matrix")!.GetValue(entry) is not MatrixTextEffect { Active: true })) return false;
                    var rows = ((ValueTuple<string[], ushort[]>)typeof(MenuRenderProbe).GetMethod("Capture", Flags)!.Invoke(null, null)!).Item1;
                    if (!rows.Any(row => row.Contains("DRAFT-STAYS")) || !rows.Any(row => row.Contains("пер")) || !rows.Any(row => row.Contains("в"))) return false;
                    buffer.Append(new string('x', 260)); Set("inputCursorIndex", buffer.Length);
                    Call("RedrawInputAreaLocked");
                    version = (int)Ui.GetField("matrixLayoutVersion", Flags)!.GetValue(null)!;
                    rows = ((ValueTuple<string[], ushort[]>)typeof(MenuRenderProbe).GetMethod("Capture", Flags)!.Invoke(null, null)!).Item1;
                    if (!rows.Any(row => row.Contains("DRAFT-STAYS"))) return false;
                    foreach (var entry in entries) entry.GetType().GetProperty("MatrixStarted")!.SetValue(entry, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 8);
                }
                Thread.Sleep(100);
                lock (gate)
                {
                    if (entries.Any(entry => entry.GetType().GetProperty("Matrix")!.GetValue(entry) != null)) return false;
                    if (version != (int)Ui.GetField("matrixLayoutVersion", Flags)!.GetValue(null)!) return false;
                }
                NetWorker.connected = false; Thread.Sleep(120);
            }
            return true;
        }
        finally { NetWorker.connected = false; }
    }
}
