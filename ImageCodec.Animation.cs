using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TCPTunnel
{
    internal static partial class ImageCodec
    {
        private static readonly Guid PixelFormat32bppBgra =
            new Guid("6FDDC324-4E03-4BFE-B185-3D77768DC90F");

        private sealed class GifFrameMetadata
        {
            public int Left;
            public int Top;
            public int Width;
            public int Height;
            public ushort DelayMilliseconds;
            public byte Disposal;
        }

        private sealed class GifMetadata
        {
            public int Width;
            public int Height;
            public byte BackgroundGray;
            public readonly List<GifFrameMetadata> Frames = new List<GifFrameMetadata>();
        }

        private struct ScaledGifRectangle
        {
            public int Left;
            public int Top;
            public int Width;
            public int Height;
        }

        public static AnimatedImagePacket PrepareAnimation(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ImagePreparationException(ImagePreparationError.InvalidFile);

            FileInfo file;
            try { file = new FileInfo(path); }
            catch (Exception ex) { throw new ImagePreparationException(ImagePreparationError.InvalidFile, ex); }
            if (!file.Exists)
                throw new ImagePreparationException(ImagePreparationError.InvalidFile);
            long fileLength;
            try { fileLength = file.Length; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new ImagePreparationException(ImagePreparationError.InvalidFile, ex);
            }
            if (fileLength <= 0)
                throw new ImagePreparationException(ImagePreparationError.InvalidFile);
            if (fileLength > MaxSourceFileBytes)
                throw new ImagePreparationException(ImagePreparationError.FileTooLarge);

            GifMetadata metadata;
            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan))
                {
                    metadata = ParseGifMetadata(stream);
                }
            }
            catch (ImagePreparationException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException || ex is EndOfStreamException)
            {
                throw new ImagePreparationException(ImagePreparationError.InvalidFile, ex);
            }

            int initializeResult = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            bool uninitialize = initializeResult >= 0;
            if (initializeResult < 0 && initializeResult != RpcEChangedMode)
                Marshal.ThrowExceptionForHR(initializeResult);

            IWICImagingFactory factory = null;
            IWICBitmapDecoder decoder = null;
            try
            {
                IntPtr factoryPointer;
                Guid factoryClassId = ClsidWicImagingFactory;
                Guid factoryInterfaceId = IidWicImagingFactory;
                ThrowForResult(CoCreateInstance(
                    ref factoryClassId,
                    IntPtr.Zero,
                    1,
                    ref factoryInterfaceId,
                    out factoryPointer), false);
                try { factory = (IWICImagingFactory)Marshal.GetObjectForIUnknown(factoryPointer); }
                finally { Marshal.Release(factoryPointer); }

                ThrowForResult(factory.CreateDecoderFromFilename(
                    path,
                    IntPtr.Zero,
                    GenericRead,
                    0,
                    out decoder), false);
                uint frameCount;
                ThrowForResult(decoder.GetFrameCount(out frameCount), false);
                if (frameCount != metadata.Frames.Count || frameCount == 0 || frameCount > ImageAnimationProtocol.MaxFrames)
                    throw new ImagePreparationException(ImagePreparationError.AnimationTooLarge);

                int targetWidth;
                int targetHeight;
                CalculateWireDimensions(metadata.Width, metadata.Height, out targetWidth, out targetHeight);
                int pixelCount = checked(targetWidth * targetHeight);
                byte[] canvas = new byte[pixelCount];
                if (metadata.BackgroundGray != 0)
                    Array.Fill(canvas, metadata.BackgroundGray);
                byte[] restoreCanvas = null;
                bool restoreAvailable = false;
                byte[][] frames = new byte[frameCount][];
                ushort[] delays = new ushort[frameCount];

                for (int index = 0; index < frames.Length; index++)
                {
                    GifFrameMetadata current = metadata.Frames[index];
                    if (index > 0)
                    {
                        GifFrameMetadata previous = metadata.Frames[index - 1];
                        if (previous.Disposal == 2)
                        {
                            ClearGifRectangle(
                                canvas,
                                targetWidth,
                                targetHeight,
                                ScaleGifRectangle(previous, metadata.Width, metadata.Height, targetWidth, targetHeight),
                                metadata.BackgroundGray);
                        }
                        else if (previous.Disposal == 3 && restoreAvailable)
                        {
                            Buffer.BlockCopy(restoreCanvas, 0, canvas, 0, canvas.Length);
                        }
                    }

                    restoreAvailable = current.Disposal == 3;
                    if (restoreAvailable)
                    {
                        if (restoreCanvas == null)
                            restoreCanvas = new byte[canvas.Length];
                        Buffer.BlockCopy(canvas, 0, restoreCanvas, 0, canvas.Length);
                    }

                    IWICBitmapFrameDecode frame = null;
                    try
                    {
                        ThrowForResult(decoder.GetFrame((uint)index, out frame), false);
                        uint frameWidth;
                        uint frameHeight;
                        ThrowForResult(frame.GetSize(out frameWidth, out frameHeight), false);
                        if (frameWidth != current.Width || frameHeight != current.Height)
                            throw new ImagePreparationException(ImagePreparationError.DecodeFailed);
                        CompositeGifFrame(
                            factory,
                            frame,
                            current,
                            metadata.Width,
                            metadata.Height,
                            canvas,
                            targetWidth,
                            targetHeight);
                    }
                    finally
                    {
                        ReleaseComObject(frame);
                    }

                    frames[index] = PackGrayCanvas(canvas);
                    delays[index] = current.DelayMilliseconds;
                }

                long sourcePixels = (long)metadata.Width * metadata.Height;
                return new AnimatedImagePacket
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    PackedFrames = frames,
                    FrameDelays = delays,
                    IsOversized = metadata.Width > OversizedSourceDimension ||
                                  metadata.Height > OversizedSourceDimension ||
                                  sourcePixels > OversizedSourcePixels
                };
            }
            catch (ImagePreparationException)
            {
                throw;
            }
            catch (COMException ex)
            {
                throw new ImagePreparationException(ImagePreparationError.DecodeFailed, ex);
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is OverflowException || ex is ArgumentException)
            {
                throw new ImagePreparationException(ImagePreparationError.DecodeFailed, ex);
            }
            finally
            {
                ReleaseComObject(decoder);
                ReleaseComObject(factory);
                if (uninitialize)
                    CoUninitialize();
            }
        }

        internal static bool RunAnimationSelfTest()
        {
            byte[] gif = new byte[]
            {
                0x47,0x49,0x46,0x38,0x39,0x61, 0x01,0x00,0x01,0x00, 0x80,0x00,0x00,
                0x00,0x00,0x00, 0xFF,0xFF,0xFF,
                0x21,0xF9,0x04,0x00,0x0A,0x00,0x00,0x00,
                0x2C,0x00,0x00,0x00,0x00,0x01,0x00,0x01,0x00,0x00, 0x02,0x01,0x44,0x00,
                0x21,0xF9,0x04,0x00,0x0A,0x00,0x00,0x00,
                0x2C,0x00,0x00,0x00,0x00,0x01,0x00,0x01,0x00,0x00, 0x02,0x01,0x4C,0x00,
                0x3B
            };
            string path = Path.Combine(Path.GetTempPath(), "tcptunnel-gif-" + Guid.NewGuid().ToString("N") + ".gif");
            try
            {
                File.WriteAllBytes(path, gif);
                AnimatedImagePacket packet = PrepareAnimation(path);
                return packet.Width == 1 && packet.Height == 1 && packet.FrameCount == 2 &&
                       packet.FrameDelays[0] == 100 && packet.FrameDelays[1] == 100 &&
                       ImageAnimationProtocol.IsValidPacket(packet, false);
            }
            catch
            {
                return false;
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static GifMetadata ParseGifMetadata(Stream stream)
        {
            Span<byte> header = stackalloc byte[13];
            ReadExact(stream, header);
            bool validHeader = header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' &&
                               header[3] == (byte)'8' && (header[4] == (byte)'7' || header[4] == (byte)'9') &&
                               header[5] == (byte)'a';
            if (!validHeader)
                throw new ImagePreparationException(ImagePreparationError.DecodeFailed);

            var metadata = new GifMetadata
            {
                Width = header[6] | (header[7] << 8),
                Height = header[8] | (header[9] << 8),
                BackgroundGray = 255
            };
            ValidateSourceDimensions((uint)metadata.Width, (uint)metadata.Height);

            int packed = header[10];
            int backgroundIndex = header[11];
            if ((packed & 0x80) != 0)
            {
                int colorCount = 1 << ((packed & 0x07) + 1);
                byte[] palette = new byte[colorCount * 3];
                ReadExact(stream, palette);
                if (backgroundIndex < colorCount)
                {
                    int paletteOffset = backgroundIndex * 3;
                    metadata.BackgroundGray = ToGray(
                        palette[paletteOffset],
                        palette[paletteOffset + 1],
                        palette[paletteOffset + 2]);
                }
            }

            ushort pendingDelay = ImageAnimationProtocol.MinFrameDelayMilliseconds;
            byte pendingDisposal = 0;
            int totalDuration = 0;
            bool ended = false;
            while (!ended)
            {
                int marker = ReadRequiredByte(stream);
                switch (marker)
                {
                    case 0x21:
                    {
                        int label = ReadRequiredByte(stream);
                        if (label == 0xF9)
                        {
                            if (ReadRequiredByte(stream) != 4)
                                throw new ImagePreparationException(ImagePreparationError.DecodeFailed);
                            int control = ReadRequiredByte(stream);
                            int delay = ReadUInt16(stream) * 10;
                            ReadRequiredByte(stream);
                            if (ReadRequiredByte(stream) != 0)
                                throw new ImagePreparationException(ImagePreparationError.DecodeFailed);
                            pendingDelay = (ushort)Math.Max(
                                ImageAnimationProtocol.MinFrameDelayMilliseconds,
                                Math.Min(ImageAnimationProtocol.MaxFrameDelayMilliseconds, delay));
                            int disposal = (control >> 2) & 0x07;
                            pendingDisposal = (byte)(disposal <= 3 ? disposal : 0);
                        }
                        else
                        {
                            SkipGifSubBlocks(stream);
                            // A graphic control extension also applies to a plain-text
                            // rendering block. Do not accidentally carry its timing and
                            // disposal metadata into the following image frame.
                            if (label == 0x01)
                            {
                                pendingDelay = ImageAnimationProtocol.MinFrameDelayMilliseconds;
                                pendingDisposal = 0;
                            }
                        }
                        break;
                    }
                    case 0x2C:
                    {
                        int left = ReadUInt16(stream);
                        int top = ReadUInt16(stream);
                        int width = ReadUInt16(stream);
                        int height = ReadUInt16(stream);
                        int imagePacked = ReadRequiredByte(stream);
                        if (width < 1 || height < 1 || left < 0 || top < 0 ||
                            left + width > metadata.Width || top + height > metadata.Height)
                            throw new ImagePreparationException(ImagePreparationError.DecodeFailed);
                        if ((imagePacked & 0x80) != 0)
                        {
                            int localColorCount = 1 << ((imagePacked & 0x07) + 1);
                            SkipExact(stream, localColorCount * 3);
                        }
                        ReadRequiredByte(stream);
                        SkipGifSubBlocks(stream);

                        if (metadata.Frames.Count >= ImageAnimationProtocol.MaxFrames)
                            throw new ImagePreparationException(ImagePreparationError.AnimationTooLarge);
                        totalDuration += pendingDelay;
                        if (totalDuration > ImageAnimationProtocol.MaxDurationMilliseconds)
                            throw new ImagePreparationException(ImagePreparationError.AnimationTooLarge);
                        metadata.Frames.Add(new GifFrameMetadata
                        {
                            Left = left,
                            Top = top,
                            Width = width,
                            Height = height,
                            DelayMilliseconds = pendingDelay,
                            Disposal = pendingDisposal
                        });
                        pendingDelay = ImageAnimationProtocol.MinFrameDelayMilliseconds;
                        pendingDisposal = 0;
                        break;
                    }
                    case 0x3B:
                        ended = true;
                        break;
                    default:
                        throw new ImagePreparationException(ImagePreparationError.DecodeFailed);
                }
            }

            if (metadata.Frames.Count == 0)
                throw new ImagePreparationException(ImagePreparationError.DecodeFailed);
            return metadata;
        }

        private static void CompositeGifFrame(
            IWICImagingFactory factory,
            IWICBitmapFrameDecode frame,
            GifFrameMetadata metadata,
            int sourceWidth,
            int sourceHeight,
            byte[] canvas,
            int targetWidth,
            int targetHeight)
        {
            ScaledGifRectangle rectangle = ScaleGifRectangle(
                metadata,
                sourceWidth,
                sourceHeight,
                targetWidth,
                targetHeight);
            IWICBitmapScaler scaler = null;
            IWICFormatConverter converter = null;
            byte[] bgra = null;
            try
            {
                ThrowForResult(factory.CreateBitmapScaler(out scaler), false);
                ThrowForResult(scaler.Initialize(
                    frame,
                    (uint)rectangle.Width,
                    (uint)rectangle.Height,
                    3), false);
                ThrowForResult(factory.CreateFormatConverter(out converter), false);
                Guid format = PixelFormat32bppBgra;
                ThrowForResult(converter.Initialize(scaler, ref format, 0, IntPtr.Zero, 0.0, 0), false);

                int stride = checked(rectangle.Width * 4);
                int bufferLength = checked(stride * rectangle.Height);
                bgra = ArrayPool<byte>.Shared.Rent(bufferLength);
                GCHandle handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                try
                {
                    ThrowForResult(converter.CopyPixels(
                        IntPtr.Zero,
                        (uint)stride,
                        (uint)bufferLength,
                        handle.AddrOfPinnedObject()), false);
                }
                finally
                {
                    handle.Free();
                }

                for (int y = 0; y < rectangle.Height; y++)
                {
                    int sourceOffset = y * stride;
                    int targetOffset = (rectangle.Top + y) * targetWidth + rectangle.Left;
                    for (int x = 0; x < rectangle.Width; x++)
                    {
                        int pixelOffset = sourceOffset + x * 4;
                        int alpha = bgra[pixelOffset + 3];
                        if (alpha == 0)
                            continue;
                        byte gray = ToGray(
                            bgra[pixelOffset + 2],
                            bgra[pixelOffset + 1],
                            bgra[pixelOffset]);
                        int canvasIndex = targetOffset + x;
                        if (alpha == 255)
                            canvas[canvasIndex] = gray;
                        else
                            canvas[canvasIndex] = (byte)((gray * alpha + canvas[canvasIndex] * (255 - alpha) + 127) / 255);
                    }
                }
            }
            finally
            {
                if (bgra != null)
                    ArrayPool<byte>.Shared.Return(bgra);
                ReleaseComObject(converter);
                ReleaseComObject(scaler);
            }
        }

        private static ScaledGifRectangle ScaleGifRectangle(
            GifFrameMetadata frame,
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            int left = frame.Left * targetWidth / sourceWidth;
            int top = frame.Top * targetHeight / sourceHeight;
            int right = (int)Math.Ceiling((double)(frame.Left + frame.Width) * targetWidth / sourceWidth);
            int bottom = (int)Math.Ceiling((double)(frame.Top + frame.Height) * targetHeight / sourceHeight);
            right = Math.Max(left + 1, Math.Min(targetWidth, right));
            bottom = Math.Max(top + 1, Math.Min(targetHeight, bottom));
            return new ScaledGifRectangle
            {
                Left = left,
                Top = top,
                Width = right - left,
                Height = bottom - top
            };
        }

        private static void ClearGifRectangle(
            byte[] canvas,
            int canvasWidth,
            int canvasHeight,
            ScaledGifRectangle rectangle,
            byte background)
        {
            int bottom = Math.Min(canvasHeight, rectangle.Top + rectangle.Height);
            int right = Math.Min(canvasWidth, rectangle.Left + rectangle.Width);
            for (int y = Math.Max(0, rectangle.Top); y < bottom; y++)
            {
                int offset = y * canvasWidth + Math.Max(0, rectangle.Left);
                for (int x = Math.Max(0, rectangle.Left); x < right; x++)
                    canvas[offset++] = background;
            }
        }

        private static byte[] PackGrayCanvas(byte[] canvas)
        {
            byte[] packed = new byte[(canvas.Length + 1) / 2];
            for (int index = 0; index < canvas.Length; index++)
                ImageProtocol.SetPixel(packed, index, (byte)(canvas[index] >> 4));
            return packed;
        }

        private static byte ToGray(byte red, byte green, byte blue)
        {
            return (byte)((77 * red + 150 * green + 29 * blue + 128) >> 8);
        }

        private static void SkipGifSubBlocks(Stream stream)
        {
            while (true)
            {
                int length = ReadRequiredByte(stream);
                if (length == 0)
                    return;
                SkipExact(stream, length);
            }
        }

        private static int ReadUInt16(Stream stream)
        {
            int low = ReadRequiredByte(stream);
            int high = ReadRequiredByte(stream);
            return low | (high << 8);
        }

        private static int ReadRequiredByte(Stream stream)
        {
            int value = stream.ReadByte();
            if (value < 0)
                throw new EndOfStreamException();
            return value;
        }

        private static void ReadExact(Stream stream, Span<byte> destination)
        {
            int offset = 0;
            while (offset < destination.Length)
            {
                int read = stream.Read(destination.Slice(offset));
                if (read <= 0)
                    throw new EndOfStreamException();
                offset += read;
            }
        }

        private static void ReadExact(Stream stream, byte[] destination)
        {
            ReadExact(stream, destination.AsSpan());
        }

        private static void SkipExact(Stream stream, int length)
        {
            if (length < 0 || stream.Position > stream.Length - length)
                throw new EndOfStreamException();
            stream.Seek(length, SeekOrigin.Current);
        }
    }
}
