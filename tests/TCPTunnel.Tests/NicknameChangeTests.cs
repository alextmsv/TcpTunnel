using System.Net;
using System.Net.Sockets;
using TCPTunnel;
using TCPTunnel.Cryptography;

internal static class NicknameChangeTests
{
    public static bool NicknameProtocolInjectionSelfTest()
    {
        string id = Guid.NewGuid().ToString("N");
        var change = new NicknameChange("firstName", "lastName", "firstName");
        if (!NicknameProtocol.TryRequest(NicknameProtocol.Request(id, "lastName"), out var parsedId, out var name) || parsedId != id || name != "lastName") return false;
        if (!NicknameProtocol.TryChanged(NicknameProtocol.Changed(change), out var decoded) || decoded != change) return false;
        if (!NicknameProtocol.TryReply(NicknameProtocol.Reply(id, "OK", change), out var reply) || reply!.Change != change) return false;
        foreach (string bad in new[] { "ab\u202Ec", "abc|GET", "abc\n", "..", new string('x', 21) })
            if (NicknameProtocol.TryRequest(NicknameProtocol.Request(id, bad), out _, out _)) return false;
        return !NicknameProtocol.TryRequest(NicknameProtocol.Request(id, "lastName") + "|OWNER", out _, out _) &&
            !NicknameProtocol.TryChanged(NicknameProtocol.Changed(change) + "|payload", out _) &&
            NicknameProtocol.DisplayName("lastName", "firstName") == "@lastName (a.k.a. firstName)";
    }

