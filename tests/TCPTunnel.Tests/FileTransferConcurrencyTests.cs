using System.Collections.Concurrent;
using System.Security.Cryptography;
using TCPTunnel;

internal static class FileTransferConcurrencyTests
{
    internal static async Task<bool> FileTransferConcurrentConsentSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "TCPTunnel-concurrent-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sessions = new Dictionary<string, FileTransferSession>();
        var notices = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
        var queue = new ConcurrentQueue<(string Target, string Frame)>();
        Task Route(string sender, string frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!FileTransferProtocol.TryBind(frame, sender, out string bound) || !FileTransferProtocol.TryParse(bound, out var packet)) throw new Exception("Invalid file frame");
            foreach (string target in sessions.Keys.Where(name => name != sender && (packet.Target == "" || packet.Target == name))) queue.Enqueue((target, bound));
            return Task.CompletedTask;
        }
        async Task Pump(Func<bool> done)
        {
            while (!done())
            {
                stop.Token.ThrowIfCancellationRequested();
                if (queue.TryDequeue(out var item)) await sessions[item.Target].HandleAsync(item.Frame);
                else await Task.Delay(2, stop.Token);
            }
        }
        try
        {
            foreach (string name in new[] { "alice", "bob", "carol", "dave" })
            {
                Directory.CreateDirectory(Path.Combine(root, name)); notices[name] = new();
                sessions.Add(name, new FileTransferSession(() => name, (frame, token) => Route(name, frame, token), notices[name].Enqueue, stop.Token, Path.Combine(root, name)));
            }
            foreach (var session in sessions.Values) await session.SetAvailableAsync(true);
            await Pump(() => queue.IsEmpty);
            byte[] data = RandomNumberGenerator.GetBytes(128 * 1024);
            string source = Path.Combine(root, "source.bin"); await File.WriteAllBytesAsync(source, data);
            foreach (string target in new[] { "bob", "carol", "dave" })
                for (int i = 0; i < 2; i++) sessions["alice"].HandleCommand($"/send {target} '{source}'");
            await Pump(() => new[] { "bob", "carol", "dave" }.All(name => notices[name].Any(text => text.StartsWith("2. "))));
            foreach (string name in new[] { "bob", "carol", "dave" })
            {
                sessions[name].HandleCommand("/receive 1,bad");
                if (Directory.GetFiles(Path.Combine(root, name)).Length != 0) return false;
                sessions[name].HandleCommand("/receive 1,2");
            }
            await Pump(() => new[] { "bob", "carol", "dave" }.All(name => File.Exists(Path.Combine(root, name, "source (1).bin"))));
            foreach (string name in new[] { "bob", "carol", "dave" })
                foreach (string file in Directory.GetFiles(Path.Combine(root, name)))
                    if (!(await File.ReadAllBytesAsync(file)).SequenceEqual(data)) return false;
            return notices.Values.SelectMany(values => values).All(text => !text.Contains("прервана") && !text.Contains("stopped"));
        }
        finally
        {
            stop.Cancel(); foreach (var session in sessions.Values) session.Dispose();
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
}
