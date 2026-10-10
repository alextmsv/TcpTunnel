#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;

namespace TCPTunnel.Cryptography;

internal sealed record IdentityChallenge(Guid Id, byte[] Nonce, byte[] Payload);
internal sealed class AuthorizedRoomJoin(string nickname, string fingerprint, byte[] package)
{
    internal string Nickname { get; } = nickname;
    internal string Fingerprint { get; } = fingerprint;
    private readonly byte[] keyPackage = (byte[])package.Clone();
    internal byte[] KeyPackage => (byte[])keyPackage.Clone();
}

internal sealed class RoomIdentityGate
{
    private sealed record Binding(string Fingerprint, bool Allowed);
    private sealed record Pending(string Nickname, string Fingerprint, byte[] PublicKey, byte[] KeyPackage, byte[] ChannelBinding, byte[] Payload, long Issued);
    private readonly object gate = new();
    private readonly Dictionary<string, Binding> bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Pending> pending = new();
    private readonly NativeMlsSession verifier;
    private readonly byte[] room;
    private readonly TimeProvider time;
    private readonly RoomTrustStore? storage;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    internal RoomIdentityGate(byte[] roomId, NativeMlsSession owner, TimeProvider? time = null, RoomTrustStore? storage = null)
    {
        if (roomId.Length != 32) throw new ArgumentException("Invalid room id.", nameof(roomId));
        room = (byte[])roomId.Clone(); verifier = owner; this.time = time ?? TimeProvider.System; this.storage = storage;
        if (storage != null)
            foreach (RoomIdentityBinding binding in storage.Load(room, owner.Fingerprint))
            {
                string nickname = binding.Nickname;
                if (String.IsNullOrWhiteSpace(nickname) || nickname.Length is < 3 or > 20) throw new InvalidDataException("Invalid saved nickname binding.");
                ValidFingerprint(binding.Fingerprint);
                if (!bindings.TryAdd(nickname, new Binding(binding.Fingerprint, binding.Allowed))) throw new InvalidDataException("Duplicate saved nickname binding.");
            }
    }

