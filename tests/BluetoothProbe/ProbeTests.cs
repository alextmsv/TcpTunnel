using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

internal static class ProbeTests
{
    internal static async Task<int> RunAsync()
    {
        int failures = 0;
        async Task Check(string name, Func<Task<bool>> test)
        {
            try
            {
                bool passed = await test();
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
                if (!passed) failures++;
            }
            catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); failures++; }
        }
        await Check("Beacon validation", () =>
        {
            byte[] bytes = ProbeBeacon.Encode(0x123456789abc, 123);
            bool valid = ProbeBeacon.TryDecode(bytes, out var beacon) && beacon.Address == 0x123456789abc && beacon.Instance == 123;
            bytes[4] = 255;
            return Task.FromResult(valid && !ProbeBeacon.TryDecode(bytes, out _) &&
                !ProbeBeacon.TryDecode(new byte[1024], out _) && !ProbeBeacon.TryDecode(new byte[19], out _));
        });
        await Check("Fragmented frame and EOF", async () =>
        {
            using var memory = new FragmentedStream();
            byte[] payload = Enumerable.Range(0, 16384).Select(i => (byte)i).ToArray();
            await ProbeWire.WriteAsync(memory, payload, default);
            memory.Position = 0;
            if (!payload.SequenceEqual(await ProbeWire.ReadAsync(memory, default))) return false;
            memory.SetLength(memory.Length - 1); memory.Position = 0;
            try { await ProbeWire.ReadAsync(memory, default); return false; }
            catch (EndOfStreamException) { return true; }
        });
        await Check("Oversize rejected before payload allocation", async () =>
        {
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, 16385);
            using var memory = new MemoryStream(header);
            try { await ProbeWire.ReadAsync(memory, default); return false; }
            catch (InvalidDataException) { return memory.Position == 4; }
        });
        await Check("Bounded bidirectional transfer over loopback (not Bluetooth)", async () =>
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var peer = new TcpClient();
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
            using var accepted = await listener.AcceptTcpClientAsync(stop.Token);
            Task server = ProbeWire.ServeAsync(accepted.GetStream(), accepted.GetStream(), stop.Token);
            await ProbeWire.ExchangeAsync(peer.GetStream(), peer.GetStream(), stop.Token);
            await server;
            return true;
        });
        await Check("Cancelled read", async () =>
        {
            using var memory = new MemoryStream();
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await ProbeWire.ReadAsync(memory, stop.Token); return false; }
            catch (OperationCanceledException) { return true; }
        });
        return failures == 0 ? 0 : 1;
    }

    private sealed class FragmentedStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }
}
