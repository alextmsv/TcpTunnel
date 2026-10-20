using System.Buffers.Binary;
using TCPTunnel.Cryptography;

namespace TCPTunnel;

internal static class SecureWireSizeTests
{
    public static async Task<bool> SecurePacketKindLimitsSelfTest()
    {
        foreach (var input in new (byte Kind, int Size)[] { (1, MessageProtocol.MaxFrameBytes + 1), (2, 256 * 1024 + 1), (3, 256 * 1024 + 1), (4, 256 * 1024 + 1), (7, 256 * 1024 + 1), (10, 31), (11, 33), (99, 0) })
        {
            byte[] header = new byte[5]; BinaryPrimitives.WriteInt32BigEndian(header, input.Size + 1); header[4] = input.Kind;
            using var stream = new MemoryStream(header);
            try { await SecureWire.ReadAsync(stream, CancellationToken.None); return false; }
            catch (InvalidDataException) { }
        }
        foreach (byte kind in new byte[] { 1, 2, 3, 4, 5, 6, 7, 10, 11 })
        {
            byte[] payload = kind is 10 or 11 ? new byte[32] : new byte[] { 1, 2, 3 };
            using var stream = new MemoryStream(SecureWire.Encode(kind, payload));
            var packet = await SecureWire.ReadAsync(stream, CancellationToken.None);
            if (packet.Kind != kind || !packet.Payload.SequenceEqual(payload)) return false;
        }
        return true;
    }
}
