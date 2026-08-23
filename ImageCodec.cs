using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TCPTunnel
{
    internal enum ImagePreparationError
    {
        InvalidFile,
        FileTooLarge,
        DimensionsTooLarge,
        CodecUnavailable,
        DecodeFailed,
        AnimationTooLarge
    }

    internal sealed class ImagePreparationException : Exception
    {
        public ImagePreparationException(ImagePreparationError error, Exception innerException = null)
            : base(error.ToString(), innerException)
        {
            Error = error;
        }

        public ImagePreparationError Error { get; }
    }

    internal static partial class ImageCodec
    {
        public const long MaxSourceFileBytes = 32L * 1024 * 1024;
        public const int MaxSourceDimension = 16384;
        public const long MaxSourcePixels = 40_000_000;
        public const long OversizedSourcePixels = 12_000_000;
        public const int OversizedSourceDimension = 4096;

        private const uint GenericRead = 0x80000000;
        private const uint CoinitMultithreaded = 0x0;
        private const int RpcEChangedMode = unchecked((int)0x80010106);
        private static readonly Guid ClsidWicImagingFactory =
            new Guid("CACAF262-9370-4615-A13B-9F5539DA4C0A");
        private static readonly Guid IidWicImagingFactory =
            new Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70");
        private static readonly Guid PixelFormat8bppGray =
            new Guid("6FDDC324-4E03-4BFE-B185-3D77768DC908");

        public static ImagePacket Prepare(string path, bool isWebP)
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

            int initializeResult = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            bool uninitialize = initializeResult >= 0;
            if (initializeResult < 0 && initializeResult != RpcEChangedMode)
                Marshal.ThrowExceptionForHR(initializeResult);

            IWICImagingFactory factory = null;
            IWICBitmapDecoder decoder = null;
            IWICBitmapFrameDecode frame = null;
            IWICBitmapScaler scaler = null;
            IWICFormatConverter converter = null;
            try
            {
                IntPtr factoryPointer;
                Guid factoryClassId = ClsidWicImagingFactory;
                Guid factoryInterfaceId = IidWicImagingFactory;
                int result = CoCreateInstance(
                    ref factoryClassId,
                    IntPtr.Zero,
                    1,
                    ref factoryInterfaceId,
                    out factoryPointer);
                ThrowForResult(result, isWebP);
                try
                {
                    factory = (IWICImagingFactory)Marshal.GetObjectForIUnknown(factoryPointer);
                }
                finally
                {
                    Marshal.Release(factoryPointer);
                }

                result = factory.CreateDecoderFromFilename(path, IntPtr.Zero, GenericRead, 0, out decoder);
                ThrowForResult(result, isWebP);
                uint frameCount;
                ThrowForResult(decoder.GetFrameCount(out frameCount), isWebP);
                if (frameCount == 0)
                    throw new ImagePreparationException(ImagePreparationError.DecodeFailed);
                ThrowForResult(decoder.GetFrame(0, out frame), isWebP);

                uint sourceWidth;
                uint sourceHeight;
                ThrowForResult(frame.GetSize(out sourceWidth, out sourceHeight), isWebP);
                ValidateSourceDimensions(sourceWidth, sourceHeight);

                int targetWidth;
                int targetHeight;
                CalculateWireDimensions((int)sourceWidth, (int)sourceHeight, out targetWidth, out targetHeight);
                ThrowForResult(factory.CreateBitmapScaler(out scaler), isWebP);
                ThrowForResult(scaler.Initialize(frame, (uint)targetWidth, (uint)targetHeight, 3), isWebP);
                ThrowForResult(factory.CreateFormatConverter(out converter), isWebP);
                Guid grayFormat = PixelFormat8bppGray;
                ThrowForResult(converter.Initialize(
                    scaler,
                    ref grayFormat,
                    0,
                    IntPtr.Zero,
                    0.0,
                    0), isWebP);

                byte[] gray = new byte[targetWidth * targetHeight];
                GCHandle grayHandle = GCHandle.Alloc(gray, GCHandleType.Pinned);
                try
                {
                    ThrowForResult(converter.CopyPixels(
                        IntPtr.Zero,
                        (uint)targetWidth,
                        (uint)gray.Length,
                        grayHandle.AddrOfPinnedObject()), isWebP);
                }
                finally
                {
                    grayHandle.Free();
                }
                byte[] packed = new byte[(gray.Length + 1) / 2];
                for (int index = 0; index < gray.Length; index++)
                    ImageProtocol.SetPixel(packed, index, (byte)(gray[index] >> 4));

                long sourcePixels = (long)sourceWidth * sourceHeight;
                return new ImagePacket
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    PackedPixels = packed,
                    IsOversized = sourceWidth > OversizedSourceDimension ||
                                  sourceHeight > OversizedSourceDimension ||
                                  sourcePixels > OversizedSourcePixels
                };
            }
            catch (ImagePreparationException)
            {
                throw;
            }
            catch (COMException ex)
            {
                throw new ImagePreparationException(
                    isWebP ? ImagePreparationError.CodecUnavailable : ImagePreparationError.DecodeFailed,
                    ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new ImagePreparationException(ImagePreparationError.InvalidFile, ex);
            }
            finally
            {
                ReleaseComObject(converter);
                ReleaseComObject(scaler);
                ReleaseComObject(frame);
                ReleaseComObject(decoder);
                ReleaseComObject(factory);
                if (uninitialize)
                    CoUninitialize();
            }
        }

        private static void CalculateWireDimensions(int width, int height, out int targetWidth, out int targetHeight)
        {
            targetWidth = Math.Min(width, ImageProtocol.MaxWireWidth);
            targetHeight = Math.Max(1, (int)Math.Round((double)height * targetWidth / width));
            if (targetHeight > ImageProtocol.MaxWireHeight)
            {
                targetHeight = ImageProtocol.MaxWireHeight;
                targetWidth = Math.Max(1, (int)Math.Round((double)width * targetHeight / height));
            }
            targetWidth = Math.Min(targetWidth, ImageProtocol.MaxWireWidth);
        }

        private static void ValidateSourceDimensions(uint width, uint height)
        {
            if (width == 0 || height == 0 || width > MaxSourceDimension || height > MaxSourceDimension ||
                (long)width * height > MaxSourcePixels)
                throw new ImagePreparationException(ImagePreparationError.DimensionsTooLarge);
        }

        private static void ThrowForResult(int result, bool isWebP)
        {
            if (result >= 0)
                return;
            var error = isWebP ? ImagePreparationError.CodecUnavailable : ImagePreparationError.DecodeFailed;
            throw new ImagePreparationException(error, Marshal.GetExceptionForHR(result));
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try { Marshal.FinalReleaseComObject(value); } catch { }
            }
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid classId,
            IntPtr outer,
            uint context,
            ref Guid interfaceId,
            out IntPtr instance);

        [ComImport, Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICImagingFactory
        {
            [PreserveSig] int CreateDecoderFromFilename(
                [MarshalAs(UnmanagedType.LPWStr)] string filename,
                IntPtr vendor,
                uint desiredAccess,
                uint metadataOptions,
                out IWICBitmapDecoder decoder);
            [PreserveSig] int CreateDecoderFromStream(IntPtr stream, IntPtr vendor, uint options, out IntPtr decoder);
            [PreserveSig] int CreateDecoderFromFileHandle(UIntPtr file, IntPtr vendor, uint options, out IntPtr decoder);
            [PreserveSig] int CreateComponentInfo(ref Guid component, out IntPtr info);
            [PreserveSig] int CreateDecoder(ref Guid container, IntPtr vendor, out IntPtr decoder);
            [PreserveSig] int CreateEncoder(ref Guid container, IntPtr vendor, out IntPtr encoder);
            [PreserveSig] int CreatePalette(out IntPtr palette);
            [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
            [PreserveSig] int CreateBitmapScaler(out IWICBitmapScaler scaler);
        }

        [ComImport, Guid("9EDDE9E7-8DEE-47EA-99DF-E6FAF2ED44BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapDecoder
        {
            [PreserveSig] int QueryCapability(IntPtr stream, out uint capability);
            [PreserveSig] int Initialize(IntPtr stream, uint options);
            [PreserveSig] int GetContainerFormat(out Guid format);
            [PreserveSig] int GetDecoderInfo(out IntPtr info);
            [PreserveSig] int CopyPalette(IntPtr palette);
            [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
            [PreserveSig] int GetPreview(out IntPtr source);
            [PreserveSig] int GetColorContexts(uint count, IntPtr contexts, out uint actualCount);
            [PreserveSig] int GetThumbnail(out IntPtr source);
            [PreserveSig] int GetFrameCount(out uint count);
            [PreserveSig] int GetFrame(uint index, out IWICBitmapFrameDecode frame);
        }

        [ComImport, Guid("00000120-A8F2-4877-BA0A-FD2B6645FB94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapSource
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            [PreserveSig] int GetPixelFormat(out Guid format);
            [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
            [PreserveSig] int CopyPalette(IntPtr palette);
            [PreserveSig] int CopyPixels(IntPtr rectangle, uint stride, uint bufferSize, IntPtr buffer);
        }

        [ComImport, Guid("3B16811B-6A43-4EC9-A813-3D930C13B940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapFrameDecode : IWICBitmapSource
        {
            [PreserveSig] new int GetSize(out uint width, out uint height);
            [PreserveSig] new int GetPixelFormat(out Guid format);
            [PreserveSig] new int GetResolution(out double dpiX, out double dpiY);
            [PreserveSig] new int CopyPalette(IntPtr palette);
            [PreserveSig] new int CopyPixels(IntPtr rectangle, uint stride, uint bufferSize, IntPtr buffer);
            [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
            [PreserveSig] int GetColorContexts(uint count, IntPtr contexts, out uint actualCount);
            [PreserveSig] int GetThumbnail(out IntPtr source);
        }

        [ComImport, Guid("00000302-A8F2-4877-BA0A-FD2B6645FB94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapScaler : IWICBitmapSource
        {
            [PreserveSig] new int GetSize(out uint width, out uint height);
            [PreserveSig] new int GetPixelFormat(out Guid format);
            [PreserveSig] new int GetResolution(out double dpiX, out double dpiY);
            [PreserveSig] new int CopyPalette(IntPtr palette);
            [PreserveSig] new int CopyPixels(IntPtr rectangle, uint stride, uint bufferSize, IntPtr buffer);
            [PreserveSig] int Initialize(IWICBitmapSource source, uint width, uint height, uint mode);
        }

        [ComImport, Guid("00000301-A8F2-4877-BA0A-FD2B6645FB94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICFormatConverter : IWICBitmapSource
        {
            [PreserveSig] new int GetSize(out uint width, out uint height);
            [PreserveSig] new int GetPixelFormat(out Guid format);
            [PreserveSig] new int GetResolution(out double dpiX, out double dpiY);
            [PreserveSig] new int CopyPalette(IntPtr palette);
            [PreserveSig] new int CopyPixels(IntPtr rectangle, uint stride, uint bufferSize, IntPtr buffer);
            [PreserveSig] int Initialize(
                IWICBitmapSource source,
                ref Guid destinationFormat,
                uint dither,
                IntPtr palette,
                double alphaThreshold,
                uint paletteType);
            [PreserveSig] int CanConvert(ref Guid sourceFormat, ref Guid destinationFormat, out int canConvert);
        }
    }
}
