using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TCPTunnel;

internal static class SnakeFrameDiagnostics
{
    private sealed class Trace
    {
        internal readonly Channel<string> Lines = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        internal Task Writer;
        internal long NextSample;
        internal int Samples;
    }
    private sealed record Cell(int Offset, int X, int Y, string NicknameCharacter, string Desired, string Cached, string Actual, int DesiredColor, int ActualColor, bool ReadSucceeded);
    private static Trace current;
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleOutputCharacterW", CharSet = CharSet.Unicode)]
    private static extern bool ReadCharacters(IntPtr output, [Out] char[] text, uint count, Coordinate origin, out uint read);
    [DllImport("kernel32.dll")]
    private static extern bool ReadConsoleOutputAttribute(IntPtr output, ushort[] attributes, uint count, Coordinate origin, out uint read);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [StructLayout(LayoutKind.Sequential)] private struct Coordinate { internal short X, Y; }

    internal static void Start(string path)
    {
        if (current != null) throw new InvalidOperationException("Snake diagnostics is already running.");
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        var output = new StreamWriter(new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        output.WriteLine(JsonSerializer.Serialize(new { Type = "start", Utc = DateTimeOffset.UtcNow, Build = typeof(Program).Assembly.ManifestModule.ModuleVersionId,
            Runtime = Environment.Version.ToString(), WindowsTerminal = !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")), Limit = "300 samples, 10 Hz; border cells only" }));
        output.Flush();
        var trace = new Trace();
        trace.Writer = Task.Run(async () =>
        {
            using (output)
            {
                try { await foreach (string line in trace.Lines.Reader.ReadAllAsync().ConfigureAwait(false)) { await output.WriteLineAsync(line).ConfigureAwait(false); await output.FlushAsync().ConfigureAwait(false); } }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { trace.Lines.Writer.TryComplete(); }
            }
        });
        current = trace;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }
    internal static bool BeginSample()
    {
        Trace trace = current;
        if (trace == null) return false;
        long now = Stopwatch.GetTimestamp();
        if (now < trace.NextSample) return false;
        if (trace.Samples >= 300) { trace.Lines.Writer.TryComplete(); return false; }
        trace.NextSample = now + Math.Max(1, Stopwatch.Frequency / 10); trace.Samples++;
        return true;
    }
    internal static void Capture(string phase, int width, int height, int step, char[] body, char[] desired, char[] cached, ushort[] desiredColors)
    {
        Trace trace = current;
        if (trace == null) return;
        int perimeter = 2 * width + 2 * (height - 2);
        var cells = new Cell[Math.Min(body?.Length ?? 0, perimeter)];
        IntPtr handle = GetStdHandle(-11);
        var character = new char[1]; var attribute = new ushort[1];
        for (int offset = 0; offset < cells.Length; offset++)
        {
            int index = ((step - offset) % perimeter + perimeter) % perimeter;
            var (x, y) = Position(index, width, height);
            character[0] = '\0'; attribute[0] = 0;
            var origin = new Coordinate { X = (short)x, Y = (short)y };
            bool read = ReadCharacters(handle, character, 1, origin, out uint chars) && chars == 1 &&
                ReadConsoleOutputAttribute(handle, attribute, 1, origin, out uint attrs) && attrs == 1;
            cells[offset] = new Cell(offset, x, y, body[body.Length - 1 - offset].ToString(), desired[index].ToString(), cached[index].ToString(),
                read ? character[0].ToString() : "", desiredColors[index] & 15, attribute[0] & 15, read);
        }
        trace.Lines.Writer.TryWrite(JsonSerializer.Serialize(new { Type = "frame", Sample = trace.Samples, Phase = phase, Utc = DateTimeOffset.UtcNow, width, height, step,
            Nickname = NetWorker.nickname, Body = body == null ? "" : new string(body), NicknameEnabled = ApplicationSettings.Current.NicknameSnake,
            Effective = UserInterface.NicknameSnakeEffective, TerminalBackground = ConsoleTheme.HasContentBackground, BatchedBorder = ConsoleGraphic.UseBatchedBorderFrames, Cells = cells }));
    }
    private static (int X, int Y) Position(int index, int width, int height)
    {
        if (index < width) return (index, 0);
        index -= width;
        if (index < height - 1) return (width - 1, index + 1);
        index -= height - 1;
        if (index < width - 1) return (width - 2 - index, height - 1);
        return (0, height - 2 - (index - (width - 1)));
    }
    internal static void Stop()
    {
        Trace trace = current;
        if (trace == null) return;
        trace.Lines.Writer.TryComplete();
        try { trace.Writer.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        current = null;
    }
}
