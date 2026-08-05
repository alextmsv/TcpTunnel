using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public static class MessageProtocol
    {
        public const int MaxFrameBytes = 16 * 1024;
        public const int MaxMessageCharacters = 2000;

        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static async Task<string> ReadStringAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            int byteCount = await Read7BitEncodedIntAsync(stream, cancellationToken).ConfigureAwait(false);
            if (byteCount < 0 || byteCount > MaxFrameBytes)
                throw new InvalidDataException(Lang.Get(TextId.FrameTooLarge, byteCount, MaxFrameBytes));
            if (byteCount == 0)
                return String.Empty;

            byte[] payload = new byte[byteCount];
            int offset = 0;
            while (offset < payload.Length)
            {
                int read = await stream.ReadAsync(payload, offset, payload.Length - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException();
                offset += read;
            }

            return Utf8.GetString(payload);
        }

        public static async Task WriteStringAsync(NetworkStream stream, string value, CancellationToken cancellationToken)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            int byteCount = Utf8.GetByteCount(value);
            if (byteCount > MaxFrameBytes)
                throw new InvalidDataException(Lang.Get(TextId.FrameTooLarge, byteCount, MaxFrameBytes));

            int prefixLength = Get7BitEncodedIntLength(byteCount);
            byte[] frame = new byte[prefixLength + byteCount];
            Write7BitEncodedInt(frame, byteCount);
            Utf8.GetBytes(value, 0, value.Length, frame, prefixLength);

            await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<int> Read7BitEncodedIntAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] singleByte = new byte[1];
            int value = 0;

            for (int shift = 0; shift < 35; shift += 7)
            {
                int read = await stream.ReadAsync(singleByte, 0, 1, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException();

                byte current = singleByte[0];
                if (shift == 28 && (current & 0xF0) != 0)
                    throw new InvalidDataException(Lang.Get(TextId.InvalidFrameLength));

                value |= (current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    return value;
            }

            throw new InvalidDataException(Lang.Get(TextId.InvalidFramePrefix));
        }

        private static int Get7BitEncodedIntLength(int value)
        {
            int length = 1;
            while ((value >>= 7) != 0)
                length++;
            return length;
        }

        private static void Write7BitEncodedInt(byte[] destination, int value)
        {
            int index = 0;
            uint remaining = (uint)value;
            while (remaining >= 0x80)
            {
                destination[index++] = (byte)(remaining | 0x80);
                remaining >>= 7;
            }
            destination[index] = (byte)remaining;
        }
    }
}
