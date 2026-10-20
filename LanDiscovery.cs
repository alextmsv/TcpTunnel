#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TCPTunnel.Cryptography;

namespace TCPTunnel;

internal sealed record LanAdvertisement(int Version, string Nonce, string HubId, string Owner, int Port, int Participants, int Limit,
    bool Encrypted, int Protocol, byte[] OwnerKey, byte[] Signature);
internal sealed record LanDiscoveredHub(IPAddress Address, LanAdvertisement Advertisement, DateTimeOffset Seen)
{
    internal string Id => Address + ":" + Advertisement.Port + ":" + Advertisement.HubId;
}

internal sealed class LanReceiveBudget
{
    private readonly Dictionary<IPAddress, (long Window, int Count)> sources = new();
    private long window = Environment.TickCount64; private int count;
    internal bool Accept(IPAddress source)
    {
        long now = Environment.TickCount64;
        if (now - window >= 1000) { window = now; count = 0; }
        if (count++ >= 64) return false;
        if (sources.Count >= 256)
            foreach (var stale in sources.Where(pair => now - pair.Value.Window > 5000).Select(pair => pair.Key).ToArray()) sources.Remove(stale);
        if (!sources.TryGetValue(source, out var entry)) { if (sources.Count >= 256) return false; entry = (now, 0); }
        if (now - entry.Window >= 1000) entry = (now, 0);
        sources[source] = (entry.Window, entry.Count + 1);
        return entry.Count < 8;
    }
}

internal sealed class LanSubnetSnapshot
{
    private (IPAddress Address, int Prefix)[] subnets = Array.Empty<(IPAddress, int)>();
    private long refresh;
    internal bool Allows(IPAddress source)
    {
        if (IPAddress.IsLoopback(source)) return true;
        long now = Environment.TickCount64;
        if (now >= refresh)
        {
            refresh = now + 5000;
            try
            {
                subnets = NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp))
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && address.PrefixLength is >= 1 and <= 32)
                    .Take(64).Select(address => (address.Address, address.PrefixLength)).ToArray();
            }
            catch (NetworkInformationException) { subnets = Array.Empty<(IPAddress, int)>(); }
        }
        return subnets.Any(subnet => LanPolicy.IsInSubnet(source, subnet.Address, subnet.Prefix));
    }
}

internal static class LanDiscoveryCodec
{
    internal const int Port = 49191;
    internal const int MaximumPacket = 1024;
    private const string QueryPrefix = "TCPTUNNEL_LAN1|";
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 4, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static byte[] Query(string nonce) => Encoding.ASCII.GetBytes(QueryPrefix + nonce);
    internal static bool TryQuery(ReadOnlySpan<byte> packet, out string nonce)
    {
        nonce = String.Empty;
        if (packet.Length != QueryPrefix.Length + 32) return false;
        string text = Encoding.ASCII.GetString(packet);
        if (!text.StartsWith(QueryPrefix, StringComparison.Ordinal)) return false;
        nonce = text.Substring(QueryPrefix.Length);
        return Guid.TryParseExact(nonce, "N", out _);
    }
    internal static byte[] Proof(LanAdvertisement advertisement) => SecureWire.Proof(4,
        JsonSerializer.SerializeToUtf8Bytes(advertisement with { Signature = Array.Empty<byte>() }, Options));
    internal static byte[] Encode(LanAdvertisement advertisement, Func<byte[], byte[]>? sign)
    {
        if (advertisement.Encrypted) advertisement = advertisement with { Signature = sign!(Proof(advertisement)) };
        byte[] packet = JsonSerializer.SerializeToUtf8Bytes(advertisement, Options);
        if (packet.Length >= MaximumPacket) throw new InvalidDataException("LAN announcement exceeds its limit.");
        return packet;
    }
    internal static bool TryDecode(byte[] packet, int length, string nonce, Func<byte[], byte[], byte[], bool> verify, out LanAdvertisement? advertisement)
    {
        advertisement = null;
        if (length <= 0 || length >= MaximumPacket) return false;
        try
        {
            var value = JsonSerializer.Deserialize<LanAdvertisement>(packet.AsSpan(0, length), Options);
            if (value == null || value.Version != 1 || value.Nonce != nonce || !Guid.TryParseExact(value.HubId, "N", out _) ||
                !NetWorker.IsNicknameValid(value.Owner) || value.Port is < 1 or > 65535 || value.Limit is < 1 or > 64 ||
                value.Participants < 1 || value.Participants > value.Limit || value.OwnerKey == null || value.Signature == null) return false;
            if (value.Encrypted)
            {
                if (value.Protocol != 3 || value.OwnerKey.Length != 32 || value.Signature.Length != 64 || !verify(value.OwnerKey, Proof(value), value.Signature)) return false;
            }
            else if (value.Protocol != 0 || value.OwnerKey.Length != 0 || value.Signature.Length != 0) return false;
            advertisement = value; return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or MlsException) { return false; }
    }
    internal static IPEndPoint[] BroadcastTargets()
    {
        var targets = new HashSet<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.PrefixLength is < 1 or > 30) continue;
                    byte[] bytes = address.Address.GetAddressBytes();
                    for (int bit = address.PrefixLength; bit < 32; bit++) bytes[bit / 8] |= (byte)(1 << (7 - bit % 8));
                    targets.Add(new IPAddress(bytes));
                }
            }
        }
        catch (NetworkInformationException) { }
        return targets.Take(16).Select(address => new IPEndPoint(address, Port)).ToArray();
    }
}

