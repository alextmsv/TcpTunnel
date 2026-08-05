using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    public class ConsoleGraphic
    {
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
        private static long localSnakeLastMoveTimestamp = Stopwatch.GetTimestamp();
        private static int borderAnimationVersion;
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

        private static readonly IntPtr consoleOutputHandle = GetStdHandle(StandardOutputHandle);

        private interface IMenuOptionRenderer
        {
            void Draw(string text, int index, int baseLeft, int baseTop, bool selected, bool animate, int animationDelay);
        }

        private sealed class GraphicalMenuOptionRenderer : IMenuOptionRenderer
        {
            public void Draw(string text, int index, int baseLeft, int baseTop, bool selected, bool animate, int animationDelay)
            {
                Console.SetCursorPosition(baseLeft - 2 * index, baseTop + index);
                if (selected)
                {
                    Console.BackgroundColor = ConsoleColor.Cyan;
                    Console.ForegroundColor = ConsoleColor.Black;
                }
                else
                {
                    Console.ResetColor();
                }

                foreach (char symbol in text)
                {
                    Console.Write(symbol);
                    if (animate)
                        Thread.Sleep(animationDelay);
                }

                Console.ResetColor();
            }
        }

        private sealed class PlainMenuOptionRenderer : IMenuOptionRenderer
        {
            public void Draw(string text, int index, int baseLeft, int baseTop, bool selected, bool animate, int animationDelay)
            {
                const int plainLeft = 2;
                int row = baseTop + index;
                string option = " " + text + " ";
                int clearWidth = Math.Max(1, Math.Min(option.Length, Console.BufferWidth - plainLeft));

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

        public static void DrawMenuOption(
            string text,
            int index,
            int baseLeft,
            int baseTop,
            bool selected,
            bool animate,
            int animationDelay)
        {
            IMenuOptionRenderer renderer = Enabled ? graphicalMenuRenderer : plainMenuRenderer;
            renderer.Draw(text, index, baseLeft, baseTop, selected, animate, animationDelay);
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
        public static int ContentWidth => Enabled
            ? Math.Max(1, Console.WindowWidth - 2)
            : Math.Max(1, Console.BufferWidth - 1);
        public static int ContentBottom => Enabled
            ? Math.Max(ContentTop, Console.WindowHeight - 2)
            : Math.Max(ContentTop, Console.BufferHeight - 1);

        public static int EnsureContentSpace(int startRow, int requiredRows)
        {
            AlignViewport();
            if (!Enabled)
                return Math.Max(0, Math.Min(startRow, Console.BufferHeight - 1));

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
            if (!Enabled || Console.IsOutputRedirected || Console.WindowWidth < 4 || Console.WindowHeight < 4)
                return;

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

                        int width = Console.WindowWidth;
                        int height = Console.WindowHeight;
                        if (width < 4 || height < 4 || !borderIsDrawn ||
                            drawnBorderWidth != width || drawnBorderHeight != height)
                            return;

                        int perimeterLength = 2 * width + 2 * (height - 2);
                        long now = Stopwatch.GetTimestamp();
                        bool changed = AdvanceSnake(
                            ref borderAnimationStep,
                            ref localSnakeLastMoveTimestamp,
                            BorderAnimationDelayMilliseconds,
                            perimeterLength,
                            now);

                        foreach (SnakeState snake in remoteSnakes.Values)
                        {
                            changed |= AdvanceSnake(
                                ref snake.Step,
                                ref snake.LastMoveTimestamp,
                                snake.DelayMilliseconds,
                                perimeterLength,
                                now);
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
            long remainingTicks = GetRemainingMoveTicks(
                localSnakeLastMoveTimestamp,
                BorderAnimationDelayMilliseconds,
                now);

            foreach (SnakeState snake in remoteSnakes.Values)
            {
                remainingTicks = Math.Min(
                    remainingTicks,
                    GetRemainingMoveTicks(snake.LastMoveTimestamp, snake.DelayMilliseconds, now));
            }

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
            int step)
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
                    LastMoveTimestamp = Stopwatch.GetTimestamp()
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

        private static void RenderSnakeLayerLocked()
        {
            if (!Enabled || !borderIsDrawn || Console.IsOutputRedirected)
                return;

            int width = Console.WindowWidth;
            int height = Console.WindowHeight;
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

        public void Clear(int lineTime = 2, int cornerTime = 5)
        {
            StopBorderAnimation();
            Monitor.Enter(borderAnimationLock);
            try
            {
                AlignViewport();
                Console.ResetColor();

                if (!Enabled || Console.WindowWidth < 4 || Console.WindowHeight < 4)
                {
                    Console.Clear();
                    borderIsDrawn = false;
                    baseBorderAttributes = null;
                    desiredBorderAttributes = null;
                    renderedBorderAttributes = null;
                    Console.SetCursorPosition(0, 0);
                    return;
                }

                int width = Console.WindowWidth;
                int height = Console.WindowHeight;
                ClearGraphicsInterior(width, height);
                if (!borderIsDrawn || drawnBorderWidth != width || drawnBorderHeight != height)
                {
                    DrawRectangle(0, 0, width, height);
                    borderIsDrawn = true;
                    drawnBorderWidth = width;
                    drawnBorderHeight = height;
                    ResetBorderAttributeCacheLocked(width, height);
                }

                const string signature = "By alextmsv";
                int signatureLeft = Math.Max(1, width - signature.Length - 9);
                int signatureTop = Math.Max(1, height - 4);
                Console.SetCursorPosition(signatureLeft, signatureTop);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(signature);
                Console.ResetColor();

                TryRenderSnakeLayerLocked();

                // Весь последующий вывод должен начинаться внутри рамки.
                Console.SetCursorPosition(1, 1);
            }
            finally
            {
                Monitor.Exit(borderAnimationLock);
            }

            StartBorderAnimation();
        }
    }
}
