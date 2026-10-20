#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel.Cryptography;

internal static class SecureBoundaryTests
{
    internal static async Task<bool> SignedProtocolVersionSelfTest()
    {
        using var owner = new NativeMlsSession();
        var legacy = new SecureBoot(RandomNumberGenerator.GetBytes(32), owner.PublicKey, RandomNumberGenerator.GetBytes(32), "ownerTest", Array.Empty<byte>());
        byte[] oldFormat = SecureWire.Json(new { legacy.Room, legacy.OwnerKey, legacy.CertificateHash, legacy.OwnerName, legacy.Signature });
        if (!oldFormat.SequenceEqual(SecureWire.Json(legacy))) return false;
        var current = legacy with { ProtocolVersion = 3 };
        byte[] signature = owner.SignIdentityProof(SecureWire.BootProof(current));
        if (!owner.VerifyIdentityProof(owner.PublicKey, SecureWire.BootProof(current), signature) ||
            owner.VerifyIdentityProof(owner.PublicKey, SecureWire.BootProof(legacy), signature)) return false;
        using var client = new NativeMlsSession(); bool trustCalled = false;
        try
        {
            using var bridge = await SecureClientBridge.ConnectAsync(new MemoryStream(), "E2E2:" + Convert.ToBase64String(SecureWire.Json(current with { Signature = signature })),
                "clientTest", client, _ => { trustCalled = true; return true; }, CancellationToken.None);
            return false;
        }
        catch (UnauthorizedAccessException) { return !trustCalled; }
    }

