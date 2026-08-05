using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public class ConsoleGraphic
    {
        internal struct ConsoleGeometry
        {
            public int WindowWidth;
            public int WindowHeight;
            public int BufferWidth;
            public int BufferHeight;

            public int DrawableWidth => Math.Min(WindowWidth, BufferWidth);
            public int DrawableHeight => Math.Min(WindowHeight, BufferHeight);

            public bool IsSameAs(ConsoleGeometry other)
            {
                return WindowWidth == other.WindowWidth &&
                       WindowHeight == other.WindowHeight &&
                       BufferWidth == other.BufferWidth &&
                       BufferHeight == other.BufferHeight;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ConsoleCoordinate
        {
            public short X;
            public short Y;

            public ConsoleCoordinate(int x, int y)
            {
                X = (short)x;
                Y = (short)y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SmallRectangle
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ConsoleScreenBufferInfo
        {
            public ConsoleCoordinate Size;
            public ConsoleCoordinate CursorPosition;
            public ushort Attributes;
            public SmallRectangle Window;
            public ConsoleCoordinate MaximumWindowSize;
        }

        private struct BorderCell
        {
            public int X;
            public int Y;

            public BorderCell(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        private sealed class SnakeState
        {
            public ConsoleColor Color;
            public int DelayMilliseconds;
            public int Step;
            public long LastMoveTimestamp;
            public bool Paused;
        }

        private const int StandardOutputHandle = -11;
        private const int BorderSnakeLength = 7;
        private const int AnimationClockIntervalMilliseconds = 20;
        private static readonly IntPtr invalidHandleValue = new IntPtr(-1);
        private static readonly object borderAnimationLock = new object();
        private static readonly Dictionary<string, SnakeState> remoteSnakes =
            new Dictionary<string, SnakeState>(StringComparer.OrdinalIgnoreCase);
        private static readonly ushort[] singleAttributeBuffer = new ushort[1];
        private static bool consoleGraphicsEnabled = true;
        private static bool borderIsDrawn;
        private static int drawnBorderWidth;
        private static int drawnBorderHeight;
        private static int borderAnimationDelayMilliseconds = 75;
        private static ConsoleColor borderSnakeColor = ConsoleColor.Green;
        private static int borderAnimationStep;
        private static bool borderSnakePaused;
        private static long localSnakeLastMoveTimestamp = Stopwatch.GetTimestamp();
        private static int borderAnimationVersion;
        private static int reservedBottomRows;
        private static ushort[] baseBorderAttributes;
        private static ushort[] desiredBorderAttributes;
        private static ushort[] renderedBorderAttributes;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteConsoleOutputAttribute(
            IntPtr consoleOutput,
            ushort[] attributes,
            uint length,
            ConsoleCoordinate writeCoordinate,
            out uint attributesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(
            IntPtr consoleOutput,
            out ConsoleScreenBufferInfo consoleScreenBufferInfo);

        private static readonly IntPtr consoleOutputHandle = GetStdHandle(StandardOutputHandle);

        private interface IMenuOptionRenderer
        {
            bool Draw(string text, int index, int baseLeft, int baseTop, bool selected, bool animate, int animationDelay);
        }

        private static bool TryGetGraphicalMenuPosition(
            ConsoleGeometry geometry,
            int index,
            int baseLeft,
            int baseTop,
            out int left,
            out int row,
            out int rightExclusive)
        {
            row = baseTop + index;
            rightExclusive = Math.Min(geometry.BufferWidth, geometry.WindowWidth - 1);
            int maximumRow = geometry.DrawableHeight - 1;
            if (row < 0 || row > maximumRow || rightExclusive <= ContentLeft)
            {
                left = ContentLeft;
                return false;
            }

            int maximumLeft = Math.Max(ContentLeft, rightExclusive - 1);
            int minimumLeft = Math.Min(maximumLeft, ContentLeft + 3);
            left = Math.Max(minimumLeft, Math.Min(baseLeft - 2 * index, maximumLeft));
            return true;
        }

        private static bool DrawGraphicalSelectionMarker(
            ConsoleGeometry geometry,
            int left,
            int row,
            bool selected)
        {
            int markerLeft = Math.Max(ContentLeft, left - 3);
            int markerWidth = Math.Min(3, Math.Max(0, left - markerLeft));
            if (markerWidth == 0)
                return true;

            Console.ResetColor();
            Console.SetCursorPosition(markerLeft, row);
            if (selected)
                Console.ForegroundColor = ConsoleColor.Cyan;

            string marker = selected ? ">> " : "   ";
            Console.Write(marker.Substring(0, markerWidth));
            Console.ResetColor();
            return IsConsoleGeometryCurrent(geometry);
        }

        private sealed class GraphicalMenuOptionRenderer : IMenuOptionRenderer
        {
            public bool Draw(string text, int index, int baseLeft, int baseTop, bool selected, bool animate, int animationDelay)
            {
                try
                {
                    ConsoleGeometry geometry;
                    if (!TryCaptureConsoleGeometry(out geometry))
                        return false;

                    int left;
                    int row;
                    int rightExclusive;
                    if (!TryGetGraphicalMenuPosition(
                        geometry,
                        index,
                        baseLeft,
                        baseTop,
                        out left,
                        out row,
                        out rightExclusive))
                        return true;

                    if (!DrawGraphicalSelectionMarker(geometry, left, row, selected))
                        return false;

                    int characterCount = Math.Max(0, Math.Min(text.Length, rightExclusive - left));
                    if (characterCount == 0)
                        return true;

                    Console.SetCursorPosition(left, row);
                    if (selected)
                    {
                        Console.BackgroundColor = ConsoleColor.Cyan;
                        Console.ForegroundColor = ConsoleColor.Black;
                    }
                    else
                    {
                        Console.ResetColor();
                    }

                    for (int characterIndex = 0; characterIndex < characterCount; characterIndex++)
                    {
                        Console.Write(text[characterIndex]);
                        if (animate)
                            Thread.Sleep(animationDelay);
                    }

                    Console.ResetColor();
                    return IsConsoleGeometryCurrent(geometry);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
            }
        }

        private sealed class PlainMenuOptionRenderer : IMenuOptionRenderer
        {
            public bool Draw(string text, int index, int baseLeft, int baseTop, bool selected, bool animate, int animationDelay)
            {
                try
                {
                    ConsoleGeometry geometry;
                    if (!TryCaptureConsoleGeometry(out geometry))
                        return false;

                    int row = baseTop + index;
                    int maximumRow = geometry.DrawableHeight - 1;
                    if (row < 0 || row > maximumRow)
                        return true;

                    int plainLeft = Math.Min(2, Math.Max(0, geometry.BufferWidth - 1));
                    string option = " " + text + " ";
                    int clearWidth = Math.Max(1, Math.Min(option.Length, geometry.BufferWidth - plainLeft));

                    Console.ResetColor();
                    Console.SetCursorPosition(plainLeft, row);
                    Console.Write(new string(' ', clearWidth));
                    Console.SetCursorPosition(plainLeft, row);
                    if (selected)
                    {
                        Console.BackgroundColor = ConsoleColor.White;
                        Console.ForegroundColor = ConsoleColor.Black;
                    }

                    Console.Write(option.Substring(0, clearWidth));
                    Console.ResetColor();
                    return IsConsoleGeometryCurrent(geometry);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
            }
        }

        private static readonly IMenuOptionRenderer graphicalMenuRenderer = new GraphicalMenuOptionRenderer();
        private static readonly IMenuOptionRenderer plainMenuRenderer = new PlainMenuOptionRenderer();

        public static bool Enabled
        {
            get { return Volatile.Read(ref consoleGraphicsEnabled); }
            set
            {
                Volatile.Write(ref consoleGraphicsEnabled, value);
                if (!value)
                {
                    Volatile.Write(ref reservedBottomRows, 0);
                    StopBorderAnimation();
                    ClearRemoteSnakes();
                }
            }
        }

        public static int BorderAnimationDelayMilliseconds
        {
            get { return Volatile.Read(ref borderAnimationDelayMilliseconds); }
            set
            {
                lock (borderAnimationLock)
                {
                    Volatile.Write(ref borderAnimationDelayMilliseconds, Math.Max(20, Math.Min(1000, value)));
                    localSnakeLastMoveTimestamp = Stopwatch.GetTimestamp();
                }
            }
        }

        public static ConsoleColor BorderSnakeColor
        {
            get
            {
                lock (borderAnimationLock)
                    return borderSnakeColor;
            }
            set
            {
                if (!IsVisibleSnakeColor(value))
                    throw new ArgumentOutOfRangeException(nameof(value));

                lock (borderAnimationLock)
                {
                    borderSnakeColor = value;
                    TryRenderSnakeLayerLocked();
                }
            }
        }

        public static int CurrentBorderSnakeStep
        {
            get
            {
                lock (borderAnimationLock)
                    return borderAnimationStep;
            }
        }

        public static bool BorderSnakePaused
        {
            get
            {
                lock (borderAnimationLock)
                    return borderSnakePaused;
            }
        }

        public static bool ToggleBorderSnakePause()
        {
            lock (borderAnimationLock)
            {
                long now = Stopwatch.GetTimestamp();
                if (!borderSnakePaused && borderIsDrawn && drawnBorderWidth >= 4 && drawnBorderHeight >= 4)
                {
                    int perimeterLength = 2 * drawnBorderWidth + 2 * (drawnBorderHeight - 2);
                    AdvanceSnake(
                        ref borderAnimationStep,
                        ref localSnakeLastMoveTimestamp,
                        BorderAnimationDelayMilliseconds,
                        perimeterLength,
                        now);
                }

                borderSnakePaused = !borderSnakePaused;
                localSnakeLastMoveTimestamp = now;
                TryRenderSnakeLayerLocked();
                return borderSnakePaused;
            }
        }

        public static bool DrawMenuOption(
            string text,
            int index,
            int baseLeft,
            int baseTop,
            bool selected,
            bool animate,
            int animationDelay)
        {
            IMenuOptionRenderer renderer = Enabled ? graphicalMenuRenderer : plainMenuRenderer;
            return renderer.Draw(text, index, baseLeft, baseTop, selected, animate, animationDelay);
        }

        public static bool DrawMenuSelectionMarker(
            int index,
            int baseLeft,
            int baseTop,
            bool selected)
        {
            if (!Enabled)
                return true;

            try
            {
                ConsoleGeometry geometry;
                if (!TryCaptureConsoleGeometry(out geometry))
                    return false;

                int left;
                int row;
                int rightExclusive;
                if (!TryGetGraphicalMenuPosition(
                    geometry,
                    index,
                    baseLeft,
                    baseTop,
                    out left,
                    out row,
                    out rightExclusive))
                    return true;

                return DrawGraphicalSelectionMarker(geometry, left, row, selected);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        internal static bool TryCaptureConsoleGeometry(out ConsoleGeometry geometry)
        {
            geometry = new ConsoleGeometry();
            try
            {
                ConsoleScreenBufferInfo nativeInfo;
                if (consoleOutputHandle != IntPtr.Zero &&
                    consoleOutputHandle != invalidHandleValue &&
                    GetConsoleScreenBufferInfo(consoleOutputHandle, out nativeInfo))
                {
                    geometry = new ConsoleGeometry
                    {
                        WindowWidth = nativeInfo.Window.Right - nativeInfo.Window.Left + 1,
                        WindowHeight = nativeInfo.Window.Bottom - nativeInfo.Window.Top + 1,
                        BufferWidth = nativeInfo.Size.X,
                        BufferHeight = nativeInfo.Size.Y
                    };
                    return geometry.DrawableWidth > 0 && geometry.DrawableHeight > 0;
                }

                ConsoleGeometry first = new ConsoleGeometry
                {
                    WindowWidth = Console.WindowWidth,
                    WindowHeight = Console.WindowHeight,
                    BufferWidth = Console.BufferWidth,
                    BufferHeight = Console.BufferHeight
                };
                ConsoleGeometry second = new ConsoleGeometry
                {
                    WindowWidth = Console.WindowWidth,
                    WindowHeight = Console.WindowHeight,
                    BufferWidth = Console.BufferWidth,
                    BufferHeight = Console.BufferHeight
                };

                if (!first.IsSameAs(second) || second.DrawableWidth <= 0 || second.DrawableHeight <= 0)
                    return false;

                geometry = second;
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        internal static bool IsConsoleGeometryCurrent(ConsoleGeometry expected)
        {
            ConsoleGeometry current;
            return TryCaptureConsoleGeometry(out current) && current.IsSameAs(expected);
        }

        public static void ConfigureConsole(int requestedWidth = 71, int requestedHeight = 16)
        {
            try
            {
                int width = Math.Max(1, Math.Min(requestedWidth, Console.LargestWindowWidth));
                int height = Math.Max(1, Math.Min(requestedHeight, Console.LargestWindowHeight));

                if (Console.BufferWidth < width || Console.BufferHeight < height + 1)
                {
                    Console.SetBufferSize(
                        Math.Max(Console.BufferWidth, width),
                        Math.Max(Console.BufferHeight, height + 1));
                }

                Console.SetWindowPosition(0, 0);
                Console.SetWindowSize(width, height);
                Console.SetWindowPosition(0, 0);

                // Широкий buffer создаёт горизонтальную прокрутку и визуально
                // сдвигает рамку относительно окна. Высоту оставляем для истории.
                if (Console.BufferWidth != Console.WindowWidth)
                {
                    Console.SetBufferSize(
                        Console.WindowWidth,
                        Math.Max(Console.BufferHeight, Console.WindowHeight + 1));
                }
            }
            catch (Exception)
            {
                // Некоторые терминалы не разрешают менять геометрию программно.
                // В этом случае интерфейс использует доступный размер окна.
            }
        }

        public static void AlignViewport()
        {
            try
            {
                if (Console.WindowLeft != 0)
                    Console.SetWindowPosition(0, Console.WindowTop);

                if (Console.BufferWidth != Console.WindowWidth)
                {
                    Console.SetBufferSize(
                        Console.WindowWidth,
                        Math.Max(Console.BufferHeight, Console.WindowHeight + 1));
                }
            }
            catch (Exception)
            {
            }
        }

        public static int ContentLeft => Enabled ? 1 : 0;
        public static int ContentTop => Enabled ? 1 : 0;
        public static int PhysicalContentBottom => Enabled
            ? Math.Max(ContentTop, Console.WindowHeight - 2)
            : Math.Max(ContentTop, Console.BufferHeight - 1);
        public static int ContentWidth => Enabled
            ? Math.Max(1, Console.WindowWidth - 2)
            : Math.Max(1, Math.Min(Console.WindowWidth, Console.BufferWidth) - 1);
        public static int ContentBottom => Enabled
            ? Math.Max(ContentTop, PhysicalContentBottom - Volatile.Read(ref reservedBottomRows))
            : Math.Max(ContentTop, Console.BufferHeight - 1);

        public static void SetReservedBottomRows(int rows)
        {
            Volatile.Write(ref reservedBottomRows, Enabled ? Math.Max(0, Math.Min(6, rows)) : 0);
        }

        public static bool TrySetContentCursor(int left, int row)
        {
            try
            {
                ConsoleGeometry geometry;
                if (!TryCaptureConsoleGeometry(out geometry))
                    return false;

                int safeLeft = Math.Max(ContentLeft, Math.Min(left, geometry.DrawableWidth - 2));
                int safeRow = Math.Max(ContentTop, Math.Min(row, ContentBottom));
                Console.SetCursorPosition(safeLeft, safeRow);
                return IsConsoleGeometryCurrent(geometry);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public static bool WriteCenteredLine(
            string message,
            int row,
            ConsoleColor color,
            bool animate = false,
            int animationDelayMilliseconds = 0)
        {
            if (!Enabled)
                return false;

            lock (borderAnimationLock)
            {
                try
                {
                    ConsoleGeometry geometry;
                    if (!TryCaptureConsoleGeometry(out geometry))
                        return false;

                    int contentWidth = Math.Max(1, geometry.DrawableWidth - 2);
                    int safeRow = Math.Max(1, Math.Min(row, geometry.DrawableHeight - 2));
                    string text = SanitizeConsoleText(message);
                    if (text.Length > contentWidth)
                        text = text.Substring(0, contentWidth);

                    Console.ResetColor();
                    Console.SetCursorPosition(1, safeRow);
                    Console.Write(new string(' ', contentWidth));

                    int left = 1 + Math.Max(0, (contentWidth - text.Length) / 2);
                    Console.SetCursorPosition(left, safeRow);
                    Console.ForegroundColor = color;
                    if (animate && animationDelayMilliseconds > 0)
                    {
                        foreach (char character in text)
                        {
                            Console.Write(character);
                            Thread.Sleep(animationDelayMilliseconds);
                        }
                    }
                    else
                    {
                        Console.Write(text);
                    }

                    Console.ResetColor();
                    return IsConsoleGeometryCurrent(geometry);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
            }
        }

        public static bool WriteBottomStatus(
            string message,
            ConsoleColor color,
            int rowFromBottom = 0,
            bool animate = false,
            int animationDelayMilliseconds = 0)
        {
            if (!Enabled)
                return false;

            try
            {
                ConsoleGeometry geometry;
                if (!TryCaptureConsoleGeometry(out geometry))
                    return false;

                int previousLeft = Console.CursorLeft;
                int previousTop = Console.CursorTop;
                int bottom = Math.Max(1, geometry.DrawableHeight - 2);
                bool written = WriteCenteredLine(
                    message,
                    Math.Max(1, bottom - Math.Max(0, rowFromBottom)),
                    color,
                    animate,
                    animationDelayMilliseconds);

                if (IsConsoleGeometryCurrent(geometry))
                {
                    int safeLeft = Math.Max(0, Math.Min(previousLeft, geometry.BufferWidth - 1));
                    int safeTop = Math.Max(0, Math.Min(previousTop, geometry.BufferHeight - 1));
                    Console.SetCursorPosition(safeLeft, safeTop);
                }

                return written;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public static void DrawServerEndpointCard(string address, int port, bool online = true)
        {
            if (!Enabled)
                return;

            SetReservedBottomRows(2);
            string safeAddress = String.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address;
            IPAddress parsedAddress;
            if (IPAddress.TryParse(safeAddress, out parsedAddress) &&
                parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                safeAddress = "[" + safeAddress + "]";

            string endpoint = safeAddress + ":" + port;
            int separatorLength = Math.Max(8, Math.Min(24, endpoint.Length + 4));
            ConsoleColor stateColor = online ? ConsoleColor.Green : ConsoleColor.Red;
            WriteBottomStatus(Lang.Get(online ? TextId.HubOnline : TextId.HubOffline), stateColor, 2);
            WriteBottomStatus(endpoint, stateColor, 1);
            WriteBottomStatus(new string('-', separatorLength), ConsoleColor.DarkGray);
            TrySetContentCursor(ContentLeft, ContentTop);
        }

        private static string SanitizeConsoleText(string message)
        {
            if (String.IsNullOrEmpty(message))
                return String.Empty;

            char[] characters = message.ToCharArray();
            for (int index = 0; index < characters.Length; index++)
            {
                if (Char.IsControl(characters[index]))
                    characters[index] = ' ';
            }

            return new string(characters);
        }

        public static int EnsureContentSpace(int startRow, int requiredRows)
        {
            AlignViewport();
            if (!Enabled)
            {
                int availableHeight = Math.Max(1, Console.BufferHeight);
                requiredRows = Math.Max(1, Math.Min(requiredRows, availableHeight));
                return Math.Max(0, Math.Min(startRow, availableHeight - requiredRows));
            }

            int top = ContentTop;
            int bottom = ContentBottom;
            int height = bottom - top + 1;
            requiredRows = Math.Max(1, Math.Min(requiredRows, height));
            startRow = Math.Max(top, startRow);

            int overflow = startRow + requiredRows - 1 - bottom;
            if (overflow <= 0)
                return startRow;

            int shift = Math.Min(height, overflow);
            int sourceHeight = height - shift;
            if (sourceHeight > 0)
            {
                Console.MoveBufferArea(
                    ContentLeft,
                    top + shift,
                    ContentWidth,
                    sourceHeight,
                    ContentLeft,
                    top,
                    ' ',
                    ConsoleColor.White,
                    ConsoleColor.Black);
            }

            for (int row = Math.Max(top, bottom - shift + 1); row <= bottom; row++)
                ClearContentRow(row);

            return Math.Max(top, bottom - requiredRows + 1);
        }

        public static void ClearContentRow(int row)
        {
            int safeRow = Math.Max(ContentTop, Math.Min(row, Console.BufferHeight - 1));
            Console.SetCursorPosition(ContentLeft, safeRow);
            Console.Write(new string(' ', ContentWidth));
        }

        public static void WriteContentLine(string message)
        {
            if (!Enabled)
            {
                Console.WriteLine(message);
                return;
            }

            AlignViewport();
            char[] characters = (message ?? String.Empty).ToCharArray();
            for (int index = 0; index < characters.Length; index++)
            {
                if (Char.IsControl(characters[index]))
                    characters[index] = ' ';
            }

            string text = new string(characters);
            int offset = 0;
            int width = ContentWidth;

            do
            {
                int row = EnsureContentSpace(Console.CursorTop, 2);
                ClearContentRow(row);
                Console.SetCursorPosition(ContentLeft, row);

                int count = Math.Min(width, text.Length - offset);
                if (count > 0)
                {
                    Console.Write(text.Substring(offset, count));
                    offset += count;
                }

                Console.SetCursorPosition(ContentLeft, Math.Min(ContentBottom, row + 1));
            }
            while (offset < text.Length);
        }

        private static void DrawRectangle(int x, int y, int width, int height)
        {
            int right = x + width - 1;
            int bottom = y + height - 1;

            Console.ForegroundColor = ConsoleColor.Blue;
            Console.SetCursorPosition(x, y);
            Console.Write('+');
            Console.SetCursorPosition(right, y);
            Console.Write('+');
            Console.SetCursorPosition(x, bottom);
            Console.Write('+');
            Console.SetCursorPosition(right, bottom);
            Console.Write('+');

            Console.ForegroundColor = ConsoleColor.Magenta;
            string horizontal = new string('-', Math.Max(0, width - 2));
            if (horizontal.Length > 0)
            {
                Console.SetCursorPosition(x + 1, y);
                Console.Write(horizontal);
                Console.SetCursorPosition(x + 1, bottom);
                Console.Write(horizontal);
            }

            for (int row = y + 1; row < bottom; row++)
            {
                Console.SetCursorPosition(x, row);
                Console.Write('|');
                Console.SetCursorPosition(right, row);
                Console.Write('|');
            }

            Console.ResetColor();
        }

        private static void StartBorderAnimation()
        {
            try
            {
                ConsoleGeometry geometry;
                if (!Enabled || Console.IsOutputRedirected ||
                    !TryCaptureConsoleGeometry(out geometry) ||
                    geometry.DrawableWidth < 4 || geometry.DrawableHeight < 4)
                    return;
            }
            catch (IOException)
            {
                return;
            }

            int version = Interlocked.Increment(ref borderAnimationVersion);
            Task.Run(() => AnimateBorderAsync(version));
        }

        private static void StopBorderAnimation()
        {
            Interlocked.Increment(ref borderAnimationVersion);
        }

        private static async Task AnimateBorderAsync(int version)
        {
            int waitMilliseconds = AnimationClockIntervalMilliseconds;
            while (Enabled && version == Volatile.Read(ref borderAnimationVersion))
            {
                try
                {
                    await Task.Delay(waitMilliseconds).ConfigureAwait(false);

                    lock (borderAnimationLock)
                    {
                        if (!Enabled || version != Volatile.Read(ref borderAnimationVersion))
                            return;

                        ConsoleGeometry geometry;
                        if (!TryCaptureConsoleGeometry(out geometry))
                            return;

                        int width = geometry.DrawableWidth;
                        int height = geometry.DrawableHeight;
                        if (width < 4 || height < 4 || !borderIsDrawn ||
                            drawnBorderWidth != width || drawnBorderHeight != height)
                            return;

                        int perimeterLength = 2 * width + 2 * (height - 2);
                        long now = Stopwatch.GetTimestamp();
                        bool changed = false;
                        if (!borderSnakePaused)
                        {
                            changed = AdvanceSnake(
                                ref borderAnimationStep,
                                ref localSnakeLastMoveTimestamp,
                                BorderAnimationDelayMilliseconds,
                                perimeterLength,
                                now);
                        }

                        foreach (SnakeState snake in remoteSnakes.Values)
                        {
                            if (!snake.Paused)
                            {
                                changed |= AdvanceSnake(
                                    ref snake.Step,
                                    ref snake.LastMoveTimestamp,
                                    snake.DelayMilliseconds,
                                    perimeterLength,
                                    now);
                            }
                        }

                        if (changed)
                            TryRenderSnakeLayerLocked();

                        waitMilliseconds = GetNextAnimationDelayLocked(now);
                    }
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        private static int GetNextAnimationDelayLocked(long now)
        {
            long remainingTicks = borderSnakePaused
                ? Int64.MaxValue
                : GetRemainingMoveTicks(
                    localSnakeLastMoveTimestamp,
                    BorderAnimationDelayMilliseconds,
                    now);

            foreach (SnakeState snake in remoteSnakes.Values)
            {
                if (!snake.Paused)
                {
                    remainingTicks = Math.Min(
                        remainingTicks,
                        GetRemainingMoveTicks(snake.LastMoveTimestamp, snake.DelayMilliseconds, now));
                }
            }

            if (remainingTicks == Int64.MaxValue)
                return 50;

            long milliseconds = (remainingTicks * 1000L + Stopwatch.Frequency - 1L) / Stopwatch.Frequency;
            return (int)Math.Max(1L, Math.Min(50L, milliseconds));
        }

        private static long GetRemainingMoveTicks(long lastMoveTimestamp, int delayMilliseconds, long now)
        {
            long delayTicks = Math.Max(1L, Stopwatch.Frequency * delayMilliseconds / 1000L);
            return Math.Max(1L, delayTicks - (now - lastMoveTimestamp));
        }

        private static bool AdvanceSnake(
            ref int step,
            ref long lastMoveTimestamp,
            int delayMilliseconds,
            int perimeterLength,
            long now)
        {
            long delayTicks = Math.Max(1L, Stopwatch.Frequency * delayMilliseconds / 1000L);
            long elapsedTicks = now - lastMoveTimestamp;
            if (elapsedTicks < delayTicks)
                return false;

            long moves = elapsedTicks / delayTicks;
            step = NormalizeBorderStep(step + (int)(moves % perimeterLength), perimeterLength);
            lastMoveTimestamp += moves * delayTicks;
            return true;
        }

        private static int NormalizeBorderStep(int step, int perimeterLength)
        {
            return ((step % perimeterLength) + perimeterLength) % perimeterLength;
        }

        private static BorderCell GetBorderCell(int index, int width, int height)
        {
            if (index < width)
                return new BorderCell(index, 0);

            index -= width;
            if (index < height - 1)
                return new BorderCell(width - 1, index + 1);

            index -= height - 1;
            if (index < width - 1)
                return new BorderCell(width - 2 - index, height - 1);

            index -= width - 1;
            return new BorderCell(0, height - 2 - index);
        }

        public static void SetRemoteSnake(
            string participant,
            int delayMilliseconds,
            ConsoleColor color,
            int step,
            bool paused = false)
        {
            if (String.IsNullOrWhiteSpace(participant) || !IsVisibleSnakeColor(color))
                return;

            lock (borderAnimationLock)
            {
                if (!Enabled)
                    return;

                remoteSnakes[participant] = new SnakeState
                {
                    Color = color,
                    DelayMilliseconds = Math.Max(20, Math.Min(1000, delayMilliseconds)),
                    Step = step,
                    LastMoveTimestamp = Stopwatch.GetTimestamp(),
                    Paused = paused
                };
                TryRenderSnakeLayerLocked();
            }
        }

        public static void RemoveRemoteSnake(string participant)
        {
            if (String.IsNullOrWhiteSpace(participant))
                return;

            lock (borderAnimationLock)
            {
                if (remoteSnakes.Remove(participant))
                    TryRenderSnakeLayerLocked();
            }
        }

        public static void ClearRemoteSnakes()
        {
            lock (borderAnimationLock)
            {
                if (remoteSnakes.Count == 0)
                    return;

                remoteSnakes.Clear();
                TryRenderSnakeLayerLocked();
            }
        }

        public static bool IsVisibleSnakeColor(ConsoleColor color)
        {
            return color >= ConsoleColor.DarkBlue && color <= ConsoleColor.White &&
                   color != ConsoleColor.Black && color != ConsoleColor.DarkGray;
        }

        private static void ResetBorderAttributeCacheLocked(int width, int height)
        {
            int perimeterLength = 2 * width + 2 * (height - 2);
            baseBorderAttributes = new ushort[perimeterLength];
            desiredBorderAttributes = new ushort[perimeterLength];
            renderedBorderAttributes = new ushort[perimeterLength];

            for (int index = 0; index < perimeterLength; index++)
            {
                BorderCell cell = GetBorderCell(index, width, height);
                ushort attribute = GetBaseBorderAttribute(cell, width, height);
                baseBorderAttributes[index] = attribute;
                desiredBorderAttributes[index] = attribute;
                renderedBorderAttributes[index] = attribute;
            }
        }

        private static void SetBorderBaseAttributeRangeLocked(
            int left,
            int top,
            int length,
            ushort attribute,
            int width,
            int height)
        {
            if (baseBorderAttributes == null || desiredBorderAttributes == null || renderedBorderAttributes == null)
                return;

            for (int index = 0; index < baseBorderAttributes.Length; index++)
            {
                BorderCell cell = GetBorderCell(index, width, height);
                if (cell.Y != top || cell.X < left || cell.X >= left + length)
                    continue;

                baseBorderAttributes[index] = attribute;
                desiredBorderAttributes[index] = attribute;
                renderedBorderAttributes[index] = attribute;
            }
        }

        private static void RenderSnakeLayerLocked()
        {
            if (!Enabled || !borderIsDrawn || Console.IsOutputRedirected)
                return;

            ConsoleGeometry geometry;
            if (!TryCaptureConsoleGeometry(out geometry))
                return;

            int width = geometry.DrawableWidth;
            int height = geometry.DrawableHeight;
            if (width != drawnBorderWidth || height != drawnBorderHeight || width < 4 || height < 4)
                return;

            int perimeterLength = 2 * width + 2 * (height - 2);
            if (baseBorderAttributes == null || desiredBorderAttributes == null || renderedBorderAttributes == null ||
                baseBorderAttributes.Length != perimeterLength ||
                desiredBorderAttributes.Length != perimeterLength || renderedBorderAttributes.Length != perimeterLength)
            {
                ResetBorderAttributeCacheLocked(width, height);
            }

            Array.Copy(baseBorderAttributes, desiredBorderAttributes, perimeterLength);

            foreach (SnakeState snake in remoteSnakes.Values)
                OverlaySnakeLocked(snake.Step, snake.Color, perimeterLength);

            OverlaySnakeLocked(borderAnimationStep, borderSnakeColor, perimeterLength);

            for (int index = 0; index < perimeterLength; index++)
            {
                ushort desired = desiredBorderAttributes[index];
                if (renderedBorderAttributes[index] == desired)
                    continue;

                SetBorderCellAttribute(GetBorderCell(index, width, height), desired);
                renderedBorderAttributes[index] = desired;
            }
        }

        private static void TryRenderSnakeLayerLocked()
        {
            try
            {
                RenderSnakeLayerLocked();
            }
            catch (Exception)
            {
                borderIsDrawn = false;
                baseBorderAttributes = null;
                desiredBorderAttributes = null;
                renderedBorderAttributes = null;
            }
        }

        private static void OverlaySnakeLocked(int step, ConsoleColor color, int perimeterLength)
        {
            int normalizedStep = NormalizeBorderStep(step, perimeterLength);
            int visibleLength = Math.Min(BorderSnakeLength, perimeterLength);
            for (int offset = 0; offset < visibleLength; offset++)
            {
                int index = NormalizeBorderStep(normalizedStep - offset, perimeterLength);
                desiredBorderAttributes[index] = (ushort)color;
            }
        }

        private static ushort GetBaseBorderAttribute(BorderCell cell, int width, int height)
        {
            bool isCorner = (cell.X == 0 || cell.X == width - 1) &&
                            (cell.Y == 0 || cell.Y == height - 1);
            return (ushort)(isCorner ? ConsoleColor.Blue : ConsoleColor.Magenta);
        }

        private static void SetBorderCellAttribute(BorderCell cell, ushort attribute)
        {
            if (consoleOutputHandle == IntPtr.Zero || consoleOutputHandle == invalidHandleValue)
                return;

            singleAttributeBuffer[0] = attribute;
            uint written;
            WriteConsoleOutputAttribute(
                consoleOutputHandle,
                singleAttributeBuffer,
                1,
                new ConsoleCoordinate(cell.X, cell.Y),
                out written);
        }

        private static void ClearGraphicsInterior(int width, int height)
        {
            string emptyRow = new string(' ', Math.Max(0, width - 2));
            for (int row = 1; row < height - 1; row++)
            {
                Console.SetCursorPosition(1, row);
                Console.Write(emptyRow);
            }
        }

        private static void InvalidateBorderLocked()
        {
            borderIsDrawn = false;
            drawnBorderWidth = 0;
            drawnBorderHeight = 0;
            baseBorderAttributes = null;
            desiredBorderAttributes = null;
            renderedBorderAttributes = null;
        }

        public void Clear(int lineTime = 2, int cornerTime = 5)
        {
            TryClear(lineTime, cornerTime);
        }

        public bool TryClear(int lineTime = 2, int cornerTime = 5)
        {
            StopBorderAnimation();
            bool frameCompleted = false;
            AlignViewport();
            lock (borderAnimationLock)
            {
                for (int attempt = 0; attempt < 2 && !frameCompleted; attempt++)
                {
                    ConsoleGeometry geometry;
                    if (!TryCaptureConsoleGeometry(out geometry))
                        break;

                    try
                    {
                        Console.ResetColor();

                        int width = geometry.DrawableWidth;
                        int height = geometry.DrawableHeight;
                        if (!Enabled || width < 4 || height < 4)
                        {
                            Console.Clear();
                            InvalidateBorderLocked();
                            Console.SetCursorPosition(0, 0);
                        }
                        else
                        {
                            bool dimensionsChanged = borderIsDrawn &&
                                                     (drawnBorderWidth != width || drawnBorderHeight != height);
                            if (dimensionsChanged)
                            {
                                Console.Clear();
                                InvalidateBorderLocked();
                            }
                            else
                            {
                                ClearGraphicsInterior(width, height);
                            }

                            if (!borderIsDrawn || drawnBorderWidth != width || drawnBorderHeight != height)
                            {
                                DrawRectangle(0, 0, width, height);
                                borderIsDrawn = true;
                                drawnBorderWidth = width;
                                drawnBorderHeight = height;
                                ResetBorderAttributeCacheLocked(width, height);
                            }

                            const string signature = "By alextmsv";
                            if (width >= signature.Length + 3 && height >= 4)
                            {
                                int signatureLeft = Math.Max(1, width - signature.Length - 2);
                                int signatureTop = height - 1;
                                Console.SetCursorPosition(signatureLeft, signatureTop);
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.Write(signature);
                                Console.ResetColor();
                                SetBorderBaseAttributeRangeLocked(
                                    signatureLeft,
                                    signatureTop,
                                    signature.Length,
                                    (ushort)ConsoleColor.DarkGray,
                                    width,
                                    height);
                            }

                            TryRenderSnakeLayerLocked();

                            Console.SetCursorPosition(1, 1);
                        }

                        frameCompleted = IsConsoleGeometryCurrent(geometry);
                        if (!frameCompleted)
                            InvalidateBorderLocked();
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        InvalidateBorderLocked();
                    }
                    catch (IOException)
                    {
                        InvalidateBorderLocked();
                    }
                }
            }

            if (frameCompleted)
                StartBorderAnimation();

            return frameCompleted;
        }
    }
}
