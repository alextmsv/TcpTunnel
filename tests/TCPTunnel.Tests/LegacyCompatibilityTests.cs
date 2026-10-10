using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using TCPTunnel;

internal static class LegacyCompatibilityTests
{
    internal static async Task<bool> Run(string assemblyPath)
    {
        var context = new AssemblyLoadContext("TCPTunnel-baseline", isCollectible: true);
        try
        {
            var legacy = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            bool server = await NewClientOldServer(legacy);
            bool receiver = await OldReceiverNewFrames(legacy);
            Console.WriteLine($"{(server ? "PASS" : "FAIL")} new client / baseline server: HELLO ignored, chat preserved");
            Console.WriteLine($"{(receiver ? "PASS" : "FAIL")} baseline receiver / new frames: controls hidden, chat preserved");
            return server && receiver;
        }
        finally { context.Unload(); }
    }

    private static async Task<bool> NewClientOldServer(Assembly legacy)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Type worker = legacy.GetType("TCPTunnel.NetWorker", true)!;
        Type clientType = legacy.GetType("TCPTunnel.Client", true)!;
        object broadcaster = worker.GetField("broadcaster")!.GetValue(null)!;
        var clients = new List<IDisposable>();
        var loops = new List<Task>();
        try
        {
            async Task<TcpClient> Connect(string nick)
            {
                var tcp = new TcpClient();
                clients.Add(tcp);
                await tcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
                var server = (IDisposable)Activator.CreateInstance(clientType, await listener.AcceptTcpClientAsync(timeout.Token))!;
                clients.Add(server);
                broadcaster.GetType().GetMethod("AddConnection")!.Invoke(broadcaster, new object[] { server });
                loops.Add((Task)worker.GetMethod("ServerClientLoopAsync")!.Invoke(null, new object[] { server, timeout.Token })!);
                var stream = tcp.GetStream();
                if (await MessageProtocol.ReadStringAsync(stream, timeout.Token) != NetWorker.DO_AUTH_MESSAGE) throw new IOException("Missing challenge");
                await MessageProtocol.WriteStringAsync(stream, "REPLY:" + nick, timeout.Token);
                if (await MessageProtocol.ReadStringAsync(stream, timeout.Token) != NetWorker.AUTH_OK_MESSAGE) throw new IOException("Authentication failed");
                return tcp;
            }
            var observer = await Connect("legacyObserver");
            var current = await Connect("currentPeer");
            await MessageProtocol.WriteStringAsync(current.GetStream(), HubStatusProtocol.Hello, timeout.Token);
            await MessageProtocol.WriteStringAsync(current.GetStream(), WhoisProtocol.Hello, timeout.Token);
            await MessageProtocol.WriteStringAsync(current.GetStream(), "compatibility marker", timeout.Token);
            while (true)
            {
                string frame = await MessageProtocol.ReadStringAsync(observer.GetStream(), timeout.Token);
                if (frame.Contains("TCPTUNNEL|EVENT|", StringComparison.Ordinal)) return false;
                if (frame == "[currentPeer]: compatibility marker") break;
            }
            using var queryTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            bool sent = false;
            var status = new HubStatusSession();
            var unavailable = await status.QueryAsync((_, _) => { sent = true; return Task.CompletedTask; }, queryTimeout.Token);
            return unavailable is null && !sent;
        }
        finally
        {
            timeout.Cancel();
            foreach (var client in clients) client.Dispose();
            listener.Stop();
            await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<bool> OldReceiverNewFrames(Assembly legacy)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var tcp = new TcpClient();
        TextWriter original = Console.Out;
        using var captured = new StringWriter();
        Task? receive = null;
        try
        {
            await tcp.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var server = new Client(await listener.AcceptTcpClientAsync(timeout.Token));
            legacy.GetType("TCPTunnel.ConsoleGraphic", true)!.GetProperty("Enabled")!.SetValue(null, false);
            Type ui = legacy.GetType("TCPTunnel.UserInterface", true)!;
            Console.SetOut(TextWriter.Synchronized(captured));
            receive = (Task)ui.GetMethod("ReceiveMessagesAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { tcp, tcp.GetStream(), timeout.Token })!;
            await server.SendAsync(HubStatusProtocol.Capabilities, timeout.Token);
            await server.SendAsync(WhoisProtocol.Capabilities, timeout.Token);
            await server.SendAsync(WhoisProtocol.Notice("testCaller"), timeout.Token);
            await server.SendAsync("[currentPeer]: compatibility marker", timeout.Token);
            server.Close();
            await receive.WaitAsync(timeout.Token);
            var history = (System.Collections.IEnumerable)ui.GetField("chatHistory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            string output = string.Join("\n", history.Cast<object>().Select(entry => (string)entry.GetType().GetProperty("Text")!.GetValue(entry)!));
            return output.Contains("[currentPeer]: compatibility marker", StringComparison.Ordinal) &&
                !output.Contains("TCPTUNNEL|EVENT|", StringComparison.Ordinal) &&
                !output.Contains("testCaller", StringComparison.Ordinal);
        }
        finally
        {
            timeout.Cancel(); tcp.Close(); listener.Stop();
            try { if (receive != null) await receive.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { Console.SetOut(original); }
        }
    }
}