    internal static async Task<bool> SecureWireBoundsSelfTest()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (int length in new[] { -1, 0, Int32.MaxValue, SecureWire.MaximumPayload + 2 })
        {
            byte[] header = new byte[5]; BinaryPrimitives.WriteInt32BigEndian(header, length);
            try { await SecureWire.ReadAsync(new MemoryStream(header), deadline.Token); return false; }
            catch (InvalidDataException) { }
        }
        byte[] payload = RandomNumberGenerator.GetBytes(70 * 1024);
        using var encoded = new MemoryStream(); await SecureWire.WriteAsync(encoded, 5, payload, deadline.Token); encoded.Position = 0;
        var read = await SecureWire.ReadAsync(encoded, deadline.Token);
        if (read.Kind != 5 || !read.Payload.SequenceEqual(payload)) return false;
        foreach (string message in new[] { SystemMessageProtocol.Create(SystemMessageKind.UserJoined, "fake"), HubStatusProtocol.Hello, WhoisProtocol.Hello, "\u001eTCPTUNNEL|IMAGE|invalid" })
        {
            try { SecureWire.Render(new[] { message }, "testUser"); return false; }
            catch (InvalidDataException) { }
        }
        byte[] batch = SecureWire.Pack(new[] { "test" });
        try { SecureWire.Unpack(batch.Concat(new byte[] { 1 }).ToArray()); return false; }
        catch (InvalidDataException) { }
        using var identity = new NativeMlsSession();
        string fingerprint = identity.Fingerprint; string room = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var invitation = new SecureInvitation(2, room, fingerprint, "127.0.0.1", 9091, 0);
        if (SecureInvitation.Parse(invitation.Encode()) != invitation) return false;
        var boot = new SecureBoot(Convert.FromHexString(room), identity.PublicKey, RandomNumberGenerator.GetBytes(32), "ownerTest", Array.Empty<byte>());
        if (!invitation.Matches(boot) || invitation.Matches(boot with { Room = RandomNumberGenerator.GetBytes(32) })) return false;
        byte[] signature = identity.SignIdentityProof(SecureWire.BootProof(boot));
        if (!identity.VerifyIdentityProof(identity.PublicKey, SecureWire.BootProof(boot), signature) || identity.VerifyIdentityProof(identity.PublicKey, SecureWire.BootProof(boot with { OwnerName = "fakeOwner" }), signature)) return false;
        return true;
    }

    internal static async Task<bool> SecureProfileAndHubLifecycleSelfTest()
    {
        if (ServerInterface.IsRunning) return false;
        string? previous = Environment.GetEnvironmentVariable("TCPTUNNEL_DATA_HOME");
        var original = ApplicationSettings.ParseProfile(ApplicationSettings.ParseLines(ApplicationSettings.Serialize(ApplicationSettings.Current)), new AppProfile());
        string originalNickname = NetWorker.nickname;
        HubOptions originalOptions = ServerInterface.Options;
        string originalAdministrator = ServerInterface.AdministratorNickname;
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-secure-profile-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            ApplicationSettings.Current.IdentityId = Guid.Empty; ApplicationSettings.Current.IdentityFingerprint = String.Empty;
            NetWorker.nickname = "ownerCheck"; ApplicationSettings.SaveCurrentProfile(NetWorker.nickname);
            string fingerprint; Guid identityId;
            using (var identity = ApplicationSettings.OpenIdentity()) { fingerprint = identity.Fingerprint; identityId = ApplicationSettings.Current.IdentityId; }
            using (var same = ApplicationSettings.OpenIdentity()) if (same.Fingerprint != fingerprint) return false;
            var config = Directory.GetFiles(Path.Combine(directory, "profiles"), "*.cfg").SelectMany(File.ReadAllLines).ToArray();
            if (!config.Contains("identityFingerprint=" + fingerprint) || !config.Contains("identityId=" + identityId.ToString("N"))) return false;
            NetWorker.nickname = "otherCheck"; ApplicationSettings.SaveCurrentProfile(NetWorker.nickname);
            using (var second = ApplicationSettings.OpenIdentity()) if (second.Fingerprint == fingerprint) return false;
            NetWorker.nickname = "ownerCheck"; ApplicationSettings.SaveCurrentProfile(NetWorker.nickname);
            using (var restored = ApplicationSettings.OpenIdentity()) if (restored.Fingerprint != fingerprint) return false;
            using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            if (!ServerInterface.TryStartServer(port, new HubOptions(HubIpMode.LanOnly, false, true), out string error, NetWorker.nickname, out _)) throw new IOException(error);
            if (ServerInterface.SecureHub == null || ServerInterface.SecureHub.OwnerFingerprint != fingerprint) return false;
            using var local = ServerInterface.CreateLocalConnection();
            if (await MessageProtocol.ReadStringAsync(local.Stream, deadline.Token) != NetWorker.DO_AUTH_MESSAGE) return false;
            await MessageProtocol.WriteStringAsync(local.Stream, "REPLY:ownerCheck", deadline.Token);
            if (await MessageProtocol.ReadStringAsync(local.Stream, deadline.Token) != NetWorker.AUTH_OK_MESSAGE) return false;
            await MessageProtocol.WriteStringAsync(local.Stream, "owner before any remote members", deadline.Token);
            using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            string greeting = await MessageProtocol.ReadStringAsync(socket.GetStream(), deadline.Token);
            var remoteIdentity = new NativeMlsSession(); string remoteFingerprint = remoteIdentity.Fingerprint;
            var invitation = SecureInvitation.Parse(ServerInterface.SecureHub.Invitation);
            var join = SecureClientBridge.ConnectAsync(socket.GetStream(), greeting, "remoteCheck", remoteIdentity, invitation.Matches, deadline.Token);
            while (!ServerInterface.SecureHub.PendingRequests.Contains(remoteFingerprint, StringComparison.Ordinal)) { if (join.IsCompleted) throw new IOException("Remote join ended before approval."); await Task.Delay(10, deadline.Token); }
            var pendingRequest = ServerInterface.SecureHub.PendingRequestsSnapshot().Single(request => request.Fingerprint == remoteFingerprint);
            var approvedRequest = ServerInterface.SecureHub.Approve(pendingRequest.Number);
            if (approvedRequest.Nickname != "remoteCheck" || approvedRequest.Fingerprint != remoteFingerprint) return false;
            using var remote = await join;
            if (await MessageProtocol.ReadStringAsync(remote.Stream, deadline.Token) != NetWorker.DO_AUTH_MESSAGE) return false;
            await MessageProtocol.WriteStringAsync(remote.Stream, "REPLY:remoteCheck", deadline.Token);
            if (await MessageProtocol.ReadStringAsync(remote.Stream, deadline.Token) != NetWorker.AUTH_OK_MESSAGE) return false;
            await MessageProtocol.WriteStringAsync(local.Stream, "owner message", deadline.Token);
            string received;
            do { received = await MessageProtocol.ReadStringAsync(remote.Stream, deadline.Token); } while (SystemMessageProtocol.IsControlMessage(received));
            if (received != "[ownerCheck]: owner message") return false;
            await MessageProtocol.WriteStringAsync(remote.Stream, "remote message", deadline.Token);
            do { received = await MessageProtocol.ReadStringAsync(local.Stream, deadline.Token); } while (SystemMessageProtocol.IsControlMessage(received));
            if (received != "[remoteCheck]: remote message") return false;
            await MessageProtocol.WriteStringAsync(remote.Stream, WhoisProtocol.Hello, deadline.Token);
            do { received = await MessageProtocol.ReadStringAsync(remote.Stream, deadline.Token); } while (received != WhoisProtocol.Capabilities);
            string requestId = Guid.NewGuid().ToString("N");
            typeof(ServerInterface).GetProperty("Options", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, ServerInterface.Options with { HideClientIpAddresses = false });
            await MessageProtocol.WriteStringAsync(remote.Stream, WhoisProtocol.Request(requestId, "remoteCheck"), deadline.Token);
            WhoisInfo info; string responseId;
            do { received = await MessageProtocol.ReadStringAsync(remote.Stream, deadline.Token); } while (!WhoisProtocol.TryReply(received, out responseId, out info));
            if (responseId != requestId || !info.AddressHidden || info.Address != String.Empty || received.Contains("127.0.0.1", StringComparison.Ordinal)) return false;
            ServerInterface.StopServer();
            File.Delete(Path.Combine(directory, "identities", identityId.ToString("N") + ".key"));
            try { using var missing = ApplicationSettings.OpenIdentity(); return false; }
            catch (FileNotFoundException) { }
            return ApplicationSettings.Current.IdentityId == identityId;
        }
        catch (Exception error) { Console.WriteLine("Secure profile lifecycle: " + error); return false; }
        finally
        {
            ServerInterface.StopServer();
            typeof(ServerInterface).GetProperty("Options", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, originalOptions);
            typeof(ServerInterface).GetProperty("AdministratorNickname")!.SetValue(null, originalAdministrator);
            foreach (var field in typeof(AppProfile).GetFields()) field.SetValue(ApplicationSettings.Current, field.GetValue(original));
            NetWorker.nickname = originalNickname; Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", previous);
        }
    }
}
