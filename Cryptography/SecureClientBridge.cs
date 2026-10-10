#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel.Cryptography;

internal sealed class SecureClientBridge : IDisposable
{
    private readonly NativeMlsSession mls;
    private readonly Stream network;
    private readonly MemoryDuplexStream pump;
    private readonly MemoryDuplexStream ui;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim write = new(1, 1);
    private readonly SecureBoot boot;
    private readonly Dictionary<string, string> roster = new(StringComparer.Ordinal);
    private string[] snakeRoster = Array.Empty<string>();
    private readonly object state = new();
    private TaskCompletionSource<bool>? receipt;
    private byte[]? pendingHash;
    private long revision;
    private int disposed;
    internal Stream Stream => ui;
    private SecureClientBridge(Stream network, NativeMlsSession mls, SecureBoot boot)
    {
        this.network = network; this.mls = mls; this.boot = boot;
        (pump, ui) = MemoryDuplexStream.CreatePair();
    }

    internal static async Task<SecureClientBridge> ConnectAsync(Stream stream, string greeting, string nickname,
        NativeMlsSession identity, Func<SecureBoot, bool> trust, CancellationToken token, Action<string>? progress = null)
    {
        SslStream? tls = null;
        try
        {
            var boot = SecureWire.Parse<SecureBoot>(Convert.FromBase64String(greeting.Substring("E2E2:".Length)));
            if (!identity.VerifyIdentityProof(boot.OwnerKey, SecureWire.BootProof(boot), boot.Signature)) throw new UnauthorizedAccessException("Hub identity is not verified.");
            tls = new SslStream(stream, false, (_, certificate, _, _) => certificate != null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), boot.CertificateHash));
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token); handshake.CancelAfter(TimeSpan.FromSeconds(30));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "TCPTunnel", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowRenegotiation = false }, handshake.Token).ConfigureAwait(false);
            if (!trust(boot)) throw new UnauthorizedAccessException("Hub identity is not verified.");
            byte[] package = identity.CreateKeyPackage(); byte[] binding = RandomNumberGenerator.GetBytes(32);
            progress?.Invoke("Identity: " + identity.Fingerprint);
            progress?.Invoke(Lang.Current == AppLanguage.Russian ? "Ожидание подтверждения владельцем Hub…" : "Waiting for the Hub owner to approve your identity…");
            await SecureWire.WriteAsync(tls, 2, SecureWire.Json(new SecureHello(nickname, package, binding)), token).ConfigureAwait(false);
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(token); admission.CancelAfter(TimeSpan.FromMinutes(3));
            SecurePacket challengePacket = await SecureWire.ReadAsync(tls, admission.Token, 256 * 1024).ConfigureAwait(false);
            if (challengePacket.Kind != 3) throw new UnauthorizedAccessException("Room access was refused.");
            var challenge = SecureWire.Parse<IdentityChallenge>(challengePacket.Payload);
            byte[] signature = RoomIdentityGate.Respond(identity, challenge, boot.Room, NativeMlsSession.FingerprintOf(boot.OwnerKey), nickname, binding, package);
            await SecureWire.WriteAsync(tls, 3, signature, admission.Token).ConfigureAwait(false);
            SecurePacket welcome = await SecureWire.ReadAsync(tls, admission.Token, 256 * 1024).ConfigureAwait(false);
            if (welcome.Kind != 4) throw new UnauthorizedAccessException("Room access was refused.");
            identity.Join(welcome.Payload);
            var bridge = new SecureClientBridge(tls, identity, boot);
            bridge.ValidateGroup();
            SecurePacket roster = await SecureWire.ReadAsync(tls, admission.Token, 256 * 1024).ConfigureAwait(false);
            bridge.AcceptRoster(roster);
            _ = bridge.RunAsync(); return bridge;
        }
        catch { tls?.Dispose(); identity.Dispose(); throw; }
    }
    private void ValidateGroup()
    {
        byte[] group = mls.GroupIdentities();
        if (group.Length < 96 || !group.AsSpan(0, 32).SequenceEqual(boot.Room) || !group.AsSpan(32, 32).SequenceEqual(Convert.FromHexString(NativeMlsSession.FingerprintOf(boot.OwnerKey)))) throw new UnauthorizedAccessException("The MLS room owner does not match the verified invitation.");
    }
    private void AcceptRoster(SecurePacket packet)
    {
        if (packet.Kind != 7) throw new InvalidDataException("Missing signed room roster.");
        var next = SecureWire.Parse<SecureRoster>(packet.Payload);
        if (next.Room == null || !next.Room.AsSpan().SequenceEqual(boot.Room) || next.Members == null || next.Members.Length is < 1 or > 64 || next.Revision <= revision ||
            !mls.VerifyIdentityProof(boot.OwnerKey, SecureWire.Proof(3, SecureWire.Json(next with { Signature = Array.Empty<byte>() })), next.Signature)) throw new UnauthorizedAccessException("Invalid signed room roster.");
        ValidateGroup(); byte[] group = mls.GroupIdentities();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        for (int offset = 64; offset < group.Length; offset += 32) fingerprints.Add(Convert.ToHexString(group.AsSpan(offset, 32)));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (fingerprints.Count != next.Members.Length) throw new UnauthorizedAccessException("Roster does not match MLS membership.");
        foreach (var member in next.Members)
        {
            if (!NetWorker.IsNicknameValid(member.Nickname) || !names.Add(member.Nickname) || !fingerprints.Remove(member.Fingerprint)) throw new UnauthorizedAccessException("Invalid roster identity binding.");
            if (member.Snake != null && (member.Snake.Length > 256 || !SnakeProtocol.TryParseServerUpdate(member.Snake, out _, out string snakeName, out _) || snakeName != member.Nickname)) throw new InvalidDataException("Invalid signed snake metadata.");
        }
        snakeRoster = next.Members.Where(member => member.Fingerprint != mls.Fingerprint && member.Snake != null).Select(member => member.Snake!).ToArray();
        roster.Clear(); foreach (var member in next.Members) roster.Add(member.Fingerprint, member.Nickname); revision = next.Revision;
    }
    private async Task RunAsync()
    {
        Task? receiver = null;
        try
        {
            await MessageProtocol.WriteStringAsync(pump, NetWorker.DO_AUTH_MESSAGE, stop.Token).ConfigureAwait(false);
            string auth = await MessageProtocol.ReadStringAsync(pump, stop.Token).ConfigureAwait(false);
            if (!auth.StartsWith("REPLY:", StringComparison.Ordinal)) throw new InvalidDataException("Invalid local UI authentication.");
            await MessageProtocol.WriteStringAsync(pump, NetWorker.AUTH_OK_MESSAGE, stop.Token).ConfigureAwait(false);
            foreach (string name in roster.Values.Where(name => name != auth.Substring(6)))
                await MessageProtocol.WriteStringAsync(pump, SystemMessageProtocol.Create(SystemMessageKind.ParticipantPresent, name), stop.Token).ConfigureAwait(false);
            foreach (string snake in snakeRoster) await MessageProtocol.WriteStringAsync(pump, snake, stop.Token).ConfigureAwait(false);
            receiver = ReceiveAsync(); var animation = new List<string>(); var assembler = new ImageAnimationAssembler();
            while (!stop.IsCancellationRequested)
            {
                string message = await assembler.ReadNextAsync(pump, stop.Token, () => animation.Clear()).ConfigureAwait(false);
                if (WhoisProtocol.IsControl(message) || HubStatusProtocol.IsControl(message))
                { await SendAsync(1, Encoding.UTF8.GetBytes(message), stop.Token).ConfigureAwait(false); continue; }
                if (ImageAnimationProtocol.IsAnimationControlMessage(message))
                {
                    if (!ImageAnimationProtocol.TryParseClient(message, out var frame)) throw new InvalidDataException("Invalid outgoing animation.");
                    if (!assembler.IsActive) animation.Clear(); animation.Add(message);
                    var result = assembler.Accept(frame, out _);
                    if (result == ImageAnimationAssemblyResult.Invalid) throw new InvalidDataException("Invalid outgoing animation.");
                    if (result != ImageAnimationAssemblyResult.Completed) continue;
                    await SendApplicationAsync(animation.ToArray()).ConfigureAwait(false); animation.Clear();
                }
                else await SendApplicationAsync(new[] { message }).ConfigureAwait(false);
            }
        }
        catch (Exception error) { if (!stop.IsCancellationRequested) await ReportFailureAsync(error).ConfigureAwait(false); }
        finally
        {
            stop.Cancel(); network.Dispose(); pump.Dispose();
            lock (state) receipt?.TrySetException(new IOException("Secure connection closed."));
            if (receiver != null) { try { await receiver.ConfigureAwait(false); } catch { } }
            mls.Dispose();
        }
    }
    private async Task SendApplicationAsync(string[] messages)
    {
        byte[] plaintext = SecureWire.Pack(messages);
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte[] encrypted; Task<bool> acknowledgement;
                lock (state)
                {
                    encrypted = mls.Encrypt(plaintext); pendingHash = SHA256.HashData(encrypted);
                    receipt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); acknowledgement = receipt.Task;
                }
                await SendAsync(5, encrypted, stop.Token).ConfigureAwait(false);
                bool accepted = await acknowledgement.WaitAsync(TimeSpan.FromSeconds(30), stop.Token).ConfigureAwait(false);
                if (accepted) return;
            }
            throw new IOException("Room membership changed too often to send the message.");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    private async Task ReceiveAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await SecureWire.ReadAsync(network, stop.Token).ConfigureAwait(false);
                if (packet.Kind is 10 or 11)
                {
                    lock (state)
                    {
                        if (pendingHash == null || !packet.Payload.AsSpan().SequenceEqual(pendingHash)) throw new InvalidDataException("Unexpected secure receipt.");
                        pendingHash = null; receipt!.TrySetResult(packet.Kind == 10);
                    }
                    continue;
                }
                if (packet.Kind == 1)
                {
                    string control = new UTF8Encoding(false, true).GetString(packet.Payload);
                    if (packet.Payload.Length > MessageProtocol.MaxFrameBytes || (!WhoisProtocol.IsControl(control) && !HubStatusProtocol.IsControl(control) && !SystemMessageProtocol.IsControlMessage(control))) throw new InvalidDataException("Unexpected Hub control message.");
                    await MessageProtocol.WriteStringAsync(pump, control, stop.Token).ConfigureAwait(false); continue;
                }
                if (packet.Kind == 7)
                {
                    string[] left; string[] joined;
                    lock (state)
                    {
                        var before = roster.Values.ToHashSet(StringComparer.OrdinalIgnoreCase); AcceptRoster(packet);
                        left = before.Except(roster.Values, StringComparer.OrdinalIgnoreCase).ToArray(); joined = roster.Values.Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                    foreach (string name in left) { await MessageProtocol.WriteStringAsync(pump, SnakeProtocol.CreateRemove(name), stop.Token).ConfigureAwait(false); await MessageProtocol.WriteStringAsync(pump, SystemMessageProtocol.Create(SystemMessageKind.UserLeft, name), stop.Token).ConfigureAwait(false); }
                    foreach (string name in joined) await MessageProtocol.WriteStringAsync(pump, SystemMessageProtocol.Create(SystemMessageKind.UserJoined, name), stop.Token).ConfigureAwait(false);
                    foreach (string snake in snakeRoster) await MessageProtocol.WriteStringAsync(pump, snake, stop.Token).ConfigureAwait(false);
                    continue;
                }
                if (packet.Kind is not (5 or 6)) throw new InvalidDataException("Unknown secure packet.");
                MlsReceived received;
                lock (state) received = mls.Process(packet.Payload);
                if (packet.Kind == 6)
                { if (received.Kind == MlsReceivedKind.Removed) throw new IOException("Removed from encrypted room."); if (received.Kind != MlsReceivedKind.Commit) throw new InvalidDataException("Unexpected MLS content."); continue; }
                if (received.Kind != MlsReceivedKind.Application || received.SenderFingerprint == null || !roster.TryGetValue(received.SenderFingerprint, out string? nickname)) throw new UnauthorizedAccessException("Unknown MLS sender.");
                try
                {
                    foreach (string frame in SecureWire.Render(SecureWire.Unpack(received.Content), nickname)) await MessageProtocol.WriteStringAsync(pump, frame, stop.Token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(received.Content); }
            }
        }
        catch (Exception error) { if (!stop.IsCancellationRequested) await ReportFailureAsync(error).ConfigureAwait(false); }
        finally { stop.Cancel(); pump.Dispose(); network.Dispose(); lock (state) receipt?.TrySetException(new IOException("Secure receiver closed.")); }
    }
    private async Task ReportFailureAsync(Exception error)
    {
        string message = Lang.Get(error is IOException && error.Message == "Removed from encrypted room." ? TextId.CryptoRemoved : error is MlsException or UnauthorizedAccessException or InvalidDataException ? TextId.CryptoInvalidPacket : TextId.CryptoConnectionEnded);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await MessageProtocol.WriteStringAsync(pump, SystemMessageProtocol.Create(SystemMessageKind.EncryptionError, message), deadline.Token).ConfigureAwait(false); } catch { }
    }
    private async Task SendAsync(byte kind, byte[] payload, CancellationToken token)
    {
        await write.WaitAsync(token).ConfigureAwait(false);
        try { await SecureWire.WriteAsync(network, kind, payload, token).ConfigureAwait(false); }
        finally { write.Release(); }
    }
    internal static bool VerifyTrust(SecureBoot boot, string? endpoint = null)
    {
        string owner = NativeMlsSession.FingerprintOf(boot.OwnerKey);
        var endpoints = new EndpointTrustStore(Path.Combine(ApplicationSettings.DataDirectory, "trusted-endpoints"));
        if (endpoint != null) endpoints.Check(endpoint, owner);
        var store = new RoomTrustStore(Path.Combine(ApplicationSettings.DataDirectory, "trusted-hubs"));
        string path = Path.Combine(ApplicationSettings.DataDirectory, "trusted-hubs", Convert.ToHexString(boot.Room) + ".trust");
        if (File.Exists(path))
        { bool allowed = store.Load(boot.Room, owner).Any(item => item.Fingerprint == owner && item.Allowed); if (allowed && endpoint != null) endpoints.Save(endpoint, owner); return allowed; }
        string? supplied = Environment.GetCommandLineArgs().FirstOrDefault(value => value.StartsWith("--e2e-owner=", StringComparison.Ordinal));
        if (supplied != null && supplied.Substring(12).Equals(owner, StringComparison.OrdinalIgnoreCase))
        { store.Save(boot.Room, owner, new[] { new RoomIdentityBinding(boot.OwnerName, owner, true) }); if (endpoint != null) endpoints.Save(endpoint, owner); return true; }
        ConsoleGraphic.WriteContentLine(Lang.Get(TextId.CryptoOwnerFingerprintPrompt));
        if (!String.Equals(Console.ReadLine()?.Trim(), owner, StringComparison.OrdinalIgnoreCase)) return false;
        store.Save(boot.Room, owner, new[] { new RoomIdentityBinding(boot.OwnerName, owner, true) }); if (endpoint != null) endpoints.Save(endpoint, owner); return true;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); ui.Dispose(); pump.Dispose(); network.Dispose();
    }
    internal static bool TrustVerifiedInvitation(SecureBoot boot, SecureInvitation invite, string? endpoint = null)
    {
        if (!invite.Matches(boot)) return false;
        var endpoints = new EndpointTrustStore(Path.Combine(ApplicationSettings.DataDirectory, "trusted-endpoints"));
        if (endpoint != null) endpoints.Check(endpoint, NativeMlsSession.FingerprintOf(boot.OwnerKey));
        var store = new RoomTrustStore(Path.Combine(ApplicationSettings.DataDirectory, "trusted-hubs"));
        string owner = NativeMlsSession.FingerprintOf(boot.OwnerKey);
        string path = Path.Combine(ApplicationSettings.DataDirectory, "trusted-hubs", Convert.ToHexString(boot.Room) + ".trust");
        if (File.Exists(path) && !store.Load(boot.Room, owner).Any(item => item.Fingerprint == owner && item.Allowed)) return false;
        store.Save(boot.Room, owner, new[] { new RoomIdentityBinding(boot.OwnerName, owner, true) }); if (endpoint != null) endpoints.Save(endpoint, owner); return true;
    }
}
