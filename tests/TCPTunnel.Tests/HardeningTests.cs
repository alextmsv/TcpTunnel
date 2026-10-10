using System.Security.Cryptography;
using TCPTunnel;
using TCPTunnel.Cryptography;

internal static class HardeningTests
{
    public static async Task<bool> QueuedCancellationReleasesCapacitySelfTest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<byte>(); int aborted = 0;
        var queue = new SerialSendQueue(async (bytes, token) => {
            started.TrySetResult(); await release.Task.WaitAsync(token); written.Add(bytes.Span[0]);
        }, () => Interlocked.Increment(ref aborted), 2, 2);
        try
        {
            Task first = queue.Enqueue(new[] { new byte[] { 1 } }, default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            using var cancel = new CancellationTokenSource();
            Task second = queue.Enqueue(new[] { new byte[] { 2 } }, cancel.Token);
            cancel.Cancel();
            try { await second.WaitAsync(TimeSpan.FromSeconds(1)); return false; } catch (OperationCanceledException) { }
            Task third = queue.Enqueue(new[] { new byte[] { 3 } }, default);
            release.TrySetResult(); await Task.WhenAll(first, third).WaitAsync(TimeSpan.FromSeconds(1));
            return written.SequenceEqual(new byte[] { 1, 3 }) && aborted == 0;
        }
        finally { release.TrySetResult(); queue.Close(); await queue.Completion; }
    }

    public static bool SeparateAdmissionQuotasSelfTest()
    {
        var admission = new ConnectionAdmission(); var leases = new List<ConnectionAdmission.Lease>();
        try
        {
            for (int i = 0; i < 4; i++) leases.Add(admission.TryAcquire("attacker", false) ?? throw new Exception("Unexpected quota."));
            if (admission.TryAcquire("attacker", false) != null) return false;
            for (int i = 0; i < 20; i++) leases.Add(admission.TryAcquire("source" + i, false) ?? throw new Exception("Unexpected quota."));
            if (admission.TryAcquire("other", false) != null) return false;
            using var local = admission.TryAcquire("owner", true);
            if (local == null || !local.Promote() || !leases[0].Promote()) return false;
            using var replacement = admission.TryAcquire("attacker", false);
            if (replacement == null) return false;
            leases[1].Dispose(); leases[1].Dispose();
            using var fresh = admission.TryAcquire("fresh", false);
            return fresh != null;
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    public static bool NicknameAndConsoleSpoofingSelfTest()
    {
        foreach (string name in new[] { "ab\u200Bc", "ab\uFEFFc", "ab\u202Ec", "ａｂｃ", "a\u0301bc", "ab\u001Bc" })
            if (NicknamePolicy.IsValid(name)) return false;
        return NicknamePolicy.IsValid("Алекс-123") && NicknamePolicy.IsValid("test.user") &&
            NicknamePolicy.Same("paypal", "pаypаl") && !NicknamePolicy.Same("Alice", "Bob") &&
            ConsoleText.Safe("a\u001B[2J\u202Eb\nc") == "a [2J b c";
    }

    public static bool EndpointPinAndLegacyReservationsSelfTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var endpoints = new EndpointTrustStore(Path.Combine(directory, "endpoints"));
            string first = new('A', 64), other = new('B', 64);
            endpoints.Save("tcp:Example:9091", first); endpoints.Check("TCP:example:9091", first);
            try { endpoints.Save("tcp:example:9091", other); return false; } catch (UnauthorizedAccessException) { }
            endpoints.Check("tcp:example:9091", first);
            endpoints.Save("bt:000000000001", other);
            using var owner = new NativeMlsSession(); byte[] room = RandomNumberGenerator.GetBytes(32);
            var storage = new RoomTrustStore(Path.Combine(directory, "room"));
            storage.Save(room, owner.Fingerprint, new[] { new RoomIdentityBinding("ｐａｙｐａｌ", first, true) });
            var identities = new RoomIdentityGate(room, owner, storage: storage);
            identities.Approve("paypal", first);
            try { identities.Approve("pаypаl", other); return false; } catch (InvalidOperationException) { }
            return storage.Load(room, owner.Fingerprint).Any(binding => binding.Nickname == "ｐａｙｐａｌ" && binding.Fingerprint == first);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (string child in Directory.EnumerateDirectories(directory).Reverse()) Directory.Delete(child);
            Directory.Delete(directory);
        }
    }

    public static bool LegacyProfileKeyMigrationSelfTest()
    {
        string? previous = Environment.GetEnvironmentVariable("TCPTUNNEL_DATA_HOME");
        var original = ApplicationSettings.ParseProfile(ApplicationSettings.ParseLines(ApplicationSettings.Serialize(ApplicationSettings.Current)), new AppProfile());
        string nickname = NetWorker.nickname;
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-profile-migration-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", directory);
        try
        {
            string profiles = Path.Combine(directory, "profiles"); Directory.CreateDirectory(profiles);
            Guid id = Guid.NewGuid(); string fingerprint = new('A', 64);
            File.WriteAllLines(Path.Combine(profiles, "old.cfg"), ApplicationSettings.Serialize(new AppProfile {
                Nickname = "old\u200Bname", IdentityId = id, IdentityFingerprint = fingerprint
            }));
            var saved = ApplicationSettings.GetSavedProfiles(profiles);
            if (saved.Count != 1 || !ApplicationSettings.SelectSavedProfile(saved[0], false, false, out _)) return false;
            NetWorker.nickname = "safeNewName"; ApplicationSettings.SaveCurrentProfile(NetWorker.nickname);
            var migrated = ApplicationSettings.GetSavedProfiles(profiles).Single(item => item.Nickname == NetWorker.nickname);
            var loaded = ApplicationSettings.ParseProfile(ApplicationSettings.ParseLines(File.ReadAllLines(migrated.Path)), new AppProfile());
            return loaded.IdentityId == id && loaded.IdentityFingerprint == fingerprint && !NicknamePolicy.IsValid(saved[0].Nickname);
        }
        finally
        {
            foreach (var property in typeof(AppProfile).GetFields()) property.SetValue(ApplicationSettings.Current, property.GetValue(original));
            NetWorker.nickname = nickname; Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", previous);
        }
    }
}