internal sealed class LanHubHost : IDisposable
{
    private readonly Socket socket;
    private readonly CancellationTokenSource stop = new();
    private readonly Func<string, LanAdvertisement?> snapshot;
    private readonly Func<byte[], byte[]>? sign;
    private readonly Dictionary<IPAddress, DateTimeOffset> requests = new();
    private readonly Task loop;
    private int disposed;
    internal string? Failure { get; private set; }
    internal IPEndPoint Endpoint => (IPEndPoint)socket.LocalEndPoint!;
    internal bool Available => !stop.IsCancellationRequested && !loop.IsCompleted;
    internal LanHubHost(Func<string, LanAdvertisement?> snapshot, Func<byte[], byte[]>? sign, int port = LanDiscoveryCodec.Port, IPAddress? bind = null)
    {
        this.snapshot = snapshot; this.sign = sign;
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true, Ttl = 1 };
        try { socket.Bind(new IPEndPoint(bind ?? IPAddress.Any, port)); }
        catch { socket.Dispose(); stop.Dispose(); throw; }
        loop = RunAsync();
    }
    private async Task RunAsync()
    {
        byte[] buffer = new byte[LanDiscoveryCodec.MaximumPacket]; DateTimeOffset window = DateTimeOffset.UtcNow; int replies = 0;
        var budget = new LanReceiveBudget(); var scope = new LanSubnetSnapshot();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try { received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize) { continue; }
                var sender = (IPEndPoint)received.RemoteEndPoint;
                if (!budget.Accept(sender.Address) || !LanDiscoveryCodec.TryQuery(buffer.AsSpan(0, received.ReceivedBytes), out string nonce) || !scope.Allows(sender.Address)) continue;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now - window >= TimeSpan.FromSeconds(1)) { window = now; replies = 0; }
                if (replies >= 16 || requests.TryGetValue(sender.Address, out var last) && now - last < TimeSpan.FromSeconds(1)) continue;
                if (requests.Count >= 256)
                    foreach (var stale in requests.Where(pair => now - pair.Value > TimeSpan.FromSeconds(30)).Select(pair => pair.Key).ToArray()) requests.Remove(stale);
                if (requests.Count >= 256 && !requests.ContainsKey(sender.Address)) continue;
                requests[sender.Address] = now; replies++;
                var advertisement = snapshot(nonce); if (advertisement == null) continue;
                byte[] packet = LanDiscoveryCodec.Encode(advertisement, sign);
                await socket.SendToAsync(packet, SocketFlags.None, sender, stop.Token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException or IOException or MlsException)
        { if (!stop.IsCancellationRequested) Failure = error.Message; }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); socket.Dispose(); try { loop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _ = loop.ContinueWith(_ => stop.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

internal sealed class LanHubScanner : IDisposable
{
    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, LanDiscoveredHub> hubs = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly string nonce = Guid.NewGuid().ToString("N");
    private readonly Task sender, receiver;
    private readonly IPEndPoint[] targets;
    private int disposed;
    internal string? Failure { get; private set; }
    internal bool Available => !stop.IsCancellationRequested && !receiver.IsCompleted && !sender.IsCompleted;
    internal LanHubScanner(IPEndPoint[]? targets = null)
    {
        this.targets = targets ?? LanDiscoveryCodec.BroadcastTargets();
        socket.EnableBroadcast = true; socket.Ttl = 1;
        try { socket.Bind(new IPEndPoint(IPAddress.Any, 0)); }
        catch { socket.Dispose(); stop.Dispose(); throw; }
        sender = SendAsync(); receiver = ReceiveAsync();
    }
    internal LanDiscoveredHub[] Snapshot()
    {
        lock (gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (string expired in hubs.Where(pair => now - pair.Value.Seen > TimeSpan.FromSeconds(5)).Select(pair => pair.Key).ToArray()) hubs.Remove(expired);
            return hubs.Values.OrderBy(hub => hub.Address.ToString(), StringComparer.Ordinal).ThenBy(hub => hub.Advertisement.Port).ToArray();
        }
    }
    private async Task SendAsync()
    {
        byte[] query = LanDiscoveryCodec.Query(nonce);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var target in targets) { try { await socket.SendToAsync(query, SocketFlags.None, target, stop.Token); } catch (SocketException) { } }
                await Task.Delay(1200, stop.Token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        { if (!stop.IsCancellationRequested) Failure = error.Message; }
    }
    private async Task ReceiveAsync()
    {
        byte[] buffer = new byte[LanDiscoveryCodec.MaximumPacket];
        var sources = new Dictionary<IPAddress, DateTimeOffset>();
        var budget = new LanReceiveBudget(); var scope = new LanSubnetSnapshot();
        try
        {
            using var verifier = new NativeMlsSession();
            while (!stop.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try { received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize) { continue; }
                var endpoint = (IPEndPoint)received.RemoteEndPoint;
                if (!budget.Accept(endpoint.Address) || !scope.Allows(endpoint.Address)) continue;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (sources.TryGetValue(endpoint.Address, out var seen) && now - seen < TimeSpan.FromMilliseconds(100)) continue;
                if (sources.Count >= 256)
                    foreach (var stale in sources.Where(pair => now - pair.Value > TimeSpan.FromSeconds(5)).Select(pair => pair.Key).ToArray()) sources.Remove(stale);
                if (sources.Count >= 256 && !sources.ContainsKey(endpoint.Address)) continue;
                sources[endpoint.Address] = now;
                if (!LanDiscoveryCodec.TryDecode(buffer, received.ReceivedBytes, nonce, verifier.VerifyIdentityProof, out var advertisement)) continue;
                var hub = new LanDiscoveredHub(endpoint.Address, advertisement!, now);
                lock (gate)
                {
                    if (!hubs.ContainsKey(hub.Id) && hubs.Values.Count(existing => existing.Address.Equals(endpoint.Address)) >= 4) continue;
                    if (hubs.Count < 64 || hubs.ContainsKey(hub.Id)) hubs[hub.Id] = hub;
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException or IOException or MlsException or InvalidOperationException)
        { if (!stop.IsCancellationRequested) Failure = error.Message; }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); socket.Dispose(); Task all = Task.WhenAll(sender, receiver); try { all.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _ = all.ContinueWith(_ => stop.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