    public static bool NicknameAliasesAndRevocationSelfTest()
    {
        using var owner = new NativeMlsSession(); using var identity = new NativeMlsSession(); using var impostor = new NativeMlsSession();
        var gate = new RoomIdentityGate(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), owner);
        gate.Approve("firstName", identity.Fingerprint);
        for (int i = 1; i < 8; i++) gate.Rename(i == 1 ? "firstName" : "aliasName" + (i - 1), "aliasName" + i, identity.Fingerprint);
        try { gate.Rename("aliasName7", "overflowName", identity.Fingerprint); return false; } catch (InvalidOperationException) { }
        try { gate.Approve("firstName", impostor.Fingerprint); return false; } catch (InvalidOperationException) { }
        gate.Revoke("aliasName7");
        foreach (string alias in new[] { "firstName", "aliasName1", "aliasName7" })
        {
            try { gate.IsApproved(alias, identity.Fingerprint); return false; } catch (UnauthorizedAccessException) { }
        }
        return true;
    }

    public static bool TrustSnapshotConcurrencySelfTest()
    {
        using var owner = new NativeMlsSession(); using var peer = new NativeMlsSession();
        byte[] room = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-trust-race-" + Guid.NewGuid().ToString("N"));
        var first = new RoomTrustStore(directory); var second = new RoomTrustStore(directory);
        first.Load(room, owner.Fingerprint); second.Load(room, owner.Fingerprint);
        first.Save(room, owner.Fingerprint, new[] { new RoomIdentityBinding("firstName", peer.Fingerprint, true) });
        try { second.Save(room, owner.Fingerprint, new[] { new RoomIdentityBinding("nextName", peer.Fingerprint, true) }); return false; }
        catch (IOException) { }
        var loaded = second.Load(room, owner.Fingerprint);
        return loaded.Length == 1 && loaded[0].Nickname == "firstName";
    }

    public static bool AliasSaveFailureRollbackSelfTest()
    {
        using var owner = new NativeMlsSession(); using var peer = new NativeMlsSession();
        byte[] room = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-alias-rollback-" + Guid.NewGuid().ToString("N"));
        var storage = new RoomTrustStore(directory); var gate = new RoomIdentityGate(room, owner, storage: storage);
        gate.Approve("firstName", peer.Fingerprint);
        var writer = new RoomTrustStore(directory); var snapshot = writer.Load(room, owner.Fingerprint);
        writer.Save(room, owner.Fingerprint, snapshot.Append(new RoomIdentityBinding("otherName", owner.Fingerprint, true)).ToArray());
        try { gate.Rename("firstName", "nextName", peer.Fingerprint); return false; } catch (IOException) { }
        return gate.IsApproved("firstName", peer.Fingerprint) && !gate.IsApproved("nextName", peer.Fingerprint) && writer.Load(room, owner.Fingerprint).Length == 2;
    }

    public static async Task<bool> OwnerRenameReconnectSelfTest()
    {
        string? previousDirectory = Environment.GetEnvironmentVariable("TCPTUNNEL_DATA_HOME");
        string previousFingerprint = ApplicationSettings.Current.IdentityFingerprint;
        Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", Path.Combine(Path.GetTempPath(), "TCPTunnel-owner-rename-" + Guid.NewGuid().ToString("N")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var owner = new NativeMlsSession(); using var hub = new SecureHubService("firstOwner", owner);
            ApplicationSettings.Current.IdentityFingerprint = owner.Fingerprint;
            async Task<(IChatConnection Ui, Client Peer, Task Loop)> Connect(string name)
            {
                var pair = LocalChatConnection.CreatePair(); var peer = new Client(pair.HubSide); NetWorker.broadcaster.AddConnection(peer);
                Task loop = hub.RunAsync(peer, deadline.Token);
                if (await MessageProtocol.ReadStringAsync(pair.ClientSide.Stream, deadline.Token) != NetWorker.DO_AUTH_MESSAGE) throw new IOException("No local auth.");
                await MessageProtocol.WriteStringAsync(pair.ClientSide.Stream, "REPLY:" + name, deadline.Token);
                if (await MessageProtocol.ReadStringAsync(pair.ClientSide.Stream, deadline.Token) != NetWorker.AUTH_OK_MESSAGE) throw new IOException("Local auth rejected.");
                return (pair.ClientSide, peer, loop);
            }
            var first = await Connect("firstOwner");
            using (first.Ui)
            {
                string id = Guid.NewGuid().ToString("N");
                await MessageProtocol.WriteStringAsync(first.Ui.Stream, NicknameProtocol.Request(id, "nextOwner"), deadline.Token);
                NicknameReply? reply = null;
                while (reply?.Id != id) NicknameProtocol.TryReply(await MessageProtocol.ReadStringAsync(first.Ui.Stream, deadline.Token), out reply);
                if (reply.Error != "OK" || first.Peer.OriginalNickname != "firstOwner") return false;
            }
            await first.Loop.WaitAsync(deadline.Token);
            var second = await Connect("nextOwner");
            using (second.Ui)
            {
                if (second.Peer.OriginalNickname != "nextOwner") return false;
                await MessageProtocol.WriteStringAsync(second.Ui.Stream, HubStatusProtocol.Hello, deadline.Token);
                while (await MessageProtocol.ReadStringAsync(second.Ui.Stream, deadline.Token) != HubStatusProtocol.Capabilities) { }
                await MessageProtocol.WriteStringAsync(second.Ui.Stream, HubStatusProtocol.CreateRequest(Guid.NewGuid().ToString("N"), true), deadline.Token);
                string original = null!; string name = null!;
                while (!HubStatusProtocol.TryParseReply(await MessageProtocol.ReadStringAsync(second.Ui.Stream, deadline.Token), out _, out name, out _, out original)) { }
                if (name != "nextOwner" || original != "nextOwner") return false;
            }
            await second.Loop.WaitAsync(deadline.Token); return true;
        }
        finally { ApplicationSettings.Current.IdentityFingerprint = previousFingerprint; Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", previousDirectory); }
    }

    public static bool StatusCardRealtimeLanguageSelfTest()
    {
        AppLanguage previous = Lang.Current;
        try
        {
            var card = new StatusCard(new Func<string>[] { () => Lang.Get(TextId.StatusAdministrator, "@tester") });
            Lang.Set(AppLanguage.English); string english = card.Render(80);
            Lang.Set(AppLanguage.Russian); string russian = card.Render(80);
            return english.Contains("Administrator:") && russian.Contains("Администратор:") && english != russian;
        }
        finally { Lang.Set(previous); }
    }

    public static async Task<bool> PlainNicknameConnectionSelfTest()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var socket = new TcpClient(); await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using var client = new Client(await listener.AcceptTcpClientAsync(deadline.Token));
        NetWorker.broadcaster.AddConnection(client);
        Task loop = NetWorker.ServerClientLoopAsync(client, deadline.Token);
        var stream = socket.GetStream();
        async Task<string> Until(Func<string, bool> predicate)
        {
            while (true) { string frame = await MessageProtocol.ReadStringAsync(stream, deadline.Token); if (predicate(frame)) return frame; }
        }
        try
        {
            await Until(frame => frame == NetWorker.DO_AUTH_MESSAGE);
            await MessageProtocol.WriteStringAsync(stream, "REPLY:firstName", deadline.Token);
            await Until(frame => frame == NetWorker.AUTH_OK_MESSAGE);
            await MessageProtocol.WriteStringAsync(stream, SnakeProtocol.Hello, deadline.Token);
            await Until(frame => frame == SnakeProtocol.Capabilities);
            foreach (string next in new[] { "secondName", "thirdName" })
            {
                string id = Guid.NewGuid().ToString("N");
                await MessageProtocol.WriteStringAsync(stream, NicknameProtocol.Request(id, next), deadline.Token);
                string frame = await Until(value => NicknameProtocol.TryReply(value, out var reply) && reply!.Id == id);
                if (!NicknameProtocol.TryReply(frame, out var accepted) || accepted!.Error != "OK" || accepted.Change.Original != "firstName") return false;
                await Until(value => NicknameProtocol.TryChanged(value, out var change) && change!.Current == next);
            }
            using var otherSocket = new TcpClient(); await otherSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
            using var other = new Client(await listener.AcceptTcpClientAsync(deadline.Token)); NetWorker.broadcaster.AddConnection(other);
            try { if (NetWorker.broadcaster.TryReserveNickname(other, "firstName")) return false; }
            finally { NetWorker.broadcaster.RemoveClient(other); }
            await MessageProtocol.WriteStringAsync(stream, WhoisProtocol.Hello, deadline.Token);
            await Until(frame => frame == WhoisProtocol.Capabilities);
            string request = Guid.NewGuid().ToString("N");
            await Task.Delay(600, deadline.Token);
            await MessageProtocol.WriteStringAsync(stream, WhoisProtocol.Request(request, "thirdName"), deadline.Token);
            string response = await Until(frame => WhoisProtocol.TryReply(frame, out var id, out _) && id == request);
            return WhoisProtocol.TryReply(response, out _, out var info) && info?.Nickname == "thirdName" && info.OriginalNickname == "firstName" && client.OriginalNickname == "firstName";
        }
        finally { deadline.Cancel(); socket.Close(); await loop.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    public static bool ProfileRenameRetainsIdentitySelfTest()
    {
        string? previousDirectory = Environment.GetEnvironmentVariable("TCPTUNNEL_DATA_HOME");
        var previous = ApplicationSettings.Current; string previousName = NetWorker.nickname;
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-rename-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", directory);
        try
        {
            typeof(ApplicationSettings).GetProperty("Current")!.SetValue(null, new AppProfile()); NetWorker.nickname = "firstName";
            ApplicationSettings.SaveCurrentProfile(NetWorker.nickname);
            string fingerprint; using (var identity = ApplicationSettings.OpenIdentity()) fingerprint = identity.Fingerprint;
            Guid identityId = ApplicationSettings.Current.IdentityId;
            ApplicationSettings.CheckSessionNickname("nextName"); ApplicationSettings.ApplySessionNickname("nextName"); ApplicationSettings.SaveSessionNickname();
            using (var same = ApplicationSettings.OpenIdentity()) if (same.Fingerprint != fingerprint || ApplicationSettings.Current.IdentityId != identityId) return false;
            NetWorker.nickname = "takenName"; ApplicationSettings.SaveCurrentProfile(NetWorker.nickname);
            using (ApplicationSettings.OpenIdentity()) { }
            NetWorker.nickname = "nextName"; ApplicationSettings.SaveCurrentProfile(NetWorker.nickname);
            try { ApplicationSettings.CheckSessionNickname("takenName"); return false; } catch (IOException) { }
            return ApplicationSettings.Current.IdentityFingerprint == fingerprint;
        }
        finally { typeof(ApplicationSettings).GetProperty("Current")!.SetValue(null, previous); NetWorker.nickname = previousName; Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", previousDirectory); }
    }
}
