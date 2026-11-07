using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class PseudoConsoleTests
{
    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public short Show, ReservedBytes;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { public Startup Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern bool WriteFile(IntPtr handle, byte[] data, uint count, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int kind, IntPtr handle);

    internal static void OpenConsoleOutput()
    {
        IntPtr output = CreateFile("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (output == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        SetStdHandle(-11, output);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupEx startup, out ProcessInfo process);

    public static Task<bool> PseudoConsoleStartupGeometrySelfTest() => RunChild("--geometry-probe", "100,30,100,30");

    public static Task<bool> PseudoConsoleOptionsNavigationSelfTest() => RunChild("--menu-probe", "PASS");
    public static Task<bool> PseudoConsoleChatLayoutSelfTest() => RunChild("--chat-layout-probe", "PASS");
    public static Task<bool> PseudoConsoleSnakeWarningSelfTest() => RunChild("--snake-warning-probe", "PASS");
    public static Task<bool> PseudoConsoleMouseMenuSelfTest() => RunChild("--mouse-menu-probe", "PASS");
    public static Task<bool> PseudoConsoleMatrixChatSelfTest() => RunChild("--matrix-chat-probe", "PASS");
    public static Task<bool> PseudoConsoleHubLimitTypingSelfTest() => RunChild("--hub-limit-typing-probe", "PASS");
    public static Task<bool> PseudoConsolePlaintextSnakeSessionSelfTest() => RunChild("--plaintext-snake-session-probe", "PASS");
    public static Task<bool> PseudoConsoleIndependentMatrixSelfTest() => RunChild("--independent-matrix-probe", "PASS");
    public static Task<bool> PseudoConsoleMenuMatrixSelfTest() => RunChild("--menu-matrix-probe", "PASS");
    public static Task<bool> PseudoConsoleNicknameSnakeCellsSelfTest() => RunChild("--nickname-snake-cells-probe", "PASS");
    public static Task<bool> PseudoConsoleScrollbackSelfTest() => RunChild("--scrollback-probe", "PASS");
    public static Task<bool> PseudoConsoleMenuResizeSelfTest() => RunChild("--resize-menu-probe", "PASS");
    public static Task<bool> PseudoConsoleMainMenuResizeSelfTest() => RunChild("--resize-main-menu-probe", "PASS");
    public static Task<bool> PseudoConsoleMenuExitSelfTest() => RunChild("--exit-menu-probe", "PASS");

    public static async Task<bool> PseudoConsoleMenuAppearanceSelfTest()
    {
        foreach (var size in new[] { (71, 16), (100, 30), (40, 16), (71, 10) })
            if (!await RunChild("--appearance-probe", "PASS", (short)size.Item1, (short)size.Item2)) return false;
        return true;
    }

    private static async Task<bool> RunChild(string mode, string expected, short width = 100, short height = 30)
    {
        string resultPath = Path.Combine(Path.GetTempPath(), "tcptunnel-geometry-" + Guid.NewGuid().ToString("N") + ".txt");
        IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero, outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
        IntPtr console = IntPtr.Zero, attributes = IntPtr.Zero;
        ProcessInfo native = default;
        Task drain = Task.CompletedTask;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
                !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) throw new System.ComponentModel.Win32Exception();
            using var output = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read);
            outputRead = IntPtr.Zero;
            drain = Task.Run(() => { try { output.CopyTo(Stream.Null); } catch (IOException) { } });
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord { X = width, Y = height }, inputRead, outputWrite, 0, out console));
            CloseHandle(inputRead); inputRead = IntPtr.Zero;
            CloseHandle(outputWrite); outputWrite = IntPtr.Zero;
            IntPtr bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(bytes);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes) ||
                !UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20016), console, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception();
            var startup = new StartupEx { Info = new Startup { Size = Marshal.SizeOf<StartupEx>() }, Attributes = attributes };
            string executable = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
            var command = new StringBuilder($"\"{executable}\" {mode} \"{resultPath}\"");
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x80000, IntPtr.Zero, null, ref startup, out native))
                throw new System.ComponentModel.Win32Exception();
            using var process = Process.GetProcessById(native.Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (mode == "--resize-menu-probe" || mode == "--exit-menu-probe" || mode == "--resize-main-menu-probe")
            {
                while (!File.Exists(resultPath + ".ready")) await Task.Delay(20, timeout.Token);
                foreach (var size in new[] { (40, 16), (160, 45), (50, 12), (100, 30) })
                {
                    Marshal.ThrowExceptionForHR(ResizePseudoConsole(console, new Coord { X = (short)size.Item1, Y = (short)size.Item2 }));
                    await Task.Delay(450, timeout.Token);
                }
                WriteFile(inputWrite, new byte[] { 27 }, 1, out _, IntPtr.Zero);
            }
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(); await process.WaitForExitAsync(); throw; }
            ClosePseudoConsole(console); console = IntPtr.Zero;
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            string result = File.ReadAllText(resultPath);
            Console.WriteLine("ConPTY " + mode + ": " + result);
            return process.ExitCode == 0 && result == expected;
        }
        finally
        {
            if (console != IntPtr.Zero) ClosePseudoConsole(console);
            foreach (IntPtr handle in new[] { inputRead, inputWrite, outputRead, outputWrite, native.Process, native.Thread })
                if (handle != IntPtr.Zero) CloseHandle(handle);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (File.Exists(resultPath)) File.Delete(resultPath);
            if (File.Exists(resultPath + ".ready")) File.Delete(resultPath + ".ready");
            if (File.Exists(resultPath + ".stage")) File.Delete(resultPath + ".stage");
        }
    }
}
