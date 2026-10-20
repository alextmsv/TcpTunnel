using System.Collections.Concurrent;
using System.Security.Cryptography;
using TCPTunnel;
using TCPTunnel.Cryptography;

internal static class FileTransferTests
{
    internal static bool FileTransferValidationSelfTest()
    {
        foreach (string bad in new[] { "../x", "a/b", "C:x", "CON.exe", "CON .exe", "com1.zip", "LPT².txt", "x.", "x ", "a\u001bb", "a\u202eb", "a\u2028b", "..", "" })
            if (FileTransferProtocol.SafeName(bad)) throw new Exception("Unsafe filename accepted: " + bad);
        foreach (string good in new[] { "файл.exe", "archive.tar.gz", "photo.gif", "script.ps1", "без расширения", "file.xyz" })
            if (!FileTransferProtocol.SafeName(good)) throw new Exception("Safe filename rejected: " + good);
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var packet = new FileTransferPacket("offer", Guid.NewGuid().ToString("N"), "bob", Name: "file.exe", Size: 123, Key: Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        string frame = FileTransferProtocol.Encode(packet);
        if (!FileTransferProtocol.TryBind(frame, "alice", out string bound) || !FileTransferProtocol.TryParse(bound, out var parsed) || parsed.Sender != "alice") return false;
        if (FileTransferProtocol.TryBind(FileTransferProtocol.Encode(packet with { Sender = "bob" }), "alice", out _)) return false;
        if (FileTransferProtocol.TryParse(FileTransferProtocol.Encode(packet with { Size = FileTransferProtocol.MaximumFileBytes + 1 }), out _)) return false;
        string rendered = SecureWire.Render(new[] { frame }, "alice").Single();
        return rendered == bound && !FileTransferProtocol.TryParse(frame + "junk", out _);
    }

    internal static async Task<bool> FileTransferConsentStreamingSelfTest() => await RunPair(false, false, false);
    internal static async Task<bool> FileTransferTamperCleanupSelfTest() => await RunPair(true, false, false);
    internal static async Task<bool> FileTransferReplayCleanupSelfTest() => await RunPair(false, true, false);
    internal static async Task<bool> FileTransferChangedSourceSelfTest() => await RunPair(false, false, true);
    internal static async Task<bool> FileTransferUnexpectedStateSelfTest() => await RunPair(false, false, false, true);

    private static async Task<bool> RunPair(bool tamper, bool replay, bool changeSource, bool unexpectedState = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "TCPTunnel-file-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "отчёт.exe"), desktop = Path.Combine(root, "Desktop");
        Directory.CreateDirectory(desktop);
        byte[] expected = RandomNumberGenerator.GetBytes(3 * FileTransferProtocol.ChunkBytes + 87);
        await File.WriteAllBytesAsync(source, expected);
        await File.WriteAllTextAsync(Path.Combine(desktop, "отчёт.exe"), "preserve existing file");
        var queue = new ConcurrentQueue<(bool ToBob, string Frame)>();
        var aliceNotices = new ConcurrentQueue<string>(); var bobNotices = new ConcurrentQueue<string>();
        int encryptedChunks = 0; bool modified = false;
        Task Route(bool toBob, string frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!FileTransferProtocol.TryBind(frame, toBob ? "alice" : "bob", out string bound)) throw new Exception("Invalid outgoing frame");
            if (FileTransferProtocol.TryParse(bound, out var p) && p.Kind == "chunk")
            {
                encryptedChunks++;
                byte[] ciphertext = Convert.FromBase64String(p.Data);
                if (ciphertext.AsSpan().IndexOf(expected.AsSpan(0, 32)) >= 0) throw new Exception("Plaintext payload leaked");
                if (tamper && !modified) { ciphertext[0] ^= 1; bound = FileTransferProtocol.Encode(p with { Data = Convert.ToBase64String(ciphertext) }); modified = true; }
                queue.Enqueue((toBob, bound));
                if (replay && !modified) { queue.Enqueue((toBob, bound)); modified = true; }
            }
            else
            {
                if (unexpectedState && FileTransferProtocol.TryParse(bound, out var accept) && accept.Kind == "accept")
                    queue.Enqueue((true, FileTransferProtocol.Encode(accept with { Target = "bob", Sender = "alice" })));
                queue.Enqueue((toBob, bound));
            }
            return Task.CompletedTask;
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var alice = new FileTransferSession(() => "alice", (frame, token) => Route(true, frame, token), aliceNotices.Enqueue, stop.Token, desktop);
            using var bob = new FileTransferSession(() => "bob", (frame, token) => Route(false, frame, token), bobNotices.Enqueue, stop.Token, desktop);
            async Task Pump(Func<bool> done)
            {
                while (!done())
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (queue.TryDequeue(out var message)) await (message.ToBob ? bob : alice).HandleAsync(message.Frame);
                    else await Task.Delay(2, stop.Token);
                }
            }
            await alice.SetAvailableAsync(true); await bob.SetAvailableAsync(true);
            await Pump(() => queue.IsEmpty);
            if (!alice.HandleCommand("/send @bob '" + source + "'")) return false;
            await Pump(() => bobNotices.Any(s => s.StartsWith("1. ", StringComparison.Ordinal)));
            if (Directory.GetFiles(desktop).Length != 1) throw new Exception("Files created without consent");
            bob.HandleCommand("/receive 1,invalid");
            if (Directory.GetFiles(desktop).Length != 1) throw new Exception("Invalid list partially accepted a file");
            if (changeSource) await File.WriteAllTextAsync(source, "changed after offer");
            bob.HandleCommand("/receive 1");
            await Pump(() => changeSource ? aliceNotices.Any(s => s.Contains("changed", StringComparison.OrdinalIgnoreCase)) : tamper || replay || unexpectedState ? bobNotices.Any(s => s.Contains("прервана") || s.Contains("stopped")) : Directory.Exists(desktop) && File.Exists(Path.Combine(desktop, "отчёт (1).exe")));
            if (tamper || replay || changeSource || unexpectedState)
            {
                alice.Dispose(); bob.Dispose();
                await Task.Delay(40);
                return Directory.GetFiles(desktop).Length == 1 && await File.ReadAllTextAsync(Path.Combine(desktop, "отчёт.exe")) == "preserve existing file";
            }
            byte[] actual = await File.ReadAllBytesAsync(Path.Combine(desktop, "отчёт (1).exe"));
            if (OperatingSystem.IsWindows() && !File.ReadAllText(Path.Combine(desktop, "отчёт (1).exe") + ":Zone.Identifier").Contains("ZoneId=3")) return false;
            return encryptedChunks == 4 && actual.SequenceEqual(expected) && await File.ReadAllTextAsync(Path.Combine(desktop, "отчёт.exe")) == "preserve existing file";
        }
        finally { if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
}
