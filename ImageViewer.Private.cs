#nullable enable
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TCPTunnel.Cryptography;

namespace TCPTunnel;

internal static partial class ImageViewer
{
    private const string PrivateViewerArgument = "--private-image-view";
    private const string PrivateViewerProbeArgument = "--private-image-probe";
    private sealed record ViewerPayload(ImagePacket? Image, AnimatedImagePacket? Animation);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle handle, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle handle, out uint processId);

    private static bool LaunchPrivate(ViewerPayload payload)
    {
        string? executable = Environment.ProcessPath;
        if (String.IsNullOrEmpty(executable)) return false;
        string name = "TCPTunnel-view-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        byte[] bytes = SecureWire.Json(payload);
        if (bytes.Length > SecureWire.MaximumPayload) { pipe.Dispose(); CryptographicOperations.ZeroMemory(bytes); return false; }
        var commandLine = new StringBuilder().Append('"').Append(executable).Append("\" ").Append(PrivateViewerArgument).Append(' ').Append(name).Append(' ').Append(Environment.ProcessId).Append(' ').Append(Lang.Current == AppLanguage.Russian ? "ru" : "en");
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = StartfUseShowWindow, wShowWindow = SwShowMaximized };
        if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateNewConsole, IntPtr.Zero, null, ref startup, out var process))
        { pipe.Dispose(); CryptographicOperations.ZeroMemory(bytes); return false; }
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
        _ = DeliverViewerAsync(pipe, checked((uint)process.dwProcessId), bytes); return true;
    }
    private static async Task DeliverViewerAsync(NamedPipeServerStream pipe, uint child, byte[] bytes)
    {
        using (pipe)
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint connected) || child != connected) throw new UnauthorizedAccessException("Unexpected viewer process.");
                await SecureWire.WriteAsync(pipe, 1, bytes, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException) { }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    private static bool TryRunPrivate(string[] args)
    {
        if (args.Length == 0 || (args[0] != PrivateViewerArgument && args[0] != PrivateViewerProbeArgument)) return false;
        try
        {
            if (args.Length != 4 || !args[1].StartsWith("TCPTunnel-view-", StringComparison.Ordinal) || args[1].Length != "TCPTunnel-view-".Length + 48 || !UInt32.TryParse(args[2], out uint parent)) throw new InvalidDataException("Invalid viewer connection.");
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            pipe.ConnectAsync(deadline.Token).GetAwaiter().GetResult();
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint connected) || connected != parent) throw new UnauthorizedAccessException("Unexpected viewer parent process.");
            var packet = SecureWire.ReadAsync(pipe, deadline.Token).GetAwaiter().GetResult();
            if (packet.Kind != 1) throw new InvalidDataException("Invalid viewer packet.");
            ViewerPayload payload;
            string digest = Convert.ToHexString(SHA256.HashData(packet.Payload));
            try { payload = SecureWire.Parse<ViewerPayload>(packet.Payload); }
            finally { CryptographicOperations.ZeroMemory(packet.Payload); }
            Lang.Set(args[3] == "ru" ? AppLanguage.Russian : AppLanguage.English);
            if (payload.Image != null && payload.Animation == null)
            {
                var image = payload.Image;
                if (image.PackedPixels == null || ImageProtocol.GetPackedLength(image.Width, image.Height) != image.PackedPixels.Length) throw new InvalidDataException("Invalid viewer image.");
                if (args[0] == PrivateViewerProbeArgument) { Console.WriteLine(digest); return true; }
                TryRun(new[] { ViewerArgument, image.Width.ToString(), image.Height.ToString(), Convert.ToBase64String(image.PackedPixels), args[3] });
            }
            else if (payload.Animation != null && payload.Image == null && ImageAnimationProtocol.IsValidPacket(payload.Animation, false))
            { if (args[0] == PrivateViewerProbeArgument) { Console.WriteLine(digest); return true; } Console.Title = "TCPTunnel GIF"; ExpandViewerConsole(payload.Animation.Width, payload.Animation.Height); PlayAnimationInViewer(payload.Animation); }
            else throw new InvalidDataException("Invalid viewer content.");
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        { Console.WriteLine(error.Message); }
        return true;
    }
    internal static async Task<bool> PrivateViewerPipeSelfTest()
    {
        byte[] pixels = new byte[ImageProtocol.GetPackedLength(3, 2)];
        var image = new ImagePacket { Width = 3, Height = 2, PackedPixels = pixels };
        var animation = new AnimatedImagePacket { Width = 3, Height = 2, PackedFrames = new[] { pixels, pixels }, FrameDelays = new ushort[] { 90, 110 } };
        foreach (var payload in new[] { new ViewerPayload(image, null), new ViewerPayload(null, animation) })
        {
            string name = "TCPTunnel-view-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new System.Diagnostics.ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            bool ownProcess = System.Reflection.Assembly.GetEntryAssembly() == typeof(ImageViewer).Assembly;
            string? executable = ownProcess ? Environment.ProcessPath : Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "dotnet.exe");
            if (String.IsNullOrEmpty(executable)) { pipe.Dispose(); return false; }
            start.FileName = executable;
            if (!ownProcess || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TCPTunnel.dll"));
            start.ArgumentList.Add(PrivateViewerProbeArgument); start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add("en");
            using var child = System.Diagnostics.Process.Start(start) ?? throw new IOException("Viewer test process failed to start.");
            byte[] bytes = SecureWire.Json(payload); string expected = Convert.ToHexString(SHA256.HashData(bytes));
            Task delivery = DeliverViewerAsync(pipe, checked((uint)child.Id), bytes);
            try
            {
                string output = await child.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(35)).ConfigureAwait(false);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); await delivery.ConfigureAwait(false);
                if (child.ExitCode != 0 || output.Trim() != expected) { Console.WriteLine("Private viewer: " + output + await child.StandardError.ReadToEndAsync()); return false; }
            }
            finally { if (!child.HasExited) child.Kill(); pipe.Dispose(); }
        }
        return true;
    }
}
