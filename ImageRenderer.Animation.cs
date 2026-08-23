using System;

namespace TCPTunnel
{
    internal sealed class FrozenAnimation
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[][] PackedFrames { get; set; }
        public byte[][] ToneMaps { get; set; }
        public ushort[] FrameDelays { get; set; }
        public int[] FrameEndMilliseconds { get; set; }
        public int DurationMilliseconds { get; set; }
        public string Sender { get; set; }
        public bool IsOutgoing { get; set; }
        public bool IsOversized { get; set; }
        public bool IsStronglyCompressed { get; set; }

        public bool ShouldOfferLook => IsOversized || IsStronglyCompressed;

        public int PackedByteCount
        {
            get
            {
                int total = 0;
                if (PackedFrames != null)
                {
                    for (int index = 0; index < PackedFrames.Length; index++)
                        total += PackedFrames[index] == null ? 0 : PackedFrames[index].Length;
                }
                return total;
            }
        }
    }

    internal static partial class ImageRenderer
    {
        public static FrozenAnimation Freeze(
            AnimatedImagePacket packet,
            int viewportWidth,
            int usableChatRows,
            string sender,
            bool outgoing)
        {
            if (!ImageAnimationProtocol.IsValidPacket(packet, false))
                throw new ArgumentException("Invalid animated image packet.", nameof(packet));

            int width;
            int height;
            CalculateDisplayDimensions(
                packet.Width,
                packet.Height,
                viewportWidth,
                usableChatRows,
                out width,
                out height);

            var frames = new byte[packet.FrameCount][];
            var toneMaps = new byte[packet.FrameCount][];
            var frameEnds = new int[packet.FrameCount];
            int duration = 0;
            for (int index = 0; index < packet.FrameCount; index++)
            {
                byte[] frame = Resample(packet.PackedFrames[index], packet.Width, packet.Height, width, height);
                frames[index] = frame;
                toneMaps[index] = BuildToneMap(frame, width, height);
                duration += packet.FrameDelays[index];
                frameEnds[index] = duration;
            }

            return new FrozenAnimation
            {
                Width = width,
                Height = height,
                PackedFrames = frames,
                ToneMaps = toneMaps,
                FrameDelays = (ushort[])packet.FrameDelays.Clone(),
                FrameEndMilliseconds = frameEnds,
                DurationMilliseconds = duration,
                Sender = sender,
                IsOutgoing = outgoing,
                IsOversized = packet.IsOversized,
                IsStronglyCompressed = IsStronglyCompressed(
                    packet.Width,
                    packet.Height,
                    width,
                    height)
            };
        }

        public static int GetFrameIndex(FrozenAnimation animation, long elapsedMilliseconds)
        {
            if (animation == null || animation.FrameEndMilliseconds == null ||
                animation.FrameEndMilliseconds.Length == 0 || animation.DurationMilliseconds <= 0)
                return 0;

            int time = (int)(Math.Max(0L, elapsedMilliseconds) % animation.DurationMilliseconds);
            int low = 0;
            int high = animation.FrameEndMilliseconds.Length - 1;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (time < animation.FrameEndMilliseconds[middle])
                    high = middle;
                else
                    low = middle + 1;
            }
            return low;
        }

        public static int GetMillisecondsUntilNextFrame(
            FrozenAnimation animation,
            long elapsedMilliseconds)
        {
            if (animation == null || animation.FrameEndMilliseconds == null ||
                animation.FrameEndMilliseconds.Length == 0 || animation.DurationMilliseconds <= 0)
                return 100;
            int time = (int)(Math.Max(0L, elapsedMilliseconds) % animation.DurationMilliseconds);
            int frame = GetFrameIndex(animation, elapsedMilliseconds);
            return Math.Max(1, animation.FrameEndMilliseconds[frame] - time);
        }

        public static void FillAsciiRow(
            FrozenAnimation animation,
            int frameIndex,
            int row,
            Span<char> destination)
        {
            if (animation == null || animation.PackedFrames == null ||
                frameIndex < 0 || frameIndex >= animation.PackedFrames.Length ||
                row < 0 || row >= animation.Height)
                throw new ArgumentOutOfRangeException();

            byte[] pixels = animation.PackedFrames[frameIndex];
            byte[] toneMap = animation.ToneMaps == null ? null : animation.ToneMaps[frameIndex];
            bool useToneMap = toneMap != null && toneMap.Length == 16;
            int count = Math.Min(animation.Width, destination.Length);
            int offset = row * animation.Width;
            for (int x = 0; x < count; x++)
            {
                int level = ImageProtocol.GetPixel(pixels, offset + x);
                destination[x] = DensityLut[useToneMap ? toneMap[level] : level];
            }
        }

        internal static bool RunAnimationSelfTest()
        {
            int packedLength = ImageProtocol.GetPackedLength(8, 4);
            var packet = new AnimatedImagePacket
            {
                Width = 8,
                Height = 4,
                PackedFrames = new[] { new byte[packedLength], new byte[packedLength] },
                FrameDelays = new ushort[] { 90, 110 }
            };
            for (int pixel = 0; pixel < 32; pixel++)
                ImageProtocol.SetPixel(packet.PackedFrames[1], pixel, 15);

            FrozenAnimation animation = Freeze(packet, 8, 10, "alex", false);
            Span<char> row = stackalloc char[8];
            FillAsciiRow(animation, 0, 0, row);
            bool dark = row[0] == '@';
            FillAsciiRow(animation, 1, 0, row);
            return dark && row[0] == ' ' && animation.DurationMilliseconds == 200 &&
                   GetFrameIndex(animation, 89) == 0 && GetFrameIndex(animation, 90) == 1 &&
                   GetFrameIndex(animation, 200) == 0 &&
                   GetMillisecondsUntilNextFrame(animation, 89) == 1 &&
                   animation.PackedByteCount ==
                       ImageProtocol.GetPackedLength(animation.Width, animation.Height) * 2;
        }

        private static void CalculateDisplayDimensions(
            int sourceWidth,
            int sourceHeight,
            int viewportWidth,
            int usableChatRows,
            out int width,
            out int height)
        {
            width = Math.Max(1, Math.Min(sourceWidth, viewportWidth));
            height = Math.Max(1,
                (int)Math.Round((double)sourceHeight * width /
                    (sourceWidth * ConsoleCellAspectNumerator)));
            int maxRows = Math.Max(1, usableChatRows * HeightPercent / 100);
            if (height > maxRows)
            {
                height = maxRows;
                width = Math.Max(1, Math.Min(width,
                    (int)Math.Round((double)sourceWidth * height * ConsoleCellAspectNumerator /
                        sourceHeight)));
            }
            width = Math.Min(width, sourceWidth);
            height = Math.Min(height, sourceHeight);
        }
    }
}
