using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using TCPTunnel;

internal static class PlaintextSnakeSessionProbe
{
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int id, IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleInputW")]
    private static extern bool WriteConsoleInput(IntPtr handle, ConsoleInput.InputRecord[] records, uint count, out uint written);
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static void Feed(IntPtr input, string text)
    {
        var records = text.Select(character => new ConsoleInput.InputRecord { Type = 1, KeyDown = 1, Repeat = 1, VirtualKey = character == '\r' ? (ushort)ConsoleKey.Enter : (ushort)0, Character = character }).ToArray();
        if (!WriteConsoleInput(input, records, (uint)records.Length, out _)) throw new Exception("Input injection failed.");
    }
    private static void Key(IntPtr input, ConsoleKey key, char text = '\0')
    {
        var records = new[] { new ConsoleInput.InputRecord { Type = 1, KeyDown = 1, Repeat = 1, VirtualKey = (ushort)key, Character = text }, new ConsoleInput.InputRecord { Type = 1, KeyDown = 0, VirtualKey = (ushort)key } };
        if (!WriteConsoleInput(input, records, 2, out _)) throw new Exception("Input injection failed.");
    }
    private static async Task WaitPrivacyModal(CancellationToken token)
    {
        while (true)
        {
            var rows = ((ValueTuple<string[], ushort[]>)typeof(MenuRenderProbe).GetMethod("Capture", Flags)!.Invoke(null, null)!).Item1;
            if (rows.Any(row => row.Contains("Your IP address will be disclosed") || row.Contains("The Hub did not report its IP policy"))) return;
            await Task.Delay(20, token);
        }
    }
    internal static bool Run(string resultPath)
    {
        PseudoConsoleTests.OpenConsoleOutput(); ConsoleGraphic.Enabled = true; ConsoleGraphic.ConfigureConsole(100, 30); Lang.Set(AppLanguage.English);
        Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", Path.Combine(Path.GetTempPath(), "TCPTunnel-plain-snake-" + Guid.NewGuid().ToString("N")));
        using var input = new FileStream("CONIN$", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); IntPtr handle = input.SafeFileHandle.DangerousGetHandle(); SetStdHandle(-10, handle);
        NetWorker.nickname = "wdg"; ApplicationSettings.Current.NicknameSnake = true; ConsoleGraphic.BorderAnimationDelayMilliseconds = 35;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using (var deniedSocket = new TcpClient())
        {
            deniedSocket.Connect((IPEndPoint)listener.LocalEndpoint);
            using var deniedClient = new Client(listener.AcceptTcpClient()); NetWorker.broadcaster.AddConnection(deniedClient);
            Task deniedServer = NetWorker.ServerClientLoopAsync(deniedClient, deadline.Token);
            try { typeof(UserInterface).GetMethod("RunClientWithSecurity", Flags)!.Invoke(null, new object[] { deniedSocket, "127.0.0.1", ConnectionSecurityMode.RequireE2E, null! }); return false; }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
            deniedServer.GetAwaiter().GetResult();
            if (deniedClient.IsAuthenticated || deniedClient.Nickname != null) return false;
        }
        using (var cancelledSocket = new TcpClient())
        {
            cancelledSocket.Connect((IPEndPoint)listener.LocalEndpoint);
            using var cancelledClient = new Client(listener.AcceptTcpClient()); NetWorker.broadcaster.AddConnection(cancelledClient);
            Task cancelledServer = NetWorker.ServerClientLoopAsync(cancelledClient, deadline.Token);
            Task cancelDriver = Task.Run(async () => { await WaitPrivacyModal(deadline.Token); Key(handle, ConsoleKey.Enter, '\r'); });
            typeof(UserInterface).GetMethod("RunClientWithSecurity", Flags)!.Invoke(null, new object[] { cancelledSocket, "127.0.0.1", ConnectionSecurityMode.AllowPlaintext, null! });
            cancelDriver.GetAwaiter().GetResult(); cancelledServer.GetAwaiter().GetResult();
            if (NetWorker.connected || (cancelledClient.TryGetSnakeProfile(out var cancelledProfile) && cancelledProfile.Nickname)) throw new Exception("Default privacy cancellation entered the chat");
        }
        using var socket = new TcpClient(); socket.Connect((IPEndPoint)listener.LocalEndpoint);
        using var client = new Client(listener.AcceptTcpClient()); NetWorker.broadcaster.AddConnection(client);
        Task server = NetWorker.ServerClientLoopAsync(client, deadline.Token);
        bool active = false, restored = false;
        Task driver = Task.Run(async () => {
            try
            {
                File.WriteAllText(resultPath + ".stage", "Waiting for connected nickname profile");
                await WaitPrivacyModal(deadline.Token);
                Key(handle, ConsoleKey.DownArrow); Key(handle, ConsoleKey.Enter, '\r');
                while (!NetWorker.connected || !client.TryGetSnakeProfile(out var profile) || !profile.Nickname) await Task.Delay(20, deadline.Token);
                File.WriteAllText(resultPath + ".stage", "Profile negotiated");
                await Task.Delay(120, deadline.Token);
                int before = ConsoleGraphic.CurrentBorderSnakeStep;
                await Task.Delay(180, deadline.Token);
                int after = ConsoleGraphic.CurrentBorderSnakeStep;
                bool drawn = (bool)typeof(ConsoleGraphic).GetField("borderIsDrawn", Flags)!.GetValue(null)!;
                int signature = (int)typeof(ConsoleGraphic).GetField("signatureLength", Flags)!.GetValue(null)!;
                active = drawn && before != after && signature == 0 && ApplicationSettings.Current.NicknameSnake && !ConsoleGraphic.BorderSnakePaused;
                File.WriteAllText(resultPath + ".stage", $"drawn={drawn}, steps={before}/{after}, signature={signature}");
                typeof(ConsoleGraphic).GetMethod("StopBorderAnimation", Flags)!.Invoke(null, null);
                ConsoleGraphic.EnsureBorderAnimationRunning();
                before = ConsoleGraphic.CurrentBorderSnakeStep;
                await Task.Delay(180, deadline.Token);
                restored = ConsoleGraphic.CurrentBorderSnakeStep != before;
                ConsoleGraphic.InvalidateVisualTheme();
                await Task.Delay(300, deadline.Token);
                restored &= (bool)typeof(ConsoleGraphic).GetField("borderIsDrawn", Flags)!.GetValue(null)!;
            }
            catch (OperationCanceledException) { throw new Exception("Negotiation: connected=" + NetWorker.connected + ", ready=" + client.IsReady + ", supported=" + client.SupportsNicknameSnake + ", profile=" + (client.TryGetSnakeProfile(out var profile) ? profile.Nickname.ToString() : "missing")); }
            finally { Feed(handle, "/exit\r\r"); }
        });
        try
        {
            typeof(UserInterface).GetMethod("RunClientWithSecurity", Flags)!.Invoke(null, new object[] { socket, "127.0.0.1", ConnectionSecurityMode.AllowPlaintext, null! });
            driver.GetAwaiter().GetResult();
            return active && restored;
        }
        finally { deadline.Cancel(); socket.Close(); server.GetAwaiter().GetResult(); }
    }
}
