using System.Net;
using System.Security.Cryptography;
using TCPTunnel;
using TCPTunnel.Cryptography;

internal static class LanDiscoveryTests
{
    public static bool LanReceiveBudgetSelfTest()
    {
        var budget = new LanReceiveBudget();
        for (int i = 0; i < 8; i++) if (!budget.Accept(IPAddress.Loopback)) return false;
        if (budget.Accept(IPAddress.Loopback)) return false;
        int accepted = 8;
        for (int i = 1; i < 100; i++) if (budget.Accept(IPAddress.Parse("10.0.0." + i))) accepted++;
        return accepted == 63;
    }

    public static async Task<bool> LanOversizeAndPortConflictSelfTest()
    {
        using var host = new LanHubHost(nonce => new LanAdvertisement(1, nonce, Guid.NewGuid().ToString("N"), "lanTest", 9091, 1, 4, false, 0, Array.Empty<byte>(), Array.Empty<byte>()), null, 0, IPAddress.Loopback);
        bool conflict = false;
        try { using var duplicate = new LanHubHost(_ => null, null, host.Endpoint.Port, IPAddress.Loopback); }
        catch (System.Net.Sockets.SocketException) { conflict = true; }
        if (!conflict) return false;
        using var sender = new System.Net.Sockets.UdpClient();
        await sender.SendAsync(new byte[2048], host.Endpoint);
        using var scanner = new LanHubScanner(new[] { host.Endpoint });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (scanner.Snapshot().Length == 0) await Task.Delay(20, deadline.Token);
        return host.Available && scanner.Available;
    }

    public static bool LanAnnouncementValidationSelfTest()
    {
        using var owner = new NativeMlsSession(); using var verifier = new NativeMlsSession();
        string nonce = Guid.NewGuid().ToString("N");
        var advertisement = new LanAdvertisement(1, nonce, Guid.NewGuid().ToString("N"), "lanOwner", 9091, 2, 4, true, 3, owner.PublicKey, Array.Empty<byte>());
        byte[] packet = LanDiscoveryCodec.Encode(advertisement, owner.SignIdentityProof);
        if (!LanDiscoveryCodec.TryDecode(packet, packet.Length, nonce, verifier.VerifyIdentityProof, out var accepted) || accepted?.Owner != "lanOwner") return false;
        if (LanDiscoveryCodec.TryDecode(packet, packet.Length, Guid.NewGuid().ToString("N"), verifier.VerifyIdentityProof, out _)) return false;
        var forged = accepted! with { Owner = "impostor" };
        byte[] forgedPacket = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(forged);
        if (LanDiscoveryCodec.TryDecode(forgedPacket, forgedPacket.Length, nonce, verifier.VerifyIdentityProof, out _)) return false;
        foreach (var invalid in new[] { advertisement with { Port = 0 }, advertisement with { Limit = 65 }, advertisement with { Participants = 5 }, advertisement with { Owner = "ab\u202Ec" } })
        {
            packet = LanDiscoveryCodec.Encode(invalid, owner.SignIdentityProof);
            if (LanDiscoveryCodec.TryDecode(packet, packet.Length, nonce, verifier.VerifyIdentityProof, out _)) return false;
        }
        var plain = advertisement with { Encrypted = false, Protocol = 0, OwnerKey = Array.Empty<byte>() };
        packet = LanDiscoveryCodec.Encode(plain, null);
        return LanDiscoveryCodec.TryDecode(packet, packet.Length, nonce, (_, _, _) => throw new Exception("Plain announcement invoked crypto."), out _) &&
            !LanDiscoveryCodec.TryDecode(new byte[1024], 1024, nonce, verifier.VerifyIdentityProof, out _);
    }

    public static async Task<bool> LanDiscoveryLifecycleSelfTest()
    {
        string id = Guid.NewGuid().ToString("N");
        using var host = new LanHubHost(nonce => new LanAdvertisement(1, nonce, id, "lanTest", 9091, 1, 4, false, 0, Array.Empty<byte>(), Array.Empty<byte>()), null, 0, IPAddress.Loopback);
        using var scanner = new LanHubScanner(new[] { host.Endpoint });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (scanner.Snapshot().Length == 0) await Task.Delay(20, deadline.Token);
        var found = scanner.Snapshot();
        return found.Length == 1 && found[0].Address.Equals(IPAddress.Loopback) && found[0].Advertisement.HubId == id && found[0].Advertisement.Port == 9091;
    }
}
