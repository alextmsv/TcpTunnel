using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TCPTunnel
{
    internal static class ImageViewer
    {
        private const string ViewerArgument = "--image-view";
        private const uint CreateNewConsole = 0x00000010;
        private const int StartfUseShowWindow = 0x00000001;
        private const short SwShowMaximized = 3;

        public static bool TryRun(string[] args)
        {
            if (args == null || args.Length == 0 ||
                !String.Equals(args[0], ViewerArgument, StringComparison.Ordinal))
                return false;

            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "TCPTunnel image";
            ImagePacket packet;
            if (!TryParseArguments(args, out packet))
            {
                Console.WriteLine("Invalid TCPTunnel image data.");
                return true;
            }

            try
            {
                ExpandViewerConsole(packet.Width, packet.Height);
                Console.Clear();

                int width;
                int height;
                CalculateDisplayDimensions(
                    packet.Width,
                    packet.Height,
                    Console.WindowWidth,
                    Console.WindowHeight,
                    out width,
                    out height);

                FrozenImage image = new FrozenImage
                {
                    Width = width,
                    Height = height,
                    PackedPixels = ImageRenderer.Resample(
                        packet.PackedPixels,
                        packet.Width,
                        packet.Height,
                        width,
                        height)
                };
                image.ToneMap = ImageRenderer.BuildToneMap(
                    image.PackedPixels,
                    image.Width,
                    image.Height);
                char[] row = new char[width];
                Console.ForegroundColor = ConsoleColor.Gray;
                for (int y = 0; y < height; y++)
                {
                    ImageRenderer.FillAsciiRow(image, y, row);
                    Console.WriteLine(row);
                }
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine(Lang.Get(TextId.ImageViewerClose));
                Console.ReadKey(true);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is System.IO.IOException)
            {
                Console.ResetColor();
                Console.WriteLine(Lang.Get(TextId.ImageViewerTooSmall));
            }
            return true;
        }

        public static bool Launch(ImagePacket packet)
        {
            if (packet == null || ImageProtocol.GetPackedLength(packet.Width, packet.Height) != packet.PackedPixels.Length)
                return false;

            string executable = Environment.ProcessPath;
            if (String.IsNullOrWhiteSpace(executable))
                return false;
            string payload = Convert.ToBase64String(packet.PackedPixels);
            var commandLine = new StringBuilder();
            commandLine.Append('"').Append(executable.Replace("\"", "\"\"")).Append('"')
                .Append(' ').Append(ViewerArgument)
                .Append(' ').Append(packet.Width)
                .Append(' ').Append(packet.Height)
                .Append(' ').Append(payload)
                .Append(' ').Append(Lang.Current == AppLanguage.Russian ? "ru" : "en");

            STARTUPINFO startup = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = StartfUseShowWindow,
                wShowWindow = SwShowMaximized
            };
            PROCESS_INFORMATION process;
            bool started = CreateProcess(
                executable,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateNewConsole,
                IntPtr.Zero,
                null,
                ref startup,
                out process);
            if (!started)
                return false;

            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            return true;
        }

        internal static bool RunSelfTest()
        {
            int width;
            int height;
            CalculateDisplayDimensions(160, 72, 200, 100, out width, out height);
            if (width != 160 || height != 36)
                return false;

            CalculateDisplayDimensions(160, 72, 80, 25, out width, out height);
            if (width != 79 || height != 18)
                return false;

            CalculateDisplayDimensions(160, 72, 80, 10, out width, out height);
            return width == 31 && height == 7;
        }

        private static void CalculateDisplayDimensions(
            int sourceWidth,
            int sourceHeight,
            int windowWidth,
            int windowHeight,
            out int width,
            out int height)
        {
            width = Math.Max(1, Math.Min(sourceWidth, windowWidth - 1));
            height = Math.Max(1,
                (int)Math.Round((double)sourceHeight * width / (sourceWidth * 2)));
            int maxHeight = Math.Max(1, windowHeight - 3);
            if (height <= maxHeight)
                return;

            height = maxHeight;
            width = Math.Max(1, Math.Min(width,
                (int)Math.Round((double)sourceWidth * height * 2 / sourceHeight)));
        }

        private static void ExpandViewerConsole(int imageWidth, int imageHeight)
        {
            if (Console.IsOutputRedirected)
                return;

            try
            {
                int requiredWidth = Math.Max(
                    Console.WindowWidth,
                    Math.Min(Console.LargestWindowWidth, imageWidth + 1));
                int requiredHeight = Math.Max(
                    Console.WindowHeight,
                    Math.Min(Console.LargestWindowHeight, (imageHeight + 1) / 2 + 3));
                if (Console.BufferWidth < requiredWidth || Console.BufferHeight < requiredHeight)
                {
                    Console.SetBufferSize(
                        Math.Max(Console.BufferWidth, requiredWidth),
                        Math.Max(Console.BufferHeight, requiredHeight));
                }

                IntPtr window = GetConsoleWindow();
                if (window != IntPtr.Zero)
                    ShowWindow(window, SwShowMaximized);

                WaitForViewerGeometry();
                ConsoleGraphic.AlignViewport();
            }
            catch (Exception ex) when (
                ex is ArgumentOutOfRangeException ||
                ex is InvalidOperationException ||
                ex is System.IO.IOException)
            {
                // Terminal hosts differ in how much geometry control they allow.
                // Rendering still uses whatever viewport is available.
            }
        }

        private static void WaitForViewerGeometry()
        {
            int previousWidth = -1;
            int previousHeight = -1;
            int stableSamples = 0;
            for (int attempt = 0; attempt < 10 && stableSamples < 2; attempt++)
            {
                Thread.Sleep(25);
                int width = Console.WindowWidth;
                int height = Console.WindowHeight;
                if (width == previousWidth && height == previousHeight)
                    stableSamples++;
                else
                    stableSamples = 0;
                previousWidth = width;
                previousHeight = height;
            }
        }

        private static bool TryParseArguments(string[] args, out ImagePacket packet)
        {
            packet = null;
            if (args.Length != 5 ||
                (args[4] != "ru" && args[4] != "en"))
                return false;
            Lang.Set(args[4] == "ru" ? AppLanguage.Russian : AppLanguage.English);
            int width;
            int height;
            if (!Int32.TryParse(args[1], out width) || !Int32.TryParse(args[2], out height))
                return false;
            int expected = ImageProtocol.GetPackedLength(width, height);
            if (expected < 0 || args[3].Length != ((expected + 2) / 3) * 4)
                return false;
            byte[] pixels;
            try { pixels = Convert.FromBase64String(args[3]); }
            catch (FormatException) { return false; }
            if (pixels.Length != expected ||
                (((width * height) & 1) != 0 && (pixels[pixels.Length - 1] & 0x0F) != 0))
                return false;
            packet = new ImagePacket { Width = width, Height = height, PackedPixels = pixels };
            return true;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
    }
}