    internal void Approve(string nickname, string fingerprint)
    {
        nickname = ValidNickname(nickname); ValidFingerprint(fingerprint);
        lock (gate)
        {
            if (bindings.TryGetValue(nickname, out Binding? existing) && !String.Equals(existing.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This nickname is already bound to another identity.");
            if (!bindings.ContainsKey(nickname) && bindings.Count >= 64) throw new InvalidOperationException("Room identity limit reached.");
            if (bindings.Any(item => !String.Equals(item.Key, nickname, StringComparison.OrdinalIgnoreCase) && NicknamePolicy.Same(item.Key, nickname) &&
                (NicknamePolicy.IsValid(item.Key) || !item.Value.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("This nickname is visually confusable with a reserved identity.");
            bindings.TryGetValue(nickname, out Binding? previous);
            bindings[nickname] = new Binding(fingerprint.ToUpperInvariant(), true);
            try { Save(); }
            catch { if (previous == null) bindings.Remove(nickname); else bindings[nickname] = previous; throw; }
        }
    }

    internal bool IsApproved(string nickname, string fingerprint)
    {
        nickname = ValidNickname(nickname); ValidFingerprint(fingerprint);
        lock (gate)
        {
            if (!bindings.TryGetValue(nickname, out Binding? binding)) return false;
            if (!String.Equals(binding.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The nickname's pinned identity changed.");
            if (!binding.Allowed) throw new UnauthorizedAccessException("This identity was revoked.");
            return true;
        }
    }

    internal void Revoke(string nickname)
    {
        nickname = ValidNickname(nickname);
        lock (gate)
        {
            if (bindings.TryGetValue(nickname, out Binding? existing))
            {
                bindings[nickname] = existing with { Allowed = false };
                try { Save(); } catch { bindings[nickname] = existing; throw; }
            }
            foreach (Guid id in pending.Where(item => String.Equals(item.Value.Nickname, nickname, StringComparison.OrdinalIgnoreCase)).Select(item => item.Key).ToArray()) pending.Remove(id);
        }
    }

    private void Save() => storage?.Save(room, verifier.Fingerprint, bindings.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
        .Select(item => new RoomIdentityBinding(item.Key, item.Value.Fingerprint, item.Value.Allowed)).ToArray());

    internal IdentityChallenge Issue(string nickname, byte[] keyPackage, byte[] channelBinding)
    {
        nickname = ValidNickname(nickname);
        if (channelBinding.Length != 32) throw new ArgumentException("A connection binding is required.", nameof(channelBinding));
        lock (gate)
        {
            foreach (Guid expiredId in pending.Where(item => time.GetElapsedTime(item.Value.Issued) >= Lifetime).Select(item => item.Key).ToArray()) pending.Remove(expiredId);
            if (pending.Count >= 64) throw new InvalidOperationException("Too many pending identity proofs.");
            if (!bindings.TryGetValue(nickname, out Binding? binding) || !binding.Allowed) throw new UnauthorizedAccessException("This identity is not approved for the room.");
            byte[] publicKey = verifier.KeyPackagePublicKey(keyPackage);
            string fingerprint = NativeMlsSession.FingerprintOf(publicKey);
            if (!String.Equals(binding.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The nickname's pinned identity changed.");
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            byte[] payload = Payload(room, verifier.Fingerprint, nickname, nonce, channelBinding, keyPackage);
            Guid id = Guid.NewGuid();
            pending.Add(id, new Pending(nickname, fingerprint, publicKey, (byte[])keyPackage.Clone(), (byte[])channelBinding.Clone(), payload, time.GetTimestamp()));
            return new IdentityChallenge(id, nonce, (byte[])payload.Clone());
        }
    }

    internal bool Complete(Guid challengeId, byte[] signature, byte[] channelBinding, out AuthorizedRoomJoin? accepted)
    {
        lock (gate)
        {
            accepted = null;
            if (!pending.Remove(challengeId, out Pending? challenge) || time.GetElapsedTime(challenge.Issued) >= Lifetime) return false;
            if (channelBinding.Length != 32 || !CryptographicOperations.FixedTimeEquals(challenge.ChannelBinding, channelBinding)) return false;
            if (!bindings.TryGetValue(challenge.Nickname, out Binding? binding) || !binding.Allowed || !String.Equals(binding.Fingerprint, challenge.Fingerprint, StringComparison.OrdinalIgnoreCase)) return false;
            if (!verifier.VerifyIdentityProof(challenge.PublicKey, challenge.Payload, signature)) return false;
            accepted = new AuthorizedRoomJoin(challenge.Nickname, challenge.Fingerprint, challenge.KeyPackage);
            return true;
        }
    }

    internal static byte[] Respond(NativeMlsSession client, IdentityChallenge challenge, byte[] expectedRoom,
        string expectedOwnerFingerprint, string nickname, byte[] channelBinding, byte[] keyPackage)
    {
        if (challenge.Nonce.Length != 32 || expectedRoom.Length != 32 || channelBinding.Length != 32) throw new InvalidDataException("Invalid identity challenge.");
        if (!client.KeyPackagePublicKey(keyPackage).SequenceEqual(client.PublicKey)) throw new UnauthorizedAccessException("The key package belongs to another identity.");
        byte[] expected = Payload(expectedRoom, expectedOwnerFingerprint, ValidNickname(nickname), challenge.Nonce, channelBinding, keyPackage);
        if (!expected.AsSpan().SequenceEqual(challenge.Payload)) throw new UnauthorizedAccessException("Identity proof context does not match the verified room and connection.");
        return client.SignIdentityProof(expected);
    }

    private static byte[] Payload(byte[] room, string ownerFingerprint, string nickname, byte[] nonce, byte[] channel, byte[] package)
    {
        ValidFingerprint(ownerFingerprint);
        byte[] name = Encoding.UTF8.GetBytes(nickname);
        byte[] payload = new byte[162 + name.Length];
        payload[0] = 1; room.CopyTo(payload, 1); Convert.FromHexString(ownerFingerprint).CopyTo(payload, 33);
        nonce.CopyTo(payload, 65); channel.CopyTo(payload, 97); SHA256.HashData(package).CopyTo(payload, 129);
        payload[161] = checked((byte)name.Length); name.CopyTo(payload, 162);
        return payload;
    }
    private static string ValidNickname(string nickname)
    {
        if (!NicknamePolicy.IsValid(nickname))
            throw new ArgumentException("Invalid secure-room nickname.", nameof(nickname));
        return nickname;
    }
    private static void ValidFingerprint(string fingerprint)
    {
        if (String.IsNullOrEmpty(fingerprint) || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid identity fingerprint.", nameof(fingerprint));
    }
}
