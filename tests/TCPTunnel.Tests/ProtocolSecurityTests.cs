using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using TCPTunnel;

internal static class ProtocolSecurityTests
{
    public static bool SnakeGlyphRangeSelfTest()
    {
        const string prefix = "\u001eTCPTUNNEL|SNAKE|";
        string nick = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("tester"));
        foreach (int glyph in new[] { 65536 + 45, -65536 + 45, int.MaxValue, int.MinValue, 27 })
        {
            if (SnakeProtocol.TryParseClientProfile(prefix + "PROFILE|1|75|10|0|0|" + glyph, out _) ||
                SnakeProtocol.TryParseServerUpdate(prefix + "SET|" + nick + "|75|10|0|0|" + glyph, out _, out _, out _)) return false;
        }
        return SnakeProtocol.TryParseClientProfile(prefix + "PROFILE|1|75|10|0", out var legacy) && legacy.Glyph == '-';
    }

    public static async Task<bool> NicknameReservationSelfTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer1 = new TcpClient();
        using var peer2 = new TcpClient();
        try
        {
            await peer1.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var first = new Client(await listener.AcceptTcpClientAsync());
            await peer2.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var second = new Client(await listener.AcceptTcpClientAsync());
            var broadcaster = new Broadcaster();
            broadcaster.AddConnection(first); broadcaster.AddConnection(second);
            if (!broadcaster.TryReserveNickname(first, "tester") || broadcaster.TryReserveNickname(second, "TESTER")) return false;
            if (broadcaster.AuthenticatedClientCount != 0) return false;
            broadcaster.RemoveClient(first);
            return broadcaster.TryReserveNickname(second, "TESTER") && broadcaster.CompleteAuthentication(second) &&
                broadcaster.AuthenticatedClientCount == 1 && !broadcaster.CompleteAuthentication(first);
        }
        finally { listener.Stop(); }
    }

    public static bool BoundedRemoteStateSelfTest()
    {
        bool enabled = ConsoleGraphic.Enabled;
        var participants = (HashSet<string>)typeof(UserInterface).GetField("activeParticipants", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var update = typeof(UserInterface).GetMethod("UpdateParticipantState", BindingFlags.Static | BindingFlags.NonPublic)!;
        var snakes = (IDictionary)typeof(ConsoleGraphic).GetField("remoteSnakes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            ConsoleGraphic.Enabled = true;
            participants.Clear(); ConsoleGraphic.ClearRemoteSnakes();
            for (int i = 0; i < 512; i++)
            {
                string name = "user" + i;
                update.Invoke(null, new object[] { SystemMessageKind.UserJoined, name });
                ConsoleGraphic.SetRemoteSnake(name, 75, ConsoleColor.Green, 0);
            }
            if (participants.Count != 64 || snakes.Count != 64) return false;
            ConsoleGraphic.SetRemoteSnake("user0", 200, ConsoleColor.Red, 0);
            ConsoleGraphic.RemoveRemoteSnake("user0");
            ConsoleGraphic.SetRemoteSnake("newUser", 75, ConsoleColor.Green, 0);
            return snakes.Count == 64 && snakes.Contains("newUser");
        }
        finally { participants.Clear(); ConsoleGraphic.ClearRemoteSnakes(); ConsoleGraphic.Enabled = enabled; }
    }

    public static async Task<bool> AuthenticationRosterSelfTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer1 = new TcpClient();
        using var peer2 = new TcpClient();
        try
        {
            await peer1.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var first = new Client(await listener.AcceptTcpClientAsync());
            await peer2.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var second = new Client(await listener.AcceptTcpClientAsync());
            var broadcaster = new Broadcaster();
            broadcaster.AddConnection(first); broadcaster.AddConnection(second);
            broadcaster.TryReserveNickname(first, "first"); broadcaster.TryReserveNickname(second, "second");
            broadcaster.CompleteAuthentication(first);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            broadcaster.CompleteAuthenticationWithRoster(second, timeout.Token, out Task roster);
            broadcaster.RemoveClient(first);
            await broadcaster.BroadcastAsync(null, SystemMessageProtocol.Create(SystemMessageKind.UserLeft, "first"), timeout.Token);
            await roster;
            string present = await MessageProtocol.ReadStringAsync(peer2.GetStream(), timeout.Token);
            string leave = await MessageProtocol.ReadStringAsync(peer2.GetStream(), timeout.Token);
            return present == SystemMessageProtocol.Create(SystemMessageKind.ParticipantPresent, "first") &&
                leave == SystemMessageProtocol.Create(SystemMessageKind.UserLeft, "first");
        }
        finally { listener.Stop(); }
    }

    public static bool PendingSnakeLifecycleSelfTest()
    {
        bool enabled = ConsoleGraphic.Enabled;
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var apply = typeof(UserInterface).GetMethod("TryApplySnakeUpdate", flags)!;
        var update = typeof(UserInterface).GetMethod("UpdateParticipantState", flags)!;
        var end = typeof(UserInterface).GetMethod("EndMentionSession", flags)!;
        var pending = (IDictionary)typeof(UserInterface).GetField("pendingParticipantSnakes", flags)!.GetValue(null)!;
        var snakes = (IDictionary)typeof(ConsoleGraphic).GetField("remoteSnakes", flags)!.GetValue(null)!;
        try
        {
            end.Invoke(null, null); ConsoleGraphic.ClearRemoteSnakes(); ConsoleGraphic.Enabled = true;
            var profile = new SnakeProfile { Enabled = true, Color = ConsoleColor.Green, DelayMilliseconds = 75, Glyph = '-' };
            for (int i = 0; i < 512; i++) apply.Invoke(null, new object[] { SnakeProtocol.CreateSet("pending" + i, profile) });
            if (pending.Count != 64 || snakes.Count != 0) return false;
            update.Invoke(null, new object[] { SystemMessageKind.UserJoined, "pending0" });
            if (pending.Count != 63 || !snakes.Contains("pending0")) return false;
            update.Invoke(null, new object[] { SystemMessageKind.UserLeft, "pending0" });
            if (snakes.Contains("pending0")) return false;
            end.Invoke(null, null);
            return pending.Count == 0;
        }
        finally { end.Invoke(null, null); ConsoleGraphic.ClearRemoteSnakes(); ConsoleGraphic.Enabled = enabled; }
    }
}
