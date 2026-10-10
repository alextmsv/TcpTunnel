using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TCPTunnel.Cryptography;

if (args.Length == 1 && args[0] == "--peer")
{
    using var session = new NativeMlsSession();
    using var reader = new BinaryReader(Console.OpenStandardInput());
    using var writer = new BinaryWriter(Console.OpenStandardOutput());
    Wire.Write(writer, session.CreateKeyPackage());
    while (true)
    {
        int command;
        try { command = reader.ReadByte(); } catch (EndOfStreamException) { break; }
        if (command == 0) break;
        byte[] input = Wire.Read(reader);
        if (command == 1) { session.Join(input); Wire.Write(writer, Array.Empty<byte>()); }
        else if (command == 2)
        {
            MlsReceived result = session.Process(input);
            if (result.Kind != MlsReceivedKind.Application) throw new InvalidDataException("Expected an application message.");
            Wire.Write(writer, Convert.FromHexString(result.SenderFingerprint!)); Wire.Write(writer, result.Content);
        }
        else throw new InvalidDataException("Unknown prototype command.");
    }
    return;
}

if (args.Length == 3 && args[0] == "--identity-peer")
{
    var store = new ProfileIdentityStore(args[1]);
    var reference = JsonSerializer.Deserialize<ProfileIdentityReference>(File.ReadAllText(args[2])) ?? throw new InvalidDataException();
    using var session = store.Open(reference);
    Console.WriteLine(session.Fingerprint);
    return;
}

