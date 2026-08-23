using System;

namespace TCPTunnel
{
    internal sealed class FrozenImage
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] PackedPixels { get; set; }
        public string Sender { get; set; }
        public bool IsOutgoing { get; set; }
        public bool IsOversized { get; set; }
        public bool IsStronglyCompressed { get; set; }
        public byte[] ToneMap { get; set; }

        public bool ShouldOfferLook => IsOversized || IsStronglyCompressed;
    }

    internal static class ImageRenderer
    {
        public const int MaxHistoryImageBytes = 256 * 1024;
        private const int HeightPercent = 55;
        private const int ConsoleCellAspectNumerator = 2;
        private const int StrongCompressionPercent = 50;

        // One glyph for every 4-bit luminance level, from black to white.
        // Keep level 15 as the only blank so pale subjects remain visible on white.
        private const string DensityLut = "@%#8&MW*o=-:;,. ";

        public static FrozenImage Freeze(
            ImagePacket packet,
            int viewportWidth,
            int usableChatRows,
            string sender,
            bool outgoing)
        {
            if (packet == null)
                throw new ArgumentNullException(nameof(packet));
            if (ImageProtocol.GetPackedLength(packet.Width, packet.Height) != packet.PackedPixels.Length)
                throw new ArgumentException("Invalid image packet.", nameof(packet));

            int width = Math.Max(1, Math.Min(packet.Width, viewportWidth));
            int height = Math.Max(1,
                (int)Math.Round((double)packet.Height * width /
                    (packet.Width * ConsoleCellAspectNumerator)));
            int maxRows = Math.Max(1, usableChatRows * HeightPercent / 100);
            if (height > maxRows)
            {
                height = maxRows;
                width = Math.Max(1, Math.Min(width,
                    (int)Math.Round((double)packet.Width * height * ConsoleCellAspectNumerator /
                        packet.Height)));
            }

            width = Math.Min(width, packet.Width);
            height = Math.Min(height, packet.Height);
            byte[] frozen = Resample(packet.PackedPixels, packet.Width, packet.Height, width, height);
            return new FrozenImage
            {
                Width = width,
                Height = height,
                PackedPixels = frozen,
                Sender = sender,
                IsOutgoing = outgoing,
                IsOversized = packet.IsOversized,
                IsStronglyCompressed = IsStronglyCompressed(
                    packet.Width,
                    packet.Height,
                    width,
                    height),
                ToneMap = BuildToneMap(frozen, width, height)
            };
        }

        private static bool IsStronglyCompressed(
            int sourceWidth,
            int sourceHeight,
            int displayWidth,
            int displayHeight)
        {
            return displayWidth * 100 <= sourceWidth * StrongCompressionPercent ||
                   displayHeight * ConsoleCellAspectNumerator * 100 <=
                       sourceHeight * StrongCompressionPercent;
        }

        public static byte[] Resample(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        {
            if (source == null || ImageProtocol.GetPackedLength(sourceWidth, sourceHeight) != source.Length ||
                targetWidth < 1 || targetWidth > sourceWidth || targetHeight < 1 || targetHeight > sourceHeight)
                throw new ArgumentException("Invalid image resample dimensions.");

            byte[] target = new byte[(targetWidth * targetHeight + 1) / 2];
            for (int y = 0; y < targetHeight; y++)
            {
                int sourceY = Math.Min(sourceHeight - 1, y * sourceHeight / targetHeight);
                int sourceRow = sourceY * sourceWidth;
                int targetRow = y * targetWidth;
                for (int x = 0; x < targetWidth; x++)
                {
                    int sourceX = Math.Min(sourceWidth - 1, x * sourceWidth / targetWidth);
                    ImageProtocol.SetPixel(target, targetRow + x,
                        ImageProtocol.GetPixel(source, sourceRow + sourceX));
                }
            }
            return target;
        }

        public static void FillAsciiRow(FrozenImage image, int row, Span<char> destination)
        {
            if (image == null || row < 0 || row >= image.Height)
                throw new ArgumentOutOfRangeException(nameof(row));
            int count = Math.Min(image.Width, destination.Length);
            int offset = row * image.Width;
            byte[] toneMap = image.ToneMap;
            bool useToneMap = toneMap != null && toneMap.Length == 16;
            for (int x = 0; x < count; x++)
            {
                int level = ImageProtocol.GetPixel(image.PackedPixels, offset + x);
                destination[x] = DensityLut[useToneMap ? toneMap[level] : level];
            }
        }

        internal static byte[] BuildToneMap(byte[] packedPixels, int width, int height)
        {
            if (packedPixels == null ||
                ImageProtocol.GetPackedLength(width, height) != packedPixels.Length)
                throw new ArgumentException("Invalid image tone-map dimensions.");

            Span<int> histogram = stackalloc int[16];
            int pixelCount = width * height;
            for (int index = 0; index < pixelCount; index++)
                histogram[ImageProtocol.GetPixel(packedPixels, index)]++;

            int firstCount = 0;
            for (int level = 0; level < histogram.Length; level++)
            {
                if (histogram[level] == 0)
                    continue;
                firstCount = histogram[level];
                break;
            }

            byte[] toneMap = new byte[16];
            int range = pixelCount - firstCount;
            if (range <= 0)
            {
                for (int level = 0; level < toneMap.Length; level++)
                    toneMap[level] = (byte)level;
                return toneMap;
            }

            int cumulative = 0;
            for (int level = 0; level < toneMap.Length; level++)
            {
                cumulative += histogram[level];
                int mapped = Math.Max(0,
                    ((cumulative - firstCount) * 15 + range / 2) / range);

                // Histogram stretching may push the brightest shade of a dark
                // image to level 15. Reserve the blank glyph for actual white.
                if (level < 15 && mapped >= 15)
                    mapped = 14;
                toneMap[level] = (byte)mapped;
            }
            toneMap[15] = 15;
            return toneMap;
        }

        internal static bool RunSelfTest()
        {
            byte[] pixels = new byte[ImageProtocol.GetPackedLength(160, 72)];
            var packet = new ImagePacket { Width = 160, Height = 72, PackedPixels = pixels };
            FrozenImage narrow = Freeze(packet, 60, 20, "alex", false);
            FrozenImage outgoing = Freeze(packet, 60, 20, "alex", true);
            FrozenImage wide = Freeze(packet, 140, 40, "alex", false);
            int frozenWidth = narrow.Width;
            int clipped = Math.Min(25, narrow.Width);
            Span<char> row = stackalloc char[25];
            FillAsciiRow(narrow, 0, row);
            if (narrow.Width > 60 || narrow.Height > 11 ||
                wide.Width <= narrow.Width || wide.Height > 22 ||
                narrow.Width != frozenWidth || clipped > narrow.Width ||
                outgoing.Width != narrow.Width || outgoing.Height != narrow.Height ||
                !narrow.IsStronglyCompressed || !narrow.ShouldOfferLook ||
                wide.IsStronglyCompressed || wide.ShouldOfferLook)
                return false;

            byte[] rampPixels = new byte[ImageProtocol.GetPackedLength(16, 1)];
            for (int level = 0; level < 16; level++)
                ImageProtocol.SetPixel(rampPixels, level, (byte)level);
            var rampImage = new FrozenImage
            {
                Width = 16,
                Height = 1,
                PackedPixels = rampPixels
            };
            Span<char> ramp = stackalloc char[16];
            FillAsciiRow(rampImage, 0, ramp);
            if (!ramp.SequenceEqual("@%#8&MW*o=-:;,. ".AsSpan()))
                return false;

            byte[] contrastPixels = new byte[5];
            ImageProtocol.SetPixel(contrastPixels, 0, 0);
            for (int index = 1; index < 5; index++)
                ImageProtocol.SetPixel(contrastPixels, index, 14);
            for (int index = 5; index < 10; index++)
                ImageProtocol.SetPixel(contrastPixels, index, 15);
            byte[] toneMap = BuildToneMap(contrastPixels, 10, 1);
            if (toneMap[14] >= 14 || toneMap[15] != 15)
                return false;

            return true;
        }
    }
}
