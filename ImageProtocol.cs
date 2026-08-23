using System;
using System.Text;

namespace TCPTunnel
{
    internal sealed class ImagePacket
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] PackedPixels { get; set; }
        public bool IsOversized { get; set; }
        public string Sender { get; set; }
    }

    internal static class ImageProtocol
    {
        public const int MaxWireWidth = 160;
        public const int MaxWireHeight = 72;
        public const int MaxWirePixels = MaxWireWidth * MaxWireHeight;
        public const int MaxPackedBytes = (MaxWirePixels + 1) / 2;
        public const int MaxImageFrameCharacters = 8192;

        private const string ClientPrefix = "\u001eTCPTUNNEL|IMAGE|1|C|";
        private const string ServerPrefix = "\u001eTCPTUNNEL|IMAGE|1|S|";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool IsImageControlMessage(string value)
        {
            return value != null && value.StartsWith("\u001eTCPTUNNEL|IMAGE|", StringComparison.Ordinal);
        }

        public static string CreateClientFrame(ImagePacket packet)
        {
            ValidatePacket(packet, false);
            string frame = ClientPrefix +
                           (packet.IsOversized ? "1|" : "0|") +
                           packet.Width + "|" + packet.Height + "|" +
                           Convert.ToBase64String(packet.PackedPixels);
            ValidateFrameLength(frame);
            return frame;
        }

        public static string CreateServerFrame(ImagePacket packet, string authenticatedSender)
        {
            ValidatePacket(packet, false);
            if (!NetWorker.IsNicknameValid(authenticatedSender))
                throw new ArgumentException("Invalid authenticated sender.", nameof(authenticatedSender));

            string sender = Convert.ToBase64String(StrictUtf8.GetBytes(authenticatedSender));
            string frame = ServerPrefix +
                           (packet.IsOversized ? "1|" : "0|") +
                           packet.Width + "|" + packet.Height + "|" + sender + "|" +
                           Convert.ToBase64String(packet.PackedPixels);
            ValidateFrameLength(frame);
            return frame;
        }

        public static bool TryParseClientFrame(string frame, out ImagePacket packet)
        {
            return TryParse(frame, ClientPrefix, false, out packet);
        }

        public static bool TryParseServerFrame(string frame, out ImagePacket packet)
        {
            return TryParse(frame, ServerPrefix, true, out packet);
        }

        public static int GetPackedLength(int width, int height)
        {
            if (width < 1 || width > MaxWireWidth || height < 1 || height > MaxWireHeight)
                return -1;

            int pixels;
            try { pixels = checked(width * height); }
            catch (OverflowException) { return -1; }
            if (pixels > MaxWirePixels)
                return -1;
            return (pixels + 1) / 2;
        }

        public static byte GetPixel(byte[] packedPixels, int pixelIndex)
        {
            byte pair = packedPixels[pixelIndex >> 1];
            return (byte)((pixelIndex & 1) == 0 ? pair >> 4 : pair & 0x0F);
        }

        public static void SetPixel(byte[] packedPixels, int pixelIndex, byte value)
        {
            int byteIndex = pixelIndex >> 1;
            value &= 0x0F;
            if ((pixelIndex & 1) == 0)
                packedPixels[byteIndex] = (byte)((packedPixels[byteIndex] & 0x0F) | (value << 4));
            else
                packedPixels[byteIndex] = (byte)((packedPixels[byteIndex] & 0xF0) | value);
        }

        internal static bool RunSelfTest()
        {
            var smallest = new ImagePacket
            {
                Width = 1,
                Height = 1,
                PackedPixels = new byte[] { 0xA0 }
            };
            ImagePacket parsed;
            if (!TryParseClientFrame(CreateClientFrame(smallest), out parsed) ||
                parsed.Width != 1 || parsed.Height != 1 || GetPixel(parsed.PackedPixels, 0) != 10)
                return false;

            byte[] largestPixels = new byte[MaxPackedBytes];
            for (int index = 0; index < largestPixels.Length; index++)
                largestPixels[index] = (byte)index;
            var largest = new ImagePacket
            {
                Width = MaxWireWidth,
                Height = MaxWireHeight,
                PackedPixels = largestPixels,
                IsOversized = true
            };
            string largestFrame = CreateServerFrame(largest, "alextmsv");
            if (largestFrame.Length > MaxImageFrameCharacters ||
                Encoding.UTF8.GetByteCount(largestFrame) >= MessageProtocol.MaxFrameBytes ||
                !TryParseServerFrame(largestFrame, out parsed) ||
                parsed.Sender != "alextmsv" || !parsed.IsOversized ||
                parsed.PackedPixels.Length != MaxPackedBytes)
                return false;

            byte[] odd = new byte[3];
            for (int index = 0; index < 5; index++)
                SetPixel(odd, index, (byte)(index + 1));
            for (int index = 0; index < 5; index++)
            {
                if (GetPixel(odd, index) != index + 1)
                    return false;
            }

            string good = CreateClientFrame(new ImagePacket
            {
                Width = 3,
                Height = 1,
                PackedPixels = new byte[] { 0x12, 0x30 }
            });
            return !TryParseClientFrame(good.Replace("|1|C|", "|2|C|"), out parsed) &&
                   !TryParseClientFrame(ClientPrefix + "0|0|1|AA==", out parsed) &&
                   !TryParseClientFrame(ClientPrefix + "0|-1|1|AA==", out parsed) &&
                   !TryParseClientFrame(ClientPrefix + "0|999999999999|1|AA==", out parsed) &&
                   !TryParseClientFrame(ClientPrefix + "0|160|72|AA==", out parsed) &&
                   !TryParseClientFrame(ClientPrefix + "0|3|1|EjAA", out parsed) &&
                   !TryParseClientFrame(ClientPrefix + "0|3|1|%%%%", out parsed) &&
                   GetPackedLength(Int32.MaxValue, Int32.MaxValue) == -1;
        }

        private static bool TryParse(string frame, string prefix, bool hasSender, out ImagePacket packet)
        {
            packet = null;
            if (String.IsNullOrEmpty(frame) || frame.Length > MaxImageFrameCharacters ||
                !frame.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            int position = prefix.Length;
            ReadOnlySpan<char> value = frame.AsSpan();
            ReadOnlySpan<char> oversizedText;
            ReadOnlySpan<char> widthText;
            ReadOnlySpan<char> heightText;
            ReadOnlySpan<char> senderText = default(ReadOnlySpan<char>);
            if (!TryReadField(frame, ref position, out oversizedText) ||
                !TryReadField(frame, ref position, out widthText) ||
                !TryReadField(frame, ref position, out heightText) ||
                (hasSender && !TryReadField(frame, ref position, out senderText)))
                return false;
            ReadOnlySpan<char> payloadText = value.Slice(position);

            bool oversized;
            if (oversizedText.Length == 1 && oversizedText[0] == '0')
                oversized = false;
            else if (oversizedText.Length == 1 && oversizedText[0] == '1')
                oversized = true;
            else
                return false;

            int width;
            int height;
            if (!Int32.TryParse(widthText, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out width) ||
                !Int32.TryParse(heightText, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out height))
                return false;
            int expectedLength = GetPackedLength(width, height);
            if (expectedLength < 0 || payloadText.Length != ((expectedLength + 2) / 3) * 4)
                return false;

            byte[] pixels;
            if (!TryDecodeBase64(payloadText, expectedLength, out pixels) ||
                pixels.Length != expectedLength)
                return false;
            if (((width * height) & 1) != 0 && (pixels[pixels.Length - 1] & 0x0F) != 0)
                return false;

            string sender = null;
            if (hasSender)
            {
                byte[] senderBytes;
                if (!TryDecodeBase64(senderText, MessageProtocol.MaxFrameBytes, out senderBytes))
                    return false;
                try { sender = StrictUtf8.GetString(senderBytes); }
                catch (DecoderFallbackException) { return false; }
                if (!NetWorker.IsNicknameValid(sender))
                    return false;
            }

            packet = new ImagePacket
            {
                Width = width,
                Height = height,
                PackedPixels = pixels,
                IsOversized = oversized,
                Sender = sender
            };
            return true;
        }

        internal static bool TryDecodeBase64(
            ReadOnlySpan<char> value,
            int maximumDecodedBytes,
            out byte[] decoded)
        {
            decoded = null;
            if (value.Length == 0 || (value.Length & 3) != 0)
                return false;

            int padding = value[value.Length - 1] == '=' ? 1 : 0;
            if (value.Length > 1 && value[value.Length - 2] == '=')
                padding++;
            int decodedLength = (value.Length / 4) * 3 - padding;
            if (decodedLength < 0 || decodedLength > maximumDecodedBytes)
                return false;

            byte[] buffer = new byte[decodedLength];
            int bytesWritten;
            if (!Convert.TryFromBase64Chars(value, buffer, out bytesWritten) ||
                bytesWritten != decodedLength)
                return false;

            decoded = buffer;
            return true;
        }

        private static bool TryReadField(
            string value,
            scoped ref int position,
            out ReadOnlySpan<char> field)
        {
            int separator = value.IndexOf('|', position);
            if (separator < position)
            {
                field = default(ReadOnlySpan<char>);
                return false;
            }
            field = value.AsSpan(position, separator - position);
            position = separator + 1;
            return field.Length > 0;
        }

        private static void ValidatePacket(ImagePacket packet, bool requireSender)
        {
            if (packet == null || packet.PackedPixels == null)
                throw new ArgumentNullException(nameof(packet));
            int expectedLength = GetPackedLength(packet.Width, packet.Height);
            if (expectedLength < 0 || packet.PackedPixels.Length != expectedLength)
                throw new ArgumentException("Invalid image dimensions or payload length.", nameof(packet));
            if (((packet.Width * packet.Height) & 1) != 0 &&
                (packet.PackedPixels[packet.PackedPixels.Length - 1] & 0x0F) != 0)
                throw new ArgumentException("Unused image nibble must be zero.", nameof(packet));
            if (requireSender && !NetWorker.IsNicknameValid(packet.Sender))
                throw new ArgumentException("Invalid image sender.", nameof(packet));
        }

        private static void ValidateFrameLength(string frame)
        {
            int bytes = Encoding.UTF8.GetByteCount(frame);
            if (frame.Length > MaxImageFrameCharacters || bytes > MessageProtocol.MaxFrameBytes)
                throw new InvalidOperationException("Image frame exceeds its protocol bound.");
        }
    }
}
