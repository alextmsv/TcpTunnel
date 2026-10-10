using TCPTunnel;
using System.Net;
using System.Net.Sockets;

internal static class HubStatusTests
{
    public static bool StatusPlainRowsSelfTest()
    {
        var card = new StatusCard(new[] { "Current", "Users: 5" }, new[] { "Own", "Users: 2" });
        using var output = new StringWriter();
        card.WritePlain(output, 79);
        string[] rows = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        return rows.Length == 2 && rows[0].StartsWith("Current") && rows[1].StartsWith("Users: 5") &&
            rows[0].IndexOf("Own", StringComparison.Ordinal) == rows[1].IndexOf("Users: 2", StringComparison.Ordinal);
    }

    public static async Task<bool> StatusIndependentRateLimitSelfTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var client = new Client(await accept);
            for (int i = 0; i < 100; i++) client.TryConsumeControlToken();
            for (int i = 0; i < 20; i++) if (!client.TryConsumeMessageToken()) return false;
            return !client.TryConsumeControlToken();
        }
        finally { listener.Stop(); }
    }

    public static async Task<bool> StatusMissingCapabilitiesSelfTest()
    {
        var session = new HubStatusSession();
        bool sent = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await session.QueryAsync((_, _) => { sent = true; return Task.CompletedTask; }, deadline.Token);
        return result == null && !sent;
    }

    public static async Task<bool> StatusSendCancellationSelfTest()
    {
        var session = new HubStatusSession();
        session.Receive(HubStatusProtocol.Capabilities);
        try
        {
            await session.QueryAsync((_, _) => Task.FromException(new OperationCanceledException()), CancellationToken.None);
            return false;
        }
        catch (IOException) { return true; }
    }

    public static async Task<bool> StatusReplyCorrelationSelfTest()
    {
        var session = new HubStatusSession();
        session.Receive(HubStatusProtocol.Capabilities);
        var result = await session.QueryAsync((frame, _) =>
        {
            HubStatusProtocol.TryParseRequest(frame, out string id);
            session.Receive(HubStatusProtocol.CreateReply(new string('c', 32), "wrong", 63));
            session.Receive(HubStatusProtocol.CreateReply(id, "owner", 2));
            return Task.CompletedTask;
        }, CancellationToken.None);
        return result?.Owner == "owner" && result.Participants == 2;
    }

    public static async Task<bool> HubStatusOverTcpSelfTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var peer = new TcpClient();
        Task loop = Task.CompletedTask;
        try
        {
            var accept = listener.AcceptTcpClientAsync(timeout.Token);
            await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var server = new Client(await accept);
            NetWorker.broadcaster.AddConnection(server);
            loop = NetWorker.ServerClientLoopAsync(server, timeout.Token);
            var stream = peer.GetStream();
            if (await MessageProtocol.ReadStringAsync(stream, timeout.Token) != NetWorker.DO_AUTH_MESSAGE) return false;
            await MessageProtocol.WriteStringAsync(stream, "REPLY:statusTest", timeout.Token);
            if (await MessageProtocol.ReadStringAsync(stream, timeout.Token) != NetWorker.AUTH_OK_MESSAGE) return false;
            await MessageProtocol.ReadStringAsync(stream, timeout.Token);
            await MessageProtocol.WriteStringAsync(stream, HubStatusProtocol.Hello, timeout.Token);
            if (await MessageProtocol.ReadStringAsync(stream, timeout.Token) != HubStatusProtocol.Capabilities) return false;
            string id = new string('b', 32);
            await MessageProtocol.WriteStringAsync(stream, HubStatusProtocol.CreateRequest(id), timeout.Token);
            string reply = await MessageProtocol.ReadStringAsync(stream, timeout.Token);
            return HubStatusProtocol.TryParseReply(reply, out string parsed, out _, out int count) && parsed == id && count == 1;
        }
        finally { peer.Close(); timeout.Cancel(); listener.Stop(); await loop; }
    }

    public static bool HubStatusProtocolSelfTest()
    {
        string id = new string('a', 32);
        string frame = HubStatusProtocol.CreateReply(id, "тестер", 5);
        if (!LegacyEventProtocol.IsControlMessage(frame) ||
            !HubStatusProtocol.TryParseReply(frame, out var parsedId, out var owner, out var count) ||
            parsedId != id || owner != "тестер" || count != 5) return false;
        return !HubStatusProtocol.TryParseReply(frame + "|extra", out _, out _, out _) &&
            !HubStatusProtocol.TryParseReply(frame.Replace("|5", "|-1"), out _, out _, out _) &&
            HubStatusProtocol.TryParseRequest(HubStatusProtocol.CreateRequest(id), out var requestId) && requestId == id;
    }

    public static bool StatusColumnsSelfTest()
    {
        var card = new StatusCard(new[] { "Current Hub", "Users: 5" }, new[] { "Your Hub", "Users: 2" });
        string wide = card.Render(80);
        if (wide.IndexOf("Your Hub", StringComparison.Ordinal) != 42 ||
            wide.IndexOf("Users: 5", StringComparison.Ordinal) != 80) return false;
        string narrow = card.Render(20);
        return narrow.Contains("Current Hub") && narrow.Contains("Your Hub") && !narrow.Contains('\n') &&
            card.Render(80) == wide;
    }
}