if (args.Length == 1 && args[0] == "--benchmark")
{
    var measurements = new List<object>();
    foreach (int count in new[] { 2, 16, 64 })
    {
        using var group = new TestGroup(count);
        foreach (var (length, iterations) in new[] { (128, 300), (5760, 40), (2_880_000, 20) })
        {
            byte[] plaintext = RandomNumberGenerator.GetBytes(length);
            byte[] warmup = group.Owner.Encrypt(plaintext); group.Members[1].Process(warmup);
            var samples = new List<double>();
            long before = GC.GetTotalAllocatedBytes(true);
            int wireSize = 0;
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                long start = Stopwatch.GetTimestamp();
                byte[] ciphertext = group.Owner.Encrypt(plaintext);
                MlsReceived received = group.Members[1].Process(ciphertext);
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                Check.That(received.Content.AsSpan().SequenceEqual(plaintext), "Benchmark changed plaintext");
                wireSize = ciphertext.Length;
            }
            samples.Sort();
            measurements.Add(new { members = count, plaintextBytes = length, ciphertextBytes = wireSize,
                encryptionAndOneRecipientDecryptionP50Ms = samples[samples.Count / 2],
                encryptionAndOneRecipientDecryptionP95Ms = samples[Math.Min(samples.Count - 1, (int)(samples.Count * .95))],
                managedBytesPerRoundtrip = (GC.GetTotalAllocatedBytes(true) - before) / iterations });
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

if (args.Length > 0 && args[0] != "--self-test") throw new ArgumentException("Use --self-test or --benchmark.");
string? referencePeer = args.Length == 3 && args[1] == "--reference-peer" ? Path.GetFullPath(args[2]) : null;
if (referencePeer != null && !File.Exists(referencePeer)) throw new FileNotFoundException("Reference peer not found.", referencePeer);
var tests = new (string Name, Func<Task> Run)[]
{
    ("Roundtrip, sender identity and replay rejection", () =>
    {
        using var group = new TestGroup(2);
        byte[] plaintext = Encoding.UTF8.GetBytes("Привет, MLS 🐍");
        byte[] ciphertext = group.Owner.Encrypt(plaintext);
        Check.That(!Contains(ciphertext, plaintext), "Plaintext occurs in ciphertext");
        MlsReceived received = group.Members[1].Process(ciphertext);
        Check.That(received.Kind == MlsReceivedKind.Application && received.SenderFingerprint == group.Owner.Fingerprint && received.Content.SequenceEqual(plaintext), "Roundtrip/identity mismatch");
        Check.Rejected(() => group.Members[1].Process(ciphertext));
        return Task.CompletedTask;
    }),
    ("Tamper rejection and continued messaging", () =>
    {
        using var group = new TestGroup(2);
        byte[] altered = group.Owner.Encrypt(Encoding.UTF8.GetBytes("tamper")); altered[^1] ^= 1;
        Check.Rejected(() => group.Members[1].Process(altered));
        byte[] valid = group.Owner.Encrypt(Encoding.UTF8.GetBytes("after tamper"));
        Check.That(Encoding.UTF8.GetString(group.Members[1].Process(valid).Content) == "after tamper", "State damaged by rejected packet");
        return Task.CompletedTask;
    }),
    ("Membership changes, pending barrier and revoked keys", () =>
    {
        using var group = new TestGroup(2);
        byte[] old = group.Owner.Encrypt(Encoding.UTF8.GetBytes("before join"));
        using var newMember = new NativeMlsSession();
        MembershipChange change = group.Owner.AddMember(newMember.CreateKeyPackage());
        Check.Rejected(() => group.Owner.Encrypt(new byte[] { 1 }));
        group.Owner.MergePendingCommit(); group.Members[1].Process(change.Commit); newMember.Join(change.Welcome);
        Check.Rejected(() => newMember.Process(old));
        byte[] removal = group.Owner.RemoveMember(group.Members[1].Fingerprint);
        group.Owner.MergePendingCommit();
        Check.That(newMember.Process(removal).Kind == MlsReceivedKind.Commit, "Remaining member did not merge removal");
        Check.That(group.Members[1].Process(removal).Kind == MlsReceivedKind.Removed, "Removed member did not leave");
        byte[] next = group.Owner.Encrypt(Encoding.UTF8.GetBytes("after kick"));
        Check.Rejected(() => group.Members[1].Process(next));
        Check.Rejected(() => group.Members[1].Encrypt(new byte[] { 1 }));
        Check.That(Encoding.UTF8.GetString(newMember.Process(next).Content) == "after kick", "Remaining member cannot decrypt");
        return Task.CompletedTask;
    }),
    ("Malformed input and wrong room", () =>
    {
        using var group = new TestGroup(2);
        using var wrongRoom = new TestGroup(2);
        Check.Rejected(() => group.Members[1].Process(wrongRoom.Owner.Encrypt(new byte[] { 1 })));
        for (int length = 0; length < 256; length++) Check.Rejected(() => group.Members[1].Process(RandomNumberGenerator.GetBytes(length)));
        byte[] valid = group.Owner.Encrypt(new byte[] { 7 });
        Check.That(group.Members[1].Process(valid).Content.SequenceEqual(new byte[] { 7 }), "Malformed input damaged group");
        return Task.CompletedTask;
    }),
    ("64-member group and member limit", () =>
    {
        using var group = new TestGroup(64);
        byte[] encrypted = group.Owner.Encrypt(Encoding.UTF8.GetBytes("all members"));
        foreach (NativeMlsSession member in group.Members.Skip(1))
            Check.That(Encoding.UTF8.GetString(member.Process(encrypted).Content) == "all members", "Group member cannot decrypt");
        using var extra = new NativeMlsSession();
        Check.Rejected(() => group.Owner.AddMember(extra.CreateKeyPackage()));
        return Task.CompletedTask;
    }),
    ("Managed concurrent calls and disposal", async () =>
    {
        using var group = new TestGroup(2);
        Task<byte[]>[] sends = Enumerable.Range(0, 8).Select(index => Task.Run(() => group.Owner.Encrypt(Encoding.UTF8.GetBytes("parallel-" + index)))).ToArray();
        byte[][] ciphertexts = await Task.WhenAll(sends);
        var received = ciphertexts.Select(ciphertext => Encoding.UTF8.GetString(group.Members[1].Process(ciphertext).Content)).ToHashSet();
        Check.That(received.Count == 8, "Concurrent results were mixed");
        using var disposable = new NativeMlsSession(); disposable.Dispose();
        try { disposable.CreateKeyPackage(); throw new Exception("Disposed handle remained usable"); } catch (ObjectDisposedException) { }
    }),
    ("Payload bound includes encryption overhead", () =>
    {
        using var group = new TestGroup(2);
        try { group.Owner.Encrypt(new byte[8 * 1024 * 1024]); throw new Exception("Unreceivable maximum-sized message was accepted"); }
        catch (ArgumentOutOfRangeException) { }
        byte[] encrypted = group.Owner.Encrypt(new byte[] { 1 });
        Check.That(group.Members[1].Process(encrypted).Content.SequenceEqual(new byte[] { 1 }), "Payload rejection damaged session");
        return Task.CompletedTask;
    }),
    ("DPAPI profile identity persistence and pinning", () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "tcptunnel-identity-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileIdentityStore(directory);
            ProfileIdentityReference reference = store.Create(Guid.NewGuid());
            using var first = store.Open(reference);
            using var second = store.Open(reference);
            Check.That(first.Fingerprint == second.Fingerprint && first.Fingerprint == reference.Fingerprint, "Restoring profile changed identity");
            byte[] secret = first.ExportIdentityMaterial();
            try
            {
                byte[] file = File.ReadAllBytes(Path.Combine(directory, reference.IdentityId.ToString("N") + ".key"));
                Check.That(!Contains(file, secret), "Identity file contains plaintext key material");
                Check.That(!JsonSerializer.Serialize(reference).Contains(Convert.ToBase64String(secret)), "Public profile reference contains private material");
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
            try { using var changed = store.Open(reference with { Fingerprint = new string('0', 64) }); throw new Exception("Changed public identity pin was accepted"); }
            catch (InvalidDataException) { }
            ProfileIdentityReference other = store.Create(Guid.NewGuid());
            Check.That(other.Fingerprint != reference.Fingerprint, "Distinct profiles share an identity");
            try { store.Create(reference.IdentityId); throw new Exception("Existing identity overwritten"); } catch (IOException) { }
            first.CreateGroup(RandomNumberGenerator.GetBytes(32));
            using var peer = store.Open(other);
            MembershipChange change = first.AddMember(peer.CreateKeyPackage());
            first.MergePendingCommit(); peer.Join(change.Welcome);
            byte[] afterRejection = first.Encrypt(new byte[] { 1, 2, 3 });
            MlsReceived received = peer.Process(afterRejection);
            Check.That(received.SenderFingerprint == reference.Fingerprint && received.Content.SequenceEqual(new byte[] { 1, 2, 3 }), "Restored signer is unusable");
        }
        finally { DeleteTestDirectory(directory); }
        return Task.CompletedTask;
    }),
    ("Identity corruption, profile binding and missing keys fail closed", () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "tcptunnel-identity-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileIdentityStore(directory);
            ProfileIdentityReference reference = store.Create(Guid.NewGuid());
            string path = Path.Combine(directory, reference.IdentityId.ToString("N") + ".key");
            byte[] original = File.ReadAllBytes(path);
            Guid otherId = Guid.NewGuid();
            File.WriteAllBytes(Path.Combine(directory, otherId.ToString("N") + ".key"), original);
            try { using var copied = store.Open(reference with { IdentityId = otherId }); throw new Exception("Key copied to a different profile was accepted"); }
            catch (System.ComponentModel.Win32Exception) { }
            byte[] modified = (byte[])original.Clone(); modified[^1] ^= 1; File.WriteAllBytes(path, modified);
            try { using var corrupted = store.Open(reference); throw new Exception("Corrupted key accepted"); }
            catch (System.ComponentModel.Win32Exception) { }
            File.Delete(path);
            try { using var missing = store.Open(reference); throw new Exception("Missing key silently regenerated"); } catch (FileNotFoundException) { }
            Check.That(!File.Exists(path), "Opening missing key generated a replacement");
        }
        finally { DeleteTestDirectory(directory); }
        return Task.CompletedTask;
    }),
    ("Same nickname requires the pinned private key", () =>
    {
        using var owner = new NativeMlsSession();
        using var alice = new NativeMlsSession();
        using var impostor = new NativeMlsSession();
        byte[] room = RandomNumberGenerator.GetBytes(32), channel = RandomNumberGenerator.GetBytes(32);
        var admissions = new RoomIdentityGate(room, owner);
        admissions.Approve("Alice", alice.Fingerprint);
        byte[] impostorPackage = impostor.CreateKeyPackage();
        try { admissions.Issue("Alice", impostorPackage, channel); throw new Exception("Impostor reused an offline nickname"); } catch (UnauthorizedAccessException) { }
        try { admissions.Issue("AnotherNick", impostorPackage, channel); throw new Exception("Unknown identity admitted under a free nickname"); } catch (UnauthorizedAccessException) { }
        byte[] package = alice.CreateKeyPackage();
        IdentityChallenge challenge = admissions.Issue("Alice", package, channel);
        byte[] proof = RoomIdentityGate.Respond(alice, challenge, room, owner.Fingerprint, "Alice", channel, package);
        Check.That(admissions.Complete(challenge.Id, proof, channel, out AuthorizedRoomJoin? allowed) && allowed!.Fingerprint == alice.Fingerprint, "Pinned identity was rejected");
        Check.That(allowed!.KeyPackage.SequenceEqual(package), "Admission returned a different key package");
        Check.That(!admissions.Complete(challenge.Id, proof, channel, out _), "Proof replay accepted");
        owner.CreateGroup(room);
        MembershipChange change = owner.AddMember(allowed.KeyPackage); owner.MergePendingCommit(); alice.Join(change.Welcome);
        byte[] message = owner.Encrypt(new byte[] { 1, 2 });
        Check.That(alice.Process(message).Content.SequenceEqual(new byte[] { 1, 2 }), "Approved member could not read chat");
        Check.Rejected(() => impostor.Join(change.Welcome));
        admissions.Revoke("Alice");
        try { admissions.Issue("Alice", package, channel); throw new Exception("Revoked identity automatically rejoined"); } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }),
    ("Identity proof binds room, connection and key package", () =>
    {
        using var owner = new NativeMlsSession(); using var alice = new NativeMlsSession(); using var another = new NativeMlsSession();
        byte[] room = RandomNumberGenerator.GetBytes(32), channel = RandomNumberGenerator.GetBytes(32), otherChannel = RandomNumberGenerator.GetBytes(32);
        var admissions = new RoomIdentityGate(room, owner); admissions.Approve("Alice", alice.Fingerprint);
        byte[] package = alice.CreateKeyPackage(), otherPackage = another.CreateKeyPackage();
        IdentityChallenge challenge = admissions.Issue("Alice", package, channel);
        foreach (Action altered in new Action[] {
            () => RoomIdentityGate.Respond(alice, challenge, RandomNumberGenerator.GetBytes(32), owner.Fingerprint, "Alice", channel, package),
            () => RoomIdentityGate.Respond(alice, challenge, room, owner.Fingerprint, "Alice", otherChannel, package),
            () => RoomIdentityGate.Respond(alice, challenge, room, owner.Fingerprint, "Alice", channel, otherPackage) })
        {
            try { altered(); throw new Exception("Mismatched proof context was signed"); } catch (UnauthorizedAccessException) { }
        }
        byte[] proof = RoomIdentityGate.Respond(alice, challenge, room, owner.Fingerprint, "Alice", channel, package);
        Check.That(!admissions.Complete(challenge.Id, proof, otherChannel, out _), "Proof accepted on a different connection");
        Check.That(!admissions.Complete(challenge.Id, proof, channel, out _), "Consumed proof reused after failure");
        IdentityChallenge fresh = admissions.Issue("Alice", package, channel);
        byte[] forged = another.SignIdentityProof(fresh.Payload);
        Check.That(!admissions.Complete(fresh.Id, forged, channel, out _), "Another private key signed as Alice");
        return Task.CompletedTask;
    }),
    ("Room nickname pins and revocation survive restart", () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "tcptunnel-identity-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var identities = new ProfileIdentityStore(Path.Combine(directory, "keys"));
            var trust = new RoomTrustStore(Path.Combine(directory, "rooms"));
            ProfileIdentityReference ownerReference = identities.Create(Guid.NewGuid()), aliceReference = identities.Create(Guid.NewGuid());
            byte[] room = RandomNumberGenerator.GetBytes(32), channel = RandomNumberGenerator.GetBytes(32);
            using (var owner = identities.Open(ownerReference)) new RoomIdentityGate(room, owner, storage: trust).Approve("Alice", aliceReference.Fingerprint);
            using var restoredOwner = identities.Open(ownerReference);
            using var alice = identities.Open(aliceReference);
            using var impostor = new NativeMlsSession();
            var restarted = new RoomIdentityGate(room, restoredOwner, storage: trust);
            byte[] impostorPackage = impostor.CreateKeyPackage();
            try { restarted.Issue("Alice", impostorPackage, channel); throw new Exception("Offline nickname pin was lost on restart"); } catch (UnauthorizedAccessException) { }
            byte[] package = alice.CreateKeyPackage();
            IdentityChallenge challenge = restarted.Issue("Alice", package, channel);
            byte[] proof = RoomIdentityGate.Respond(alice, challenge, room, restoredOwner.Fingerprint, "Alice", channel, package);
            Check.That(restarted.Complete(challenge.Id, proof, channel, out _), "Approved identity lost on restart");
            restarted.Revoke("Alice");
            var afterRevocation = new RoomIdentityGate(room, restoredOwner, storage: trust);
            try { afterRevocation.Issue("Alice", package, channel); throw new Exception("Revoked identity restored on restart"); } catch (UnauthorizedAccessException) { }
            string path = Path.Combine(directory, "rooms", Convert.ToHexString(room) + ".trust");
            byte[] file = File.ReadAllBytes(path); file[^1] ^= 1; File.WriteAllBytes(path, file);
            try { new RoomIdentityGate(room, restoredOwner, storage: trust); throw new Exception("Corrupted trust data loaded"); } catch (System.ComponentModel.Win32Exception) { }
        }
        finally { DeleteTestDirectory(directory); }
        return Task.CompletedTask;
    }),
    ("Expired proof and failed persistence do not grant admission", () =>
    {
        using var owner = new NativeMlsSession(); using var alice = new NativeMlsSession();
        byte[] room = RandomNumberGenerator.GetBytes(32), channel = RandomNumberGenerator.GetBytes(32), package = alice.CreateKeyPackage();
        var clock = new ManualTimeProvider();
        var admissions = new RoomIdentityGate(room, owner, clock); admissions.Approve("Alice", alice.Fingerprint);
        IdentityChallenge challenge = admissions.Issue("Alice", package, channel);
        byte[] proof = RoomIdentityGate.Respond(alice, challenge, room, owner.Fingerprint, "Alice", channel, package);
        clock.Advance(TimeSpan.FromSeconds(31));
        Check.That(!admissions.Complete(challenge.Id, proof, channel, out _), "Expired proof accepted");
        string directory = Path.Combine(Path.GetTempPath(), "tcptunnel-identity-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string blocker = Path.Combine(directory, "blocked");
            var failing = new RoomIdentityGate(room, owner, storage: new RoomTrustStore(blocker));
            File.WriteAllText(blocker, "not a directory");
            try { failing.Approve("Alice", alice.Fingerprint); throw new Exception("Failed trust persistence was ignored"); } catch (IOException) { }
            try { failing.Issue("Alice", package, channel); throw new Exception("Unpersisted identity was admitted"); } catch (UnauthorizedAccessException) { }
        }
        finally { DeleteTestDirectory(directory); }
        return Task.CompletedTask;
    }),
    ("Profile identity restores in an independent process", async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "tcptunnel-identity-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileIdentityStore(directory); ProfileIdentityReference reference = store.Create(Guid.NewGuid());
            string metadata = Path.Combine(directory, "public-reference.json"); File.WriteAllText(metadata, JsonSerializer.Serialize(reference));
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException();
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TCPTunnel.CryptoPrototype.dll"));
            start.ArgumentList.Add("--identity-peer"); start.ArgumentList.Add(directory); start.ArgumentList.Add(metadata);
            using Process child = Process.Start(start) ?? throw new InvalidOperationException();
            Task<string> result = child.StandardOutput.ReadToEndAsync(), error = child.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await child.WaitForExitAsync(deadline.Token); }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            Check.That(child.ExitCode == 0 && (await result).Trim() == reference.Fingerprint, "Independent identity restore failed: " + await error);
        }
        finally { DeleteTestDirectory(directory); }
    }),
    ("Native handle resource bound", () =>
    {
        var sessions = new List<NativeMlsSession>();
        try
        {
            for (int count = 0; count < 128; count++) sessions.Add(new NativeMlsSession());
            Check.Rejected(() => { using var tooMany = new NativeMlsSession(); });
        }
        finally { foreach (NativeMlsSession session in sessions) session.Dispose(); }
        using var afterCleanup = new NativeMlsSession();
        Check.That(afterCleanup.CreateKeyPackage().Length > 0, "Native resources were not released");
        return Task.CompletedTask;
    }),
    ("Independent recipient process", async () =>
    {
        string processPath = referencePeer ?? Environment.ProcessPath ?? throw new InvalidOperationException();
        var start = new ProcessStartInfo(processPath) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TCPTunnel.CryptoPrototype.dll"));
        start.ArgumentList.Add("--peer");
        using Process peer = Process.Start(start) ?? throw new InvalidOperationException("Could not start recipient");
        Task<string> stderr = peer.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var owner = new NativeMlsSession(); owner.CreateGroup(RandomNumberGenerator.GetBytes(32));
            using var writer = new BinaryWriter(peer.StandardInput.BaseStream, Encoding.UTF8, leaveOpen: true);
            using var reader = new BinaryReader(peer.StandardOutput.BaseStream, Encoding.UTF8, leaveOpen: true);
            byte[] package = await Task.Run(() => Wire.Read(reader)).WaitAsync(deadline.Token);
            MembershipChange change = owner.AddMember(package); owner.MergePendingCommit();
            writer.Write((byte)1); Wire.Write(writer, change.Welcome);
            Check.That((await Task.Run(() => Wire.Read(reader)).WaitAsync(deadline.Token)).Length == 0, "Independent join failed");
            byte[] plaintext = Encoding.UTF8.GetBytes("independent recipient 🐍");
            writer.Write((byte)2); Wire.Write(writer, owner.Encrypt(plaintext));
            byte[] identity = await Task.Run(() => Wire.Read(reader)).WaitAsync(deadline.Token);
            byte[] decoded = await Task.Run(() => Wire.Read(reader)).WaitAsync(deadline.Token);
            Check.That(Convert.ToHexString(identity) == owner.Fingerprint && decoded.SequenceEqual(plaintext), "Independent process result mismatch");
            writer.Write((byte)0); writer.Flush();
            await peer.WaitForExitAsync(deadline.Token);
            Check.That(peer.ExitCode == 0, "Independent process failed: " + await stderr);
        }
        finally { if (!peer.HasExited) { peer.Kill(); await peer.WaitForExitAsync(); } }
    })
};
int failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + error.Message); }
}
Environment.ExitCode = failed;

