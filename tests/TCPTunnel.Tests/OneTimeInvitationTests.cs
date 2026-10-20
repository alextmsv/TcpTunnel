using System.Security.Cryptography;
using TCPTunnel.Cryptography;

namespace TCPTunnel;

internal static class OneTimeInvitationTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
    public static bool OneTimeInvitationStoreSelfTest()
    {
        var clock = new Clock(); var store = new OneTimeInvitations(clock);
        string token = store.Create(); int approvals = 0;
        if (!store.Available(token) || !store.Available(token.ToLowerInvariant()) || store.Available(new string('0', 64))) return false;
        Parallel.For(0, 16, _ => store.Redeem(token, () => approvals++));
        if (approvals != 1 || store.Available(token)) return false;
        token = store.Create(); clock.Ticks += TimeSpan.FromMinutes(10).Ticks;
        if (store.Available(token) || store.Redeem(token, () => approvals++)) return false;
        token = store.Create(); store.RevokeAll();
        if (store.Available(token)) return false;
        for (int i = 0; i < 64; i++) store.Create();
        try { store.Create(); return false; } catch (InvalidOperationException) { }
        string room = new('A', 64), owner = new('B', 64);
        var manual = new SecureInvitation(2, room, owner, "localhost", 9091, 0);
        if (System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(manual.Encode().Substring(5))).Contains("Token", StringComparison.Ordinal) || SecureInvitation.Parse(manual.Encode()).Token != null) return false;
        var once = new SecureInvitation(3, room, owner, "localhost", 9091, 0, token);
        return SecureInvitation.Parse(once.Encode()) == once && once.Encode().StartsWith("tct3:", StringComparison.Ordinal);
    }
    public static bool OneTimeInvitationIdentityProofSelfTest()
    {
        using var owner = new NativeMlsSession(); using var alice = new NativeMlsSession(); using var bob = new NativeMlsSession();
        byte[] room = RandomNumberGenerator.GetBytes(32), binding = RandomNumberGenerator.GetBytes(32);
        var gate = new RoomIdentityGate(room, owner); var store = new OneTimeInvitations(); string token = store.Create();
        byte[] package = alice.CreateKeyPackage();
        var challenge = gate.IssueInvitation("alice", package, binding);
        if (gate.CompleteInvitation(challenge.Id, new byte[64], binding, store, token, out _) || !store.Available(token) || gate.IsApproved("alice", alice.Fingerprint)) return false;
        challenge = gate.IssueInvitation("alice", package, binding);
        byte[] proof = RoomIdentityGate.Respond(alice, challenge, room, owner.Fingerprint, "alice", binding, package);
        if (!gate.CompleteInvitation(challenge.Id, proof, binding, store, token, out var accepted) || accepted?.Fingerprint != alice.Fingerprint || store.Available(token) || !gate.IsApproved("alice", alice.Fingerprint)) return false;
        byte[] bobPackage = bob.CreateKeyPackage();
        challenge = gate.IssueInvitation("bob", bobPackage, binding);
        proof = RoomIdentityGate.Respond(bob, challenge, room, owner.Fingerprint, "bob", binding, bobPackage);
        if (gate.CompleteInvitation(challenge.Id, proof, binding, store, token, out _) || gate.IsApproved("bob", bob.Fingerprint)) return false;
        try { gate.IssueInvitation("alice", bobPackage, binding); return false; } catch (UnauthorizedAccessException) { }
        gate.Revoke("alice");
        try { gate.IssueInvitation("anotherAlice", package, binding); return false; } catch (UnauthorizedAccessException) { }
        return true;
    }
    public static async Task<bool> OneTimeInvitationTlsSelfTest()
    {
        string? previous = Environment.GetEnvironmentVariable("TCPTUNNEL_DATA_HOME");
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-once-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", directory);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var owner = new NativeMlsSession(); using var hub = new SecureHubService("onceOwner", owner);
            var invitation = SecureWire.Parse<SecureInvitation>(Convert.FromBase64String(hub.CreateOneTimeInvitation().Substring(5))) with { Port = 9091 };
            async Task<bool> Join(string nickname)
            {
                var pair = MemoryDuplexStream.CreatePair();
                using var peer = new Client(new Connection(pair.First));
                NetWorker.broadcaster.AddConnection(peer);
                Task server = hub.RunAsync(peer, deadline.Token);
                SecureClientBridge? bridge = null;
                try
                {
                    string greeting = await MessageProtocol.ReadStringAsync(pair.Second, deadline.Token);
                    bridge = await SecureClientBridge.ConnectAsync(pair.Second, greeting, nickname, new NativeMlsSession(),
                        boot => invitation.Matches(boot), deadline.Token, invitationToken: invitation.Token);
                    if (peer.IpDisclosureAllowed || hub.PendingRequestsSnapshot().Length != 0) return false;
                    if (await MessageProtocol.ReadStringAsync(bridge.Stream, deadline.Token) != NetWorker.DO_AUTH_MESSAGE) return false;
                    await MessageProtocol.WriteStringAsync(bridge.Stream, "REPLY:" + nickname, deadline.Token);
                    if (await MessageProtocol.ReadStringAsync(bridge.Stream, deadline.Token) != NetWorker.AUTH_OK_MESSAGE) return false;
                    await MessageProtocol.WriteStringAsync(bridge.Stream, PrivacyProtocol.Ack, deadline.Token);
                    string privacyId = Guid.NewGuid().ToString("N");
                    await MessageProtocol.WriteStringAsync(bridge.Stream, PrivacyProtocol.Request(privacyId), deadline.Token);
                    while (true)
                    {
                        string reply = await MessageProtocol.ReadStringAsync(bridge.Stream, deadline.Token);
                        if (PrivacyProtocol.TryReply(reply, privacyId, out bool hidden)) return hidden == peer.HideClientIpAddresses && peer.IpDisclosureAllowed;
                    }
                }
                finally { bridge?.Dispose(); pair.Second.Dispose(); peer.Close(); await server; }
            }
            if (!await Join("onceAlice")) return false;
            try { await Join("onceBob"); return false; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
        }
        finally { Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", previous); }
    }
    private sealed class Connection(Stream stream) : IChatConnection
    {
        public Stream Stream => stream;
        public System.Net.IPAddress? RemoteIpAddress => null;
        public ChatTransport Transport => ChatTransport.Bluetooth;
        public void Dispose() => stream.Dispose();
    }
}
