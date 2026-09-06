using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TCPTunnel
{
    internal static partial class ImageViewer
    {
        private const string AnimationViewerArgument = "--animation-view";
        private const int AnimationFileMagic = 0x31414754; // TGA1
        private const int MaxAnimationFileBytes = 8 * 1024 * 1024;

        public static bool Launch(AnimatedImagePacket packet)
        {
            if (!ImageAnimationProtocol.IsValidPacket(packet, false))
                return false;

            string executable = Environment.ProcessPath;
            if (String.IsNullOrWhiteSpace(executable))
                return false;

            string path = null;
            try
            {
                string directory = GetAnimationViewerDirectory();
                Directory.CreateDirectory(directory);
                CleanupOldAnimationFiles(directory);
                path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tca");
                WriteAnimationFile(path, packet);

                var commandLine = new StringBuilder();
                commandLine.Append('"').Append(executable.Replace("\"", "\"\"")).Append('"')
                    .Append(' ').Append(AnimationViewerArgument)
                    .Append(' ').Append('"').Append(path.Replace("\"", "\"\"")).Append('"')
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
                path = null; // The viewer owns and deletes it now.
                return true;
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is NotSupportedException)
            {
                return false;
            }
            finally
            {
                if (path != null)
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }

        private static bool TryRunAnimation(string[] args)
        {
            if (args == null || args.Length == 0 ||
                !String.Equals(args[0], AnimationViewerArgument, StringComparison.Ordinal))
                return false;

            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "TCPTunnel GIF";
            string path = args.Length == 3 ? args[1] : null;
            bool ownsPath = false;
            try
            {
                if (args.Length != 3 || (args[2] != "ru" && args[2] != "en") ||
                    !IsTrustedAnimationViewerPath(path))
                {
                    Console.WriteLine("Invalid TCPTunnel animation data.");
                    return true;
                }
                ownsPath = true;
                Lang.Set(args[2] == "ru" ? AppLanguage.Russian : AppLanguage.English);
                AnimatedImagePacket packet = ReadAnimationFile(path);
                ExpandViewerConsole(packet.Width, packet.Height);
                PlayAnimationInViewer(packet);
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is InvalidDataException || ex is ArgumentException ||
                ex is InvalidOperationException)
            {
                Console.ResetColor();
                Console.WriteLine(Lang.Get(TextId.ImageViewerTooSmall));
            }
            finally
            {
                if (ownsPath && path != null)
                {
                    try { File.Delete(path); } catch { }
                }
            }
            return true;
        }

        private static void PlayAnimationInViewer(AnimatedImagePacket packet)
        {
            int previousWindowWidth = -1;
            int previousWindowHeight = -1;
            FrozenAnimation animation = null;
            ViewerLayout layout = new ViewerLayout();
            char[] row = Array.Empty<char>();
            int lastFrame = -1;
            var clock = Stopwatch.StartNew();

            Console.CursorVisible = false;
            try
            {
                while (true)
                {
                    try
                    {
                        if (Console.KeyAvailable)
                        {
                            Console.ReadKey(true);
                            break;
                        }

                        int windowWidth = Console.WindowWidth;
                        int windowHeight = Console.WindowHeight;
                        if (animation == null || windowWidth != previousWindowWidth ||
                            windowHeight != previousWindowHeight)
                        {
                            previousWindowWidth = windowWidth;
                            previousWindowHeight = windowHeight;
                            layout = CalculateViewerLayout(
                                packet.Width,
                                packet.Height,
                                windowWidth,
                                windowHeight);
                            animation = FreezeAnimationForViewer(packet, layout);
                            row = new char[animation.Width];
                            Console.Clear();
                            lastFrame = -1;
                        }

                        int frame = ImageRenderer.GetFrameIndex(animation, clock.ElapsedMilliseconds);
                        if (frame != lastFrame)
                        {
                            Console.ForegroundColor = ConsoleColor.Gray;
                            for (int y = 0; y < animation.Height; y++)
                            {
                                Console.SetCursorPosition(
                                    Console.WindowLeft + layout.Left,
                                    Console.WindowTop + layout.Top + y);
                                ImageRenderer.FillAsciiRow(animation, frame, y, row);
                                Console.Write(row);
                            }
                            Console.ResetColor();
                            WriteViewerPrompt(layout);
                            lastFrame = frame;
                        }
                        int wait = ImageRenderer.GetMillisecondsUntilNextFrame(
                            animation,
                            clock.ElapsedMilliseconds);
                        Thread.Sleep(Math.Max(5, Math.Min(50, wait)));
                    }
                    catch (Exception ex) when (
                        ex is ArgumentOutOfRangeException ||
                        ex is InvalidOperationException ||
                        ex is IOException)
                    {
                        Console.ResetColor();
                        animation = null;
                        previousWindowWidth = -1;
                        previousWindowHeight = -1;
                        lastFrame = -1;
                        Thread.Sleep(50);
                    }
                }
            }
            finally
            {
                Console.CursorVisible = true;
                Console.ResetColor();
            }
        }

        private static FrozenAnimation FreezeAnimationForViewer(
            AnimatedImagePacket packet,
            ViewerLayout layout)
        {
            var frames = new byte[packet.FrameCount][];
            var maps = new byte[packet.FrameCount][];
            var ends = new int[packet.FrameCount];
            int duration = 0;
            for (int index = 0; index < packet.FrameCount; index++)
            {
                frames[index] = ImageRenderer.ResampleCropped(
                    packet.PackedFrames[index],
                    packet.Width,
                    packet.Height,
                    layout.ScaledWidth,
                    layout.Height,
                    layout.CropLeft,
                    layout.Width);
                maps[index] = ImageRenderer.BuildToneMap(
                    frames[index],
                    layout.Width,
                    layout.Height);
                duration += packet.FrameDelays[index];
                ends[index] = duration;
            }
            return new FrozenAnimation
            {
                Width = layout.Width,
                Height = layout.Height,
                PackedFrames = frames,
                ToneMaps = maps,
                FrameDelays = packet.FrameDelays,
                FrameEndMilliseconds = ends,
                DurationMilliseconds = duration
            };
        }

        private static void WriteAnimationFile(string path, AnimatedImagePacket packet)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, false))
            {
                writer.Write(AnimationFileMagic);
                writer.Write(packet.Width);
                writer.Write(packet.Height);
                writer.Write(packet.FrameCount);
                writer.Write(packet.IsOversized);
                for (int index = 0; index < packet.FrameCount; index++)
                {
                    writer.Write(packet.FrameDelays[index]);
                    writer.Write(packet.PackedFrames[index].Length);
                    writer.Write(packet.PackedFrames[index]);
                }
            }
        }

        private static AnimatedImagePacket ReadAnimationFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxAnimationFileBytes)
                throw new InvalidDataException();

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, false))
            {
                if (reader.ReadInt32() != AnimationFileMagic)
                    throw new InvalidDataException();
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                int count = reader.ReadInt32();
                bool oversized = reader.ReadBoolean();
                if (!ImageAnimationProtocol.IsValidDimensions(width, height, count))
                    throw new InvalidDataException();
                int expected = ImageProtocol.GetPackedLength(width, height);
                var frames = new byte[count][];
                var delays = new ushort[count];
                for (int index = 0; index < count; index++)
                {
                    delays[index] = reader.ReadUInt16();
                    int length = reader.ReadInt32();
                    if (length != expected || length > stream.Length - stream.Position)
                        throw new InvalidDataException();
                    frames[index] = reader.ReadBytes(length);
                    if (frames[index].Length != length)
                        throw new EndOfStreamException();
                }
                if (stream.Position != stream.Length)
                    throw new InvalidDataException();
                var packet = new AnimatedImagePacket
                {
                    Width = width,
                    Height = height,
                    PackedFrames = frames,
                    FrameDelays = delays,
                    IsOversized = oversized
                };
                if (!ImageAnimationProtocol.IsValidPacket(packet, false))
                    throw new InvalidDataException();
                return packet;
            }
        }

        private static bool IsTrustedAnimationViewerPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                return false;
            try
            {
                string fullPath = Path.GetFullPath(path);
                string directory = Path.GetFullPath(GetAnimationViewerDirectory())
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
                       String.Equals(Path.GetExtension(fullPath), ".tca", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string GetAnimationViewerDirectory()
        {
            string localDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TCPTunnel",
                "viewer");
            try
            {
                Directory.CreateDirectory(localDirectory);
                string probe = Path.Combine(localDirectory, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, String.Empty);
                File.Delete(probe);
                return localDirectory;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                string fallback = Path.Combine(Path.GetTempPath(), "TCPTunnel", "viewer");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        private static void CleanupOldAnimationFiles(string directory)
        {
            try
            {
                string[] files = Directory.GetFiles(directory, "*.tca");
                DateTime cutoff = DateTime.UtcNow.AddHours(-1);
                for (int index = 0; index < files.Length && index < 128; index++)
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(files[index]) < cutoff)
                            File.Delete(files[index]);
                    }
                    catch { }
                }
            }
            catch { }
        }

        internal static bool RunAnimationSelfTest()
        {
            int length = ImageProtocol.GetPackedLength(3, 2);
            var packet = new AnimatedImagePacket
            {
                Width = 3,
                Height = 2,
                PackedFrames = new[] { new byte[length], new byte[length] },
                FrameDelays = new ushort[] { 90, 110 },
                IsOversized = true
            };
            string path = null;
            try
            {
                string directory = GetAnimationViewerDirectory();
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, "selftest-" + Guid.NewGuid().ToString("N") + ".tca");
                WriteAnimationFile(path, packet);
                AnimatedImagePacket restored = ReadAnimationFile(path);
                ViewerLayout layout = CalculateViewerLayout(
                    restored.Width,
                    restored.Height,
                    200,
                    100);
                FrozenAnimation fullScreen = FreezeAnimationForViewer(restored, layout);
                return IsTrustedAnimationViewerPath(path) && restored.Width == 3 &&
                       restored.Height == 2 && restored.FrameCount == 2 &&
                       restored.FrameDelays[1] == 110 && restored.IsOversized &&
                       fullScreen.Width == 200 && fullScreen.Height == 99 &&
                       fullScreen.ToneMaps[0].Length == 16;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (path != null)
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }
    }
}
