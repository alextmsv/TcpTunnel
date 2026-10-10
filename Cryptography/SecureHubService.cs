#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel.Cryptography;

internal sealed class SecureHubService : IDisposable
{
    private sealed record Peer(Client Client, string Fingerprint);
    private readonly NativeMlsSession owner;
    private readonly RoomIdentityGate identities;
    private readonly X509Certificate2 certificate;
    private readonly byte[] room;
    private readonly SecureBoot boot;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Client, Peer> peers = new();
    private readonly PendingRoomApprovals pending = new();
    private readonly CancellationTokenSource stop = new();
    private Client? local;
    private long revision = 1;
    private bool failed;
    internal string OwnerFingerprint => owner.Fingerprint;
    internal int ParticipantCount => stop.IsCancellationRequested ? 0 : peers.Count + 1;
    internal event Action<Exception>? SessionError;
    internal string ServiceStatus => failed ? "FAILED" : stop.IsCancellationRequested ? "STOPPED" : "READY (OpenMLS)";
    internal SecureHubService(string nickname, NativeMlsSession owner)
    {
        this.owner = owner;
        try
        {
            if (!NetWorker.IsNicknameValid(nickname)) throw new InvalidDataException("A secure Hub requires an owner nickname.");
            room = SHA256.HashData(Encoding.ASCII.GetBytes("TCPTunnel/Room/v2/" + owner.Fingerprint));
            identities = new RoomIdentityGate(room, owner, storage: new RoomTrustStore(Path.Combine(ApplicationSettings.DataDirectory, "rooms")));
            identities.Approve(nickname, owner.Fingerprint); owner.CreateGroup(room);
            using var certificateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=TCPTunnel", certificateKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
            byte[] pfx = generated.Export(X509ContentType.Pfx);
            // WARNING: Windows Schannel rejects ephemeral TLS private keys; disposal removes this temporary key container.
            try { certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            var unsigned = new SecureBoot(room, owner.PublicKey, SHA256.HashData(certificate.RawData), nickname, Array.Empty<byte>());
            boot = unsigned with { Signature = owner.SignIdentityProof(SecureWire.BootProof(unsigned)) };
        }
        catch { owner.Dispose(); throw; }
    }
    internal string Invitation => new SecureInvitation(2, Convert.ToHexString(room), owner.Fingerprint, ServerInterface.DisplayAddress, ServerInterface.ListeningPort, ServerInterface.BluetoothAddress).Encode();
    internal string PendingRequests
    {
        get => String.Join("\n", pending.Snapshot().SelectMany(item => item.DisplayLines()));
    }
    internal PendingRoomRequest[] PendingRequestsSnapshot() => pending.Snapshot();
    internal PendingRoomRequest GetPendingRequest(long number) => pending.Get(number);
    internal PendingRoomRequest Approve(long number) => pending.Approve(number, identities.Approve);
    internal void Approve(string nickname, string fingerprint) => pending.Approve(nickname, fingerprint, identities.Approve);
    internal async Task RunAsync(Client client, CancellationToken serverToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(serverToken, stop.Token);
        Task? heartbeat = null;
        try
        {
            if (client.Transport == ChatTransport.Local) await AuthenticateLocalAsync(client, session.Token).ConfigureAwait(false);
            else await AuthenticateRemoteAsync(client, session.Token).ConfigureAwait(false);
            while (!session.IsCancellationRequested)
            {
                if (client.Transport == ChatTransport.Local)
                {
                    var animation = new List<string>(); var assembler = new ImageAnimationAssembler();
                    while (!session.IsCancellationRequested)
                    {
                        string message = await assembler.ReadNextAsync(client.Stream, session.Token, () => animation.Clear()).ConfigureAwait(false);
                        if (await ControlAsync(client, message, session.Token).ConfigureAwait(false))
                        { if (client.Diagnostics.Supported && heartbeat == null) heartbeat = WhoisServer.HeartbeatAsync(client, session.Token); continue; }
                        if (ImageAnimationProtocol.IsAnimationControlMessage(message))
                        {
                            if (!ImageAnimationProtocol.TryParseClient(message, out var frame)) throw new InvalidDataException("Invalid local animation.");
                            if (!assembler.IsActive) animation.Clear(); animation.Add(message);
                            var result = assembler.Accept(frame, out _);
                            if (result == ImageAnimationAssemblyResult.Invalid) throw new InvalidDataException("Invalid local animation.");
                            if (result != ImageAnimationAssemblyResult.Completed) continue;
                            await SendOwnerAsync(client, animation.ToArray(), session.Token).ConfigureAwait(false); animation.Clear();
                        }
                        else await SendOwnerAsync(client, new[] { message }, session.Token).ConfigureAwait(false);
                    }
                    return;
                }
                SecurePacket packet = await SecureWire.ReadAsync(client.Stream, session.Token).ConfigureAwait(false);
                if (packet.Kind == 1)
                {
                    if (packet.Payload.Length > MessageProtocol.MaxFrameBytes) throw new InvalidDataException("Control frame exceeds its limit.");
                    if (!await ControlAsync(client, new UTF8Encoding(false, true).GetString(packet.Payload), session.Token).ConfigureAwait(false)) throw new InvalidDataException("Unexpected unencrypted chat data.");
                    if (client.Diagnostics.Supported && heartbeat == null) heartbeat = WhoisServer.HeartbeatAsync(client, session.Token);
                    continue;
                }
                if (packet.Kind != 5) throw new InvalidDataException("Clients cannot change MLS membership.");
                await gate.WaitAsync(session.Token).ConfigureAwait(false);
                try
                {
                    if (!peers.TryGetValue(client, out Peer? peer)) throw new UnauthorizedAccessException("Inactive room member.");
                    MlsReceived received;
                    try { received = owner.Process(packet.Payload); }
                    catch (MlsException error) when (error.Code == MlsError.RejectedMessage)
                    { if (!client.TryConsumeControlToken()) throw new InvalidDataException("Too many rejected MLS messages."); QueueSecure(client, 11, SHA256.HashData(packet.Payload), session.Token); continue; }
                    if (received.Kind != MlsReceivedKind.Application || received.SenderFingerprint != peer.Fingerprint) throw new UnauthorizedAccessException("MLS sender does not own this authenticated connection.");
                    try
                    {
                        var messages = SecureWire.Unpack(received.Content); var rendered = ValidateContent(client, messages);
                        client.Diagnostics.CountMessage();
                        await BroadcastPacketAsync(5, packet.Payload, client, session.Token).ConfigureAwait(false);
                        if (local != null) QueueLocal(local, rendered, session.Token);
                        QueueSecure(client, 10, SHA256.HashData(packet.Payload), session.Token);
                    }
                    finally { CryptographicOperations.ZeroMemory(received.Content); }
                }
                finally { gate.Release(); }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException or OperationCanceledException or ObjectDisposedException or AuthenticationException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or MlsException or TimeoutException)
        {
            SessionError?.Invoke(error);
            if (error is MlsException mls && mls.Code is MlsError.InternalFailure or MlsError.PoisonedState) FailClosed();
        }
        finally
        {
            session.Cancel();
            if (heartbeat != null) { try { await heartbeat.ConfigureAwait(false); } catch { } }
            try { await RemoveAsync(client, CancellationToken.None).ConfigureAwait(false); } catch { FailClosed(); }
            NetWorker.broadcaster.RemoveClient(client);
        }
    }
    private async Task AuthenticateLocalAsync(Client client, CancellationToken token)
    {
        await client.SendAsync(NetWorker.DO_AUTH_MESSAGE, token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        string reply = await MessageProtocol.ReadStringAsync(client.Stream, deadline.Token).ConfigureAwait(false);
        if (reply != "REPLY:" + boot.OwnerName || ApplicationSettings.Current.IdentityFingerprint != owner.Fingerprint) throw new UnauthorizedAccessException("The local profile is not the room owner.");
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (local != null || !NetWorker.broadcaster.TryReserveNickname(client, boot.OwnerName)) throw new UnauthorizedAccessException("Owner UI is already connected.");
            local = client; client.IpAddress = String.Empty;
            QueueLocal(client, new[] { NetWorker.AUTH_OK_MESSAGE }, token);
            if (!NetWorker.broadcaster.CompleteAuthentication(client)) throw new IOException("Hub participant limit reached.");
            QueueLocal(client, peers.Values.Select(peer => SystemMessageProtocol.Create(SystemMessageKind.ParticipantPresent, peer.Client.Nickname)).ToArray(), token);
            await NoticeAsync(Lang.Get(TextId.HelpOwnerTip), token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private async Task AuthenticateRemoteAsync(Client client, CancellationToken token)
    {
        await client.SendAsync("E2E2:" + Convert.ToBase64String(SecureWire.Json(boot)), token).ConfigureAwait(false);
        var tls = new SslStream(client.Stream, false);
        client.Stream = tls;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token); handshake.CancelAfter(TimeSpan.FromSeconds(15));
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowRenegotiation = false }, handshake.Token).ConfigureAwait(false);
        client.SecureWireEnabled = true;
        using var identityDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); identityDeadline.CancelAfter(TimeSpan.FromMinutes(2));
        var packet = await SecureWire.ReadAsync(tls, identityDeadline.Token, 256 * 1024).ConfigureAwait(false);
        if (packet.Kind != 2) throw new InvalidDataException("Missing identity hello.");
        var hello = SecureWire.Parse<SecureHello>(packet.Payload);
        if (hello.Package == null || hello.Binding == null || hello.Binding.Length != 32 || !NetWorker.IsNicknameValid(hello.Nickname)) throw new InvalidDataException("Invalid identity hello.");
        string fingerprint = NativeMlsSession.FingerprintOf(owner.KeyPackagePublicKey(hello.Package));
        if (fingerprint == owner.Fingerprint) throw new UnauthorizedAccessException("The owner uses the private local connection.");
        if (!identities.IsApproved(hello.Nickname, fingerprint))
        {
            var request = pending.Add(hello.Nickname, fingerprint);
            try
            {
                await NoticeAsync(Lang.Get(TextId.ApprovalRequestReceived) + "\n" + String.Join("\n", request.Info.DisplayLines()) + "\n" + Lang.Get(TextId.ApprovalVerifyHint), token).ConfigureAwait(false);
                await request.Completion.Task.WaitAsync(TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
            }
            finally { pending.Remove(request); }
        }
        var challenge = identities.Issue(hello.Nickname, hello.Package, hello.Binding);
        await client.SendSecureAsync(3, SecureWire.Json(challenge), token).ConfigureAwait(false);
        using var proofDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); proofDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        var proof = await SecureWire.ReadAsync(tls, proofDeadline.Token, 256).ConfigureAwait(false);
        if (proof.Kind != 3 || !identities.Complete(challenge.Id, proof.Payload, hello.Binding, out var approved)) throw new UnauthorizedAccessException("Identity proof was rejected.");
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (peers.Count >= 63 || peers.Values.Any(peer => peer.Fingerprint == fingerprint) || !NetWorker.broadcaster.TryReserveNickname(client, hello.Nickname)) throw new UnauthorizedAccessException("Identity or nickname is already connected.");
            MembershipChange change;
            try { change = owner.AddMember(approved!.KeyPackage); }
            catch (MlsException error) when (error.Code is MlsError.RejectedMessage or MlsError.InvalidArgument or MlsError.ResourceLimit)
            { NetWorker.broadcaster.RemoveClient(client); throw; }
            owner.MergePendingCommit();
            await BroadcastPacketAsync(6, change.Commit, null, token).ConfigureAwait(false);
            peers.Add(client, new Peer(client, fingerprint)); client.IpAddress = client.ObservedIpAddress;
            QueueSecure(client, 4, change.Welcome, token);
            if (!NetWorker.broadcaster.CompleteAuthentication(client)) throw new IOException("Hub participant limit reached.");
            await BroadcastRosterAsync(token).ConfigureAwait(false);
            if (local != null) QueueLocal(local, new[] { SystemMessageProtocol.Create(SystemMessageKind.UserJoined, client.Nickname) }, token);
        }
        catch { if (!peers.ContainsKey(client) && client.IsAuthenticated) FailClosed(); throw; }
        finally { gate.Release(); }
    }
    private string[] ValidateContent(Client client, string[] messages)
    {
        bool image = messages.Length > 1 || ImageProtocol.IsImageControlMessage(messages[0]);
        if (!(image ? client.TryConsumeImageToken() : client.TryConsumeMessageToken())) throw new InvalidDataException("Chat rate limit exceeded.");
        string[] rendered = SecureWire.Render(messages, client.Nickname);
        if (messages.Length == 1 && SnakeProtocol.TryParseClientProfile(messages[0], out var profile)) client.UpdateSnakeProfile(profile);
        return rendered;
    }
    private async Task SendOwnerAsync(Client client, string[] messages, CancellationToken token)
    {
        ValidateContent(client, messages); byte[] plaintext = SecureWire.Pack(messages);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await BroadcastPacketAsync(5, owner.Encrypt(plaintext), null, token).ConfigureAwait(false); client.Diagnostics.CountMessage(); }
        finally { CryptographicOperations.ZeroMemory(plaintext); gate.Release(); }
    }
    private async Task<bool> ControlAsync(Client client, string message, CancellationToken token)
    {
        if (WhoisProtocol.IsControl(message)) { await WhoisServer.HandleAsync(client, message, token).ConfigureAwait(false); return true; }
        if (!HubStatusProtocol.IsControl(message)) return false;
        if (message.Length > 256 || !client.TryConsumeControlToken()) return true;
        if (message == HubStatusProtocol.Hello) { client.SupportsHubStatus = true; await client.SendAsync(HubStatusProtocol.Capabilities, token).ConfigureAwait(false); }
        else if (client.SupportsHubStatus && HubStatusProtocol.TryParseRequest(message, out string request)) await client.SendAsync(HubStatusProtocol.CreateReply(request, boot.OwnerName, peers.Count + 1), token).ConfigureAwait(false);
        return true;
    }
    private Task NoticeAsync(string text, CancellationToken token)
    {
        if (local != null) QueueLocal(local, new[] { SystemMessageProtocol.Create(SystemMessageKind.SecurityNotice, text) }, token);
        return Task.CompletedTask;
    }
    private void QueueSecure(Client client, byte kind, byte[] data, CancellationToken token) => _ = DeliverAsync(client, SecureWire.EncodeBatch(kind, data), token);
    private void QueueLocal(Client client, string[] messages, CancellationToken token) => _ = DeliverAsync(client, EncodedFrameBatch.Encode(messages), token);
    private Task BroadcastPacketAsync(byte kind, byte[] packet, Client? excluded, CancellationToken token)
    {
        var frames = SecureWire.EncodeBatch(kind, packet);
        foreach (var client in peers.Keys.Where(client => client != excluded)) _ = DeliverAsync(client, frames, token);
        return Task.CompletedTask;
    }
    private async Task DeliverAsync(Client client, EncodedFrameBatch frames, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try { await client.SendEncodedAsync(frames, deadline.Token).ConfigureAwait(false); }
        catch { client.Close(); }
    }
    private Task BroadcastRosterAsync(CancellationToken token)
    {
        static string? Snake(Client? client) => client != null && client.TryGetSnakeProfile(out var profile) && profile.Enabled ? SnakeProtocol.CreateSet(client.Nickname, profile) : null;
        var members = new[] { new SecureRosterEntry(boot.OwnerName, owner.Fingerprint, Snake(local)) }.Concat(peers.Values.Select(peer => new SecureRosterEntry(peer.Client.Nickname, peer.Fingerprint, Snake(peer.Client)))).ToArray();
        var unsigned = new SecureRoster(room, ++revision, members, Array.Empty<byte>());
        var signed = unsigned with { Signature = owner.SignIdentityProof(SecureWire.Proof(3, SecureWire.Json(unsigned))) };
        return BroadcastPacketAsync(7, SecureWire.Json(signed), null, token);
    }
    private async Task RemoveAsync(Client client, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (local == client) { local = null; return; }
            if (!peers.Remove(client, out var peer) || stop.IsCancellationRequested) return;
            byte[] commit = owner.RemoveMember(peer.Fingerprint); owner.MergePendingCommit();
            await BroadcastPacketAsync(6, commit, null, token).ConfigureAwait(false);
            await BroadcastRosterAsync(token).ConfigureAwait(false);
            if (local != null) QueueLocal(local, new[] { SnakeProtocol.CreateRemove(client.Nickname), SystemMessageProtocol.Create(SystemMessageKind.UserLeft, client.Nickname) }, token);
        }
        finally { gate.Release(); }
    }
    internal async Task<bool> KickAsync(string nickname, string reason)
    {
        Client? target;
        await gate.WaitAsync().ConfigureAwait(false);
        try { target = peers.Keys.FirstOrDefault(client => client.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)); if (target != null) identities.Revoke(nickname); }
        finally { gate.Release(); }
        if (target == null) return false;
        await RemoveAsync(target, CancellationToken.None).ConfigureAwait(false);
        using var noticeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await target.SendAsync(SystemMessageProtocol.Create(SystemMessageKind.Kicked, reason), noticeDeadline.Token).ConfigureAwait(false); } catch { }
        target.Close(); return true;
    }
    private void FailClosed() { failed = true; stop.Cancel(); foreach (var client in NetWorker.broadcaster.GetAuthenticatedClients()) client.Close(); }
    public void Dispose()
    {
        stop.Cancel(); pending.CancelAll();
        gate.Wait(); try { owner.Dispose(); certificate.Dispose(); } finally { gate.Release(); }
    }
}