static bool Contains(byte[] ciphertext, byte[] plaintext)
{
    for (int index = 0; index <= ciphertext.Length - plaintext.Length; index++)
        if (ciphertext.AsSpan(index, plaintext.Length).SequenceEqual(plaintext)) return true;
    return false;
}

static void DeleteTestDirectory(string directory)
{
    string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    string resolved = Path.GetFullPath(directory);
    if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("tcptunnel-identity-test-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected identity test directory.");
    if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
}

internal sealed class TestGroup : IDisposable
{
    internal List<NativeMlsSession> Members { get; } = new();
    internal NativeMlsSession Owner => Members[0];
    internal TestGroup(int count)
    {
        try
        {
            Members.Add(new NativeMlsSession()); Owner.CreateGroup(RandomNumberGenerator.GetBytes(32));
            for (int index = 1; index < count; index++)
            {
                var member = new NativeMlsSession();
                try
                {
                    MembershipChange change = Owner.AddMember(member.CreateKeyPackage());
                    Owner.MergePendingCommit();
                    foreach (NativeMlsSession existing in Members.Skip(1)) existing.Process(change.Commit);
                    member.Join(change.Welcome); Members.Add(member);
                }
                catch { member.Dispose(); throw; }
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { foreach (NativeMlsSession member in Members) member.Dispose(); Members.Clear(); }
}
internal static class Check
{
    internal static void That(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Rejected(Action action) { try { action(); } catch (MlsException) { return; } throw new Exception("Unsafe operation was accepted"); }
}
internal static class Wire
{
    internal static void Write(BinaryWriter writer, byte[] data) { writer.Write(data.Length); writer.Write(data); writer.Flush(); }
    internal static byte[] Read(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 8 * 1024 * 1024 + 256 * 1024) throw new InvalidDataException("Prototype frame size exceeds bound.");
        byte[] data = reader.ReadBytes(length);
        if (data.Length != length) throw new EndOfStreamException();
        return data;
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private long timestamp;
    public override long GetTimestamp() => timestamp;
    internal void Advance(TimeSpan elapsed) => timestamp += (long)(elapsed.TotalSeconds * TimestampFrequency);
}
