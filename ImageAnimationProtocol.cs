using System;
using System.Globalization;
using System.Text;

namespace TCPTunnel
{
    internal sealed class AnimatedImagePacket
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[][] PackedFrames { get; set; }
        public ushort[] FrameDelays { get; set; }
        public bool IsOversized { get; set; }
        public string Sender { get; set; }

        public int FrameCount => PackedFrames == null ? 0 : PackedFrames.Length;

        public int PackedByteCount
        {
            get
            {
                if (PackedFrames == null)
                    return 0;
                int total = 0;
                for (int index = 0; index < PackedFrames.Length; index++)
                    total += PackedFrames[index] == null ? 0 : PackedFrames[index].Length;
                return total;
            }
        }
    }

    internal enum ImageAnimationControlKind
    {
        Begin,
        Frame,
        End
    }

    internal sealed class ImageAnimationControlFrame
    {
        public ImageAnimationControlKind Kind;
        public string TransferId;
        public int Width;
        public int Height;
        public int FrameCount;
        public int FrameIndex;
        public ushort DelayMilliseconds;
        public byte[] PackedPixels;
        public bool IsOversized;
        public string Sender;
    }

    internal enum ImageAnimationAssemblyResult
    {
        Accepted,
        Completed,
        Invalid
    }

    internal sealed class ImageAnimationAssembler
    {
        // A fully packed animation is still below 1 MiB, but slow WAN links can
        // legitimately need more than 15 seconds to deliver it.
        private const int TransferTimeoutMilliseconds = 180_000;
        private string transferId;
        private string sender;
        private int width;
        private int height;
        private bool oversized;
        private byte[][] frames;
        private ushort[] delays;
        private int nextFrame;
        private int durationMilliseconds;
        private long deadlineTimestamp;

        public bool IsActive => transferId != null;

        public ImageAnimationAssemblyResult Accept(
            ImageAnimationControlFrame control,
            out AnimatedImagePacket packet)
        {
            packet = null;
            if (control == null)
                return ImageAnimationAssemblyResult.Invalid;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (IsActive && now > deadlineTimestamp)
                Reset();

            if (control.Kind == ImageAnimationControlKind.Begin)
            {
                if (IsActive || !ImageAnimationProtocol.IsValidDimensions(
                        control.Width,
                        control.Height,
                        control.FrameCount))
                {
                    Reset();
                    return ImageAnimationAssemblyResult.Invalid;
                }

                transferId = control.TransferId;
                sender = control.Sender;
                width = control.Width;
                height = control.Height;
                oversized = control.IsOversized;
                frames = new byte[control.FrameCount][];
                delays = new ushort[control.FrameCount];
                nextFrame = 0;
                durationMilliseconds = 0;
                deadlineTimestamp = now +
                    System.Diagnostics.Stopwatch.Frequency * TransferTimeoutMilliseconds / 1000L;
                return ImageAnimationAssemblyResult.Accepted;
            }

            if (!IsActive || !String.Equals(transferId, control.TransferId, StringComparison.Ordinal))
            {
                Reset();
                return ImageAnimationAssemblyResult.Invalid;
            }

            deadlineTimestamp = now +
                System.Diagnostics.Stopwatch.Frequency * TransferTimeoutMilliseconds / 1000L;
            if (control.Kind == ImageAnimationControlKind.Frame)
            {
                int expectedLength = ImageProtocol.GetPackedLength(width, height);
                if (control.FrameIndex != nextFrame || nextFrame >= frames.Length ||
                    control.PackedPixels == null || control.PackedPixels.Length != expectedLength ||
                    (((width * height) & 1) != 0 &&
                     (control.PackedPixels[control.PackedPixels.Length - 1] & 0x0F) != 0) ||
                    control.DelayMilliseconds < ImageAnimationProtocol.MinFrameDelayMilliseconds ||
                    control.DelayMilliseconds > ImageAnimationProtocol.MaxFrameDelayMilliseconds ||
                    durationMilliseconds + control.DelayMilliseconds > ImageAnimationProtocol.MaxDurationMilliseconds)
                {
                    Reset();
                    return ImageAnimationAssemblyResult.Invalid;
                }

                frames[nextFrame] = control.PackedPixels;
                delays[nextFrame] = control.DelayMilliseconds;
                durationMilliseconds += control.DelayMilliseconds;
                nextFrame++;
                return ImageAnimationAssemblyResult.Accepted;
            }

            if (control.Kind != ImageAnimationControlKind.End || nextFrame != frames.Length)
            {
                Reset();
                return ImageAnimationAssemblyResult.Invalid;
            }

            packet = new AnimatedImagePacket
            {
                Width = width,
                Height = height,
                PackedFrames = frames,
                FrameDelays = delays,
                IsOversized = oversized,
                Sender = sender
            };
            Reset();
            return ImageAnimationAssemblyResult.Completed;
        }

        public void Reset()
        {
            transferId = null;
            sender = null;
            width = 0;
            height = 0;
            oversized = false;
            frames = null;
            delays = null;
            nextFrame = 0;
            durationMilliseconds = 0;
            deadlineTimestamp = 0;
        }
    }

    internal static class ImageAnimationProtocol
    {
        public const int MaxFrames = 500;
        public const int MaxDurationMilliseconds = 60_000;
        // Browsers commonly clamp extremely small GIF delays. Microsoft's WIC
        // sample uses 90 ms as a compatibility floor, which also bounds redraw CPU.
        public const ushort MinFrameDelayMilliseconds = 90;
        public const ushort MaxFrameDelayMilliseconds = 2_000;
        public const int MaxControlFrameCharacters = 8192;
        public const int TransferIdLength = 16;

        private const string ClientPrefix = "\u001eTCPTUNNEL|ANIMATION|1|C|";
        private const string ServerPrefix = "\u001eTCPTUNNEL|ANIMATION|1|S|";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static bool IsAnimationControlMessage(string value)
        {
            return value != null && value.StartsWith("\u001eTCPTUNNEL|ANIMATION|", StringComparison.Ordinal);
        }

        public static string CreateTransferId()
        {
            Span<char> characters = stackalloc char[32];
            int charactersWritten;
            if (!Guid.NewGuid().TryFormat(characters, out charactersWritten, "N") ||
                charactersWritten < TransferIdLength)
                throw new InvalidOperationException("Unable to create an animation transfer id.");
            return new string(characters.Slice(0, TransferIdLength));
        }

        public static string CreateClientBegin(string transferId, AnimatedImagePacket packet)
        {
            ValidatePacket(packet, false);
            ValidateTransferIdOrThrow(transferId);
            return ValidateCreatedFrame(ClientPrefix + "B|" + transferId + "|" +
                (packet.IsOversized ? "1|" : "0|") + packet.Width + "|" + packet.Height + "|" +
                packet.FrameCount);
        }

        public static string CreateClientFrame(
            string transferId,
            int frameIndex,
            ushort delayMilliseconds,
            byte[] packedPixels)
        {
            ValidateTransferIdOrThrow(transferId);
            return CreateFrame(ClientPrefix, transferId, frameIndex, delayMilliseconds, packedPixels);
        }

        public static string CreateClientEnd(string transferId)
        {
            ValidateTransferIdOrThrow(transferId);
            return ValidateCreatedFrame(ClientPrefix + "E|" + transferId);
        }

        public static string[] CreateServerTransfer(AnimatedImagePacket packet, string authenticatedSender)
        {
            ValidatePacket(packet, false);
            if (!NetWorker.IsNicknameValid(authenticatedSender))
                throw new ArgumentException("Invalid authenticated sender.", nameof(authenticatedSender));

            string transferId = CreateTransferId();
            string encodedSender = Convert.ToBase64String(StrictUtf8.GetBytes(authenticatedSender));
            string[] transfer = new string[packet.FrameCount + 2];
            transfer[0] = ValidateCreatedFrame(ServerPrefix + "B|" + transferId + "|" +
                (packet.IsOversized ? "1|" : "0|") + packet.Width + "|" + packet.Height + "|" +
                packet.FrameCount + "|" + encodedSender);
            for (int index = 0; index < packet.FrameCount; index++)
            {
                transfer[index + 1] = CreateFrame(
                    ServerPrefix,
                    transferId,
                    index,
                    packet.FrameDelays[index],
                    packet.PackedFrames[index]);
            }
            transfer[transfer.Length - 1] = ValidateCreatedFrame(ServerPrefix + "E|" + transferId);
            return transfer;
        }

        public static bool TryParseClient(string value, out ImageAnimationControlFrame control)
        {
            return TryParse(value, ClientPrefix, false, out control);
        }

        public static bool TryParseServer(string value, out ImageAnimationControlFrame control)
        {
            return TryParse(value, ServerPrefix, true, out control);
        }

        public static bool IsValidPacket(AnimatedImagePacket packet, bool requireSender)
        {
            if (packet == null || !IsValidDimensions(packet.Width, packet.Height, packet.FrameCount) ||
                packet.FrameDelays == null || packet.FrameDelays.Length != packet.FrameCount)
                return false;
            if (requireSender && !NetWorker.IsNicknameValid(packet.Sender))
                return false;

            int expectedLength = ImageProtocol.GetPackedLength(packet.Width, packet.Height);
            int duration = 0;
            for (int index = 0; index < packet.FrameCount; index++)
            {
                byte[] frame = packet.PackedFrames[index];
                ushort delay = packet.FrameDelays[index];
                if (frame == null || frame.Length != expectedLength ||
                    delay < MinFrameDelayMilliseconds || delay > MaxFrameDelayMilliseconds)
                    return false;
                if (((packet.Width * packet.Height) & 1) != 0 && (frame[frame.Length - 1] & 0x0F) != 0)
                    return false;
                duration += delay;
                if (duration > MaxDurationMilliseconds)
                    return false;
            }
            return true;
        }

        internal static bool IsValidDimensions(int width, int height, int frameCount)
        {
            return ImageProtocol.GetPackedLength(width, height) > 0 &&
                   frameCount >= 1 && frameCount <= MaxFrames;
        }

        internal static bool RunSelfTest()
        {
            if (!IsValidDimensions(ImageProtocol.MaxWireWidth, ImageProtocol.MaxWireHeight, MaxFrames) ||
                IsValidDimensions(ImageProtocol.MaxWireWidth, ImageProtocol.MaxWireHeight, MaxFrames + 1) ||
                MaxDurationMilliseconds != 60_000)
                return false;

            int length = ImageProtocol.GetPackedLength(3, 2);
            var packet = new AnimatedImagePacket
            {
                Width = 3,
                Height = 2,
                PackedFrames = new[] { new byte[length], new byte[length] },
                FrameDelays = new ushort[] { 100, 160 },
                IsOversized = true
            };
            string id = "0123456789abcdef";
            var assembler = new ImageAnimationAssembler();
            ImageAnimationControlFrame control;
            AnimatedImagePacket completed;
            if (!TryParseClient(CreateClientBegin(id, packet), out control) ||
                assembler.Accept(control, out completed) != ImageAnimationAssemblyResult.Accepted)
                return false;
            for (int index = 0; index < packet.FrameCount; index++)
            {
                if (!TryParseClient(CreateClientFrame(id, index, packet.FrameDelays[index], packet.PackedFrames[index]), out control) ||
                    assembler.Accept(control, out completed) != ImageAnimationAssemblyResult.Accepted)
                    return false;
            }
            if (!TryParseClient(CreateClientEnd(id), out control) ||
                assembler.Accept(control, out completed) != ImageAnimationAssemblyResult.Completed ||
                completed.FrameCount != 2 || !completed.IsOversized)
                return false;

            string[] serverTransfer = CreateServerTransfer(completed, "alextmsv");
            assembler = new ImageAnimationAssembler();
            for (int index = 0; index < serverTransfer.Length; index++)
            {
                if (!TryParseServer(serverTransfer[index], out control))
                    return false;
                ImageAnimationAssemblyResult result = assembler.Accept(control, out completed);
                if (index + 1 == serverTransfer.Length)
                {
                    if (result != ImageAnimationAssemblyResult.Completed || completed.Sender != "alextmsv")
                        return false;
                }
                else if (result != ImageAnimationAssemblyResult.Accepted)
                    return false;
            }

            assembler = new ImageAnimationAssembler();
            TryParseClient(CreateClientBegin(id, packet), out control);
            assembler.Accept(control, out completed);
            TryParseClient(CreateClientFrame(id, 1, 100, packet.PackedFrames[0]), out control);
            return assembler.Accept(control, out completed) == ImageAnimationAssemblyResult.Invalid &&
                   !TryParseClient(ClientPrefix + "B|bad|0|3|2|2", out control) &&
                   !TryParseClient(ClientPrefix + "F|" + id + "|0|1|AAAA", out control);
        }

        private static string CreateFrame(
            string prefix,
            string transferId,
            int frameIndex,
            ushort delayMilliseconds,
            byte[] packedPixels)
        {
            if (frameIndex < 0 || frameIndex >= MaxFrames || packedPixels == null ||
                packedPixels.Length > ImageProtocol.MaxPackedBytes ||
                delayMilliseconds < MinFrameDelayMilliseconds || delayMilliseconds > MaxFrameDelayMilliseconds)
                throw new ArgumentException("Invalid animation frame.");
            string payload = Convert.ToBase64String(packedPixels);
            return ValidateCreatedFrame(prefix + "F|" + transferId + "|" + frameIndex + "|" +
                delayMilliseconds + "|" + payload);
        }

        private static bool TryParse(
            string value,
            string prefix,
            bool serverFrame,
            out ImageAnimationControlFrame control)
        {
            control = null;
            if (String.IsNullOrEmpty(value) || value.Length > MaxControlFrameCharacters ||
                !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length <= prefix.Length + 2)
                return false;

            int position = prefix.Length;
            char kind = value[position++];
            if (position >= value.Length || value[position++] != '|')
                return false;
            ReadOnlySpan<char> transferId;
            if (kind == 'E')
            {
                transferId = value.AsSpan(position);
                position = value.Length;
                if (!IsValidTransferId(transferId))
                    return false;
                control = new ImageAnimationControlFrame
                    {
                        Kind = ImageAnimationControlKind.End,
                        TransferId = transferId.ToString()
                };
                return true;
            }

            if (!TryReadField(value, ref position, out transferId) || !IsValidTransferId(transferId))
                return false;

            if (kind == 'B')
            {
                ReadOnlySpan<char> oversizedText;
                ReadOnlySpan<char> widthText;
                ReadOnlySpan<char> heightText;
                ReadOnlySpan<char> countText;
                if (!TryReadField(value, ref position, out oversizedText) ||
                    !TryReadField(value, ref position, out widthText) ||
                    !TryReadField(value, ref position, out heightText) ||
                    !TryReadLastOrField(value, ref position, serverFrame, out countText))
                    return false;
                bool oversized = oversizedText.Length == 1 && oversizedText[0] == '1';
                if (!oversized && !(oversizedText.Length == 1 && oversizedText[0] == '0'))
                    return false;
                int width;
                int height;
                int count;
                if (!TryParseInt(widthText, out width) || !TryParseInt(heightText, out height) ||
                    !TryParseInt(countText, out count) || !IsValidDimensions(width, height, count))
                    return false;

                string sender = null;
                if (serverFrame)
                {
                    ReadOnlySpan<char> senderText = value.AsSpan(position);
                    if (senderText.Length == 0)
                        return false;
                    byte[] senderBytes;
                    if (!ImageProtocol.TryDecodeBase64(
                            senderText,
                            MessageProtocol.MaxFrameBytes,
                            out senderBytes))
                        return false;
                    try { sender = StrictUtf8.GetString(senderBytes); }
                    catch (DecoderFallbackException) { return false; }
                    if (!NetWorker.IsNicknameValid(sender))
                        return false;
                }
                else if (position != value.Length)
                    return false;

                control = new ImageAnimationControlFrame
                    {
                        Kind = ImageAnimationControlKind.Begin,
                        TransferId = transferId.ToString(),
                    Width = width,
                    Height = height,
                    FrameCount = count,
                    IsOversized = oversized,
                    Sender = sender
                };
                return true;
            }

            if (kind != 'F')
                return false;
            ReadOnlySpan<char> indexText;
            ReadOnlySpan<char> delayText;
            if (!TryReadField(value, ref position, out indexText) ||
                !TryReadField(value, ref position, out delayText))
                return false;
            int frameIndex;
            int delay;
            if (!TryParseInt(indexText, out frameIndex) || frameIndex < 0 || frameIndex >= MaxFrames ||
                !TryParseInt(delayText, out delay) || delay < MinFrameDelayMilliseconds ||
                delay > MaxFrameDelayMilliseconds)
                return false;
            ReadOnlySpan<char> payload = value.AsSpan(position);
            if (payload.Length == 0 || payload.Length > ((ImageProtocol.MaxPackedBytes + 2) / 3) * 4)
                return false;
            byte[] pixels;
            if (!ImageProtocol.TryDecodeBase64(payload, ImageProtocol.MaxPackedBytes, out pixels))
                return false;
            control = new ImageAnimationControlFrame
            {
                Kind = ImageAnimationControlKind.Frame,
                TransferId = transferId.ToString(),
                FrameIndex = frameIndex,
                DelayMilliseconds = (ushort)delay,
                PackedPixels = pixels
            };
            return true;
        }

        private static bool TryReadLastOrField(
            string value,
            scoped ref int position,
            bool hasFollowingField,
            out ReadOnlySpan<char> field)
        {
            if (hasFollowingField)
                return TryReadField(value, ref position, out field);
            if (position > value.Length)
            {
                field = default(ReadOnlySpan<char>);
                return false;
            }
            field = value.AsSpan(position);
            position = value.Length;
            return field.Length > 0;
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

        private static bool TryParseInt(ReadOnlySpan<char> value, out int parsed)
        {
            return Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool IsValidTransferId(ReadOnlySpan<char> value)
        {
            if (value.Length != TransferIdLength)
                return false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                    return false;
            }
            return true;
        }

        private static void ValidateTransferIdOrThrow(string transferId)
        {
            if (!IsValidTransferId(transferId))
                throw new ArgumentException("Invalid animation transfer id.", nameof(transferId));
        }

        private static void ValidatePacket(AnimatedImagePacket packet, bool requireSender)
        {
            if (!IsValidPacket(packet, requireSender))
                throw new ArgumentException("Invalid animation packet.", nameof(packet));
        }

        private static string ValidateCreatedFrame(string frame)
        {
            if (frame.Length > MaxControlFrameCharacters ||
                Encoding.UTF8.GetByteCount(frame) > MessageProtocol.MaxFrameBytes)
                throw new InvalidOperationException("Animation frame exceeds its protocol bound.");
            return frame;
        }
    }
}
