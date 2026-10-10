using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

internal readonly record struct ProbeBeacon(ulong Address, ulong Instance)
{
    internal static byte[] Encode(ulong address, ulong instance)
    {
        if (address == 0 || address > 0xffffffffffff || instance == 0) throw new ArgumentOutOfRangeException();
        var bytes = new byte[19];
        "TCPB"u8.CopyTo(bytes); bytes[4] = 1;
        for (int i = 0; i < 6; i++) bytes[5 + i] = (byte)(address >> (8 * i));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(11), instance);
        return bytes;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, out ProbeBeacon beacon)
    {
        beacon = default;
        if (bytes.Length != 19 || !bytes[..4].SequenceEqual("TCPB"u8) || bytes[4] != 1) return false;
        ulong address = 0;
        for (int i = 0; i < 6; i++) address |= (ulong)bytes[5 + i] << (8 * i);
        ulong instance = BinaryPrimitives.ReadUInt64LittleEndian(bytes[11..]);
        if (address == 0 || instance == 0) return false;
        beacon = new(address, instance);
        return true;
    }
}

internal static class ProbeWire
{
    private const int FrameLimit = 16384;
    private const int FrameCount = 16;
    private static readonly byte[] Greeting = Encoding.ASCII.GetBytes("TCPTunnel BT probe v1");

    internal static async Task<byte[]> ReadAsync(Stream input, CancellationToken token)
    {
        byte[] header = new byte[4];
        await input.ReadExactlyAsync(header, token);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count < 1 || count > FrameLimit) throw new InvalidDataException("Invalid probe frame length");
        byte[] payload = new byte[count];
        await input.ReadExactlyAsync(payload, token);
        return payload;
    }

    internal static async Task WriteAsync(Stream output, byte[] payload, CancellationToken token)
    {
        if (payload.Length < 1 || payload.Length > FrameLimit) throw new InvalidDataException("Invalid probe frame length");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await output.WriteAsync(header, token);
        await output.WriteAsync(payload, token);
        await output.FlushAsync(token);
    }

    internal static async Task ServeAsync(Stream input, Stream output, CancellationToken token)
    {
        if (!Greeting.SequenceEqual(await ReadAsync(input, token))) throw new InvalidDataException("Unknown probe handshake");
        await WriteAsync(output, Greeting, token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < FrameCount; i++)
        {
            byte[] frame = await ReadAsync(input, token);
            if (frame.Length != FrameLimit || BinaryPrimitives.ReadInt32LittleEndian(frame) != i)
                throw new InvalidDataException("Probe frame size/order mismatch");
            hash.AppendData(frame);
            await WriteAsync(output, frame, token);
        }
        await WriteAsync(output, hash.GetHashAndReset(), token);
    }

    internal static async Task ExchangeAsync(Stream input, Stream output, CancellationToken token)
    {
        await WriteAsync(output, Greeting, token);
        if (!Greeting.SequenceEqual(await ReadAsync(input, token))) throw new InvalidDataException("Unknown probe handshake");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < FrameCount; i++)
        {
            byte[] frame = RandomNumberGenerator.GetBytes(FrameLimit);
            BinaryPrimitives.WriteInt32LittleEndian(frame, i);
            hash.AppendData(frame);
            await WriteAsync(output, frame, token);
            if (!frame.SequenceEqual(await ReadAsync(input, token))) throw new InvalidDataException("Probe echo mismatch");
        }
        if (!hash.GetHashAndReset().SequenceEqual(await ReadAsync(input, token))) throw new InvalidDataException("Probe digest mismatch");
    }
}
