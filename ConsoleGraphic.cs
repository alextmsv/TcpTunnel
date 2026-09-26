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
        static ConsoleGraphic()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => SetMenuScreen(false);
        }

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

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct ConsoleCell
        {
            [FieldOffset(0)] public char Character;
            [FieldOffset(2)] public ushort Attributes;
        }

        private static readonly ConsoleCell[] borderCellBuffer = new ConsoleCell[1];

        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleOutputW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WriteConsoleOutput(IntPtr output, ConsoleCell[] cells,
            ConsoleCoordinate size, ConsoleCoordinate origin, ref SmallRectangle region);

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
            public char Glyph;
            public int DelayMilliseconds;
            public int Step;
            public long LastMoveTimestamp;
            public bool Paused;
        }

        private const int StandardOutputHandle = -11;
        private const int BorderSnakeLength = 7;
        private const int AnimationClockIntervalMilliseconds = 20;
        private static readonly IntPtr invalidHandleValue = new IntPtr(-1);
        internal static readonly object borderAnimationLock = new object();
        private static readonly Dictionary<string, SnakeState> remoteSnakes =
            new Dictionary<string, SnakeState>(StringComparer.OrdinalIgnoreCase);
        private static ushort[] rowAttributeBuffer = Array.Empty<ushort>();
        private static bool consoleGraphicsEnabled = true;
        private static bool graphicsTemporarilySuspended;
        private static bool borderIsDrawn;
        private static int drawnBorderWidth;
        private static int drawnBorderHeight;
        private static int borderAnimationDelayMilliseconds = 75;
        private static ConsoleColor borderSnakeColor = ConsoleColor.Green;
        private static char borderSnakeGlyph = '-';
        private static int borderAnimationStep;
        private static bool borderSnakePaused;
        private static long localSnakeLastMoveTimestamp = Stopwatch.GetTimestamp();
        private static int borderAnimationVersion;
        private static int borderAnimationRunning;
        private static int reservedBottomRows;
        private static ushort[] baseBorderAttributes;
        private static ushort[] desiredBorderAttributes;
        private static ushort[] renderedBorderAttributes;
        private static char[] baseBorderCharacters;
        private static char[] desiredBorderCharacters;
        private static char[] renderedBorderCharacters;
        private static bool forceFullBorderResync;
        private static int signatureLeft;
        private static int signatureTop;
        private static int signatureLength;
        private static int visualThemeRevision;
        private static bool menuScreenActive;
        private static uint menuPreviousOutputMode;
        private static bool menuPreviousCursorVisible;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);

        // Keep full-screen menu redraws out of Terminal's scrollback; chat uses the main buffer.
        internal const bool MenuScreenSupported = false;

        internal static void SetMenuScreen(bool active)
        {
            if (active && (!MenuScreenSupported || !Enabled || Console.IsOutputRedirected || ConsoleWindowState.ClassicWindow != IntPtr.Zero))
                active = false;
            lock (borderAnimationLock)
            {
                if (menuScreenActive == active) return;
                if (consoleOutputHandle == IntPtr.Zero || consoleOutputHandle == invalidHandleValue) return;
                StopBorderAnimation();
                try
                {
                    if (active)
                    {
                        if (!GetConsoleMode(consoleOutputHandle, out menuPreviousOutputMode) ||
                            !SetConsoleMode(consoleOutputHandle, menuPreviousOutputMode | 4)) return;
                        menuPreviousCursorVisible = Console.CursorVisible;
                        Console.Write("\u001b[?1049h\u001b[?25l");
                        menuScreenActive = true;
                    }
                    else
                    {
                        Console.Write("\u001b[?1049l");
                        Console.CursorVisible = menuPreviousCursorVisible;
                        SetConsoleMode(consoleOutputHandle, menuPreviousOutputMode);
                        menuScreenActive = false;
                    }
                    InvalidateBorderLocked();
                }
                catch (IOException)
                {
                    SetConsoleMode(consoleOutputHandle, menuPreviousOutputMode);
                    menuScreenActive = false;
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteConsoleOutputAttribute(
            IntPtr consoleOutput,
            ushort[] attributes,
            uint length,
            ConsoleCoordinate writeCoordinate,
            out uint attributesWritten);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WriteConsoleOutputCharacter(
            IntPtr consoleOutput,
            char[] characters,
            uint length,
            ConsoleCoordinate writeCoordinate,
            out uint charactersWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(
            IntPtr consoleOutput,
            out ConsoleScreenBufferInfo consoleScreenBufferInfo);

        private static readonly IntPtr consoleOutputHandle = GetStdHandle(StandardOutputHandle);

        internal const int VirtualTerminalProcessingFlag = 0x0004;

        internal static bool EnableVirtualTerminalOutput()
        {
            if (consoleOutputHandle == IntPtr.Zero || consoleOutputHandle == invalidHandleValue || Console.IsOutputRedirected)
                return false;
            try
            {
                if (!GetConsoleMode(consoleOutputHandle, out uint mode))
                    return false;
                return (mode & VirtualTerminalProcessingFlag) != 0 ||
                       SetConsoleMode(consoleOutputHandle, mode | VirtualTerminalProcessingFlag);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Caller must ResetColor() before and after - never mix with ForegroundColor/BackgroundColor=.
        internal static void WriteCustomColor(int? foregroundRgb, int? backgroundRgb)
        {
            if (!foregroundRgb.HasValue && !backgroundRgb.HasValue)
                return;
            try
            {
                var sequence = new System.Text.StringBuilder(32);
                if (foregroundRgb.HasValue)
                    AppendTrueColorSgr(sequence, 38, foregroundRgb.Value);
                if (backgroundRgb.HasValue)
                    AppendTrueColorSgr(sequence, 48, backgroundRgb.Value);
                Console.Write(sequence.ToString());
            }
            catch (IOException)
            {
            }
        }

        internal static void ApplyContentColors(ConsoleColor foreground)
        {
            Console.ResetColor();
            if (ConsoleTheme.HasContentBackground)
            {
                Console.Write("\u001b[" + ConsoleTheme.ForegroundSgr(ConsoleTheme.ContentColor(foreground,
                    ConsoleTheme.BackgroundCustomRgb)) + ";49m");
            }
            else
                Console.ForegroundColor = foreground;
        }

        private static void AppendTrueColorSgr(System.Text.StringBuilder sequence, int sgrBase, int rgb)
        {
            sequence.Append("[").Append(sgrBase).Append(";2;")
                .Append((rgb >> 16) & 0xFF).Append(';')
                .Append((rgb >> 8) & 0xFF).Append(';')
                .Append(rgb & 0xFF).Append('m');
        }

        private interface IMenuOptionRenderer
        {
            bool Draw(
                string text,
                int index,
                int baseLeft,
                int baseTop,
                bool selected,
                bool animate,
                int animationDelay,
                int previewStart,
                ConsoleColor? previewColor,
                int previewLength,
                bool highlightOnly);
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
            int maximumRow = geometry.DrawableHeight - 2;
            if (row < 1 || row > maximumRow || rightExclusive <= ContentLeft)
            {
                left = ContentLeft;
                return false;
            }

            int maximumLeft = Math.Max(ContentLeft, rightExclusive - 1);
            int minimumLeft = Math.Min(maximumLeft, ContentLeft + 3);
            left = Math.Max(minimumLeft, Math.Min(baseLeft, maximumLeft));
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

            ApplyContentColors(ConsoleTheme.MenuText);
            Console.SetCursorPosition(markerLeft, row);
            selected &= ConsoleTheme.SelectionStyle == MenuSelectionStyle.Arrow;
            if (selected)
            {
                if (ConsoleTheme.SelectionUsesCustomColor)
                    WriteCustomColor(ConsoleTheme.SelectionCustomRgb, null);
                else
                    ApplyContentColors(ConsoleTheme.SelectionBackground);
            }

            string marker = selected ? " > " : "   ";
            Console.Write(marker.Substring(0, markerWidth));
            Console.ResetColor();
            return IsConsoleGeometryCurrent(geometry);
        }

        private sealed class GraphicalMenuOptionRenderer : IMenuOptionRenderer
        {
            public bool Draw(
                string text,
                int index,
                int baseLeft,
                int baseTop,
                bool selected,
                bool animate,
                int animationDelay,
                int previewStart,
                ConsoleColor? previewColor,
                int previewLength,
                bool highlightOnly)
            {
                try
                {
                    ConsoleGeometry geometry;
                    int left, row, rightExclusive, characterCount;
                    string label, option;
                    lock (borderAnimationLock)
                    {
                        if (!TryCaptureConsoleGeometry(out geometry))
                            return false;

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

                        int availableWidth = Math.Max(0, rightExclusive - left);
                        bool brackets = selected && ConsoleTheme.SelectionStyle == MenuSelectionStyle.Brackets;
                        int labelWidth = Math.Max(0, availableWidth - 4);
                        label = text.Substring(0, Math.Min(text.Length, labelWidth));
                        option = brackets ? "[ " + label + " ]" : "  " + label + "  ";
                        characterCount = Math.Min(option.Length, availableWidth);
                        if (characterCount == 0)
                            return true;
                    }

                    for (int characterIndex = 0; characterIndex < characterCount; characterIndex++)
                    {
                        lock (borderAnimationLock)
                        {
                            if (!IsConsoleGeometryCurrent(geometry))
                                return false;
                            Console.SetCursorPosition(left + characterIndex, row);
                            bool inPreview = previewStart >= 0 && characterIndex < label.Length + 2 && characterIndex >= previewStart + 2 &&
                                (previewLength <= 0 || characterIndex < previewStart + 2 + previewLength);
                            if (highlightOnly && selected)
                            {
                                bool bracket = ConsoleTheme.SelectionStyle == MenuSelectionStyle.Brackets &&
                                    (characterIndex < 2 || characterIndex >= label.Length + 2);
                                ApplyGraphicalOptionColors(inPreview || bracket);
                            }
                            else
                            {
                                ApplyGraphicalOptionColors(selected);
                                if (previewColor.HasValue && inPreview)
                                {
                                    Console.ResetColor();
                                    ApplyPreviewColor(previewColor.Value);
                                }
                            }
                            Console.Write(option[characterIndex]);
                            Console.ResetColor();
                        }
                        // Allow local and remote snakes to render between text animation frames.
                        if (animate && ConsoleTheme.SelectionStyle == MenuSelectionStyle.Arrow && !IsInputWaiting())
                        {
                            Thread.Sleep(animationDelay);
                            lock (borderAnimationLock)
                                AdvanceAndRenderBorderTickLocked(Stopwatch.GetTimestamp());
                        }
                    }
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

        private static void ApplyGraphicalOptionColors(bool selected)
        {
            Console.ResetColor();
            bool fill = selected && ConsoleTheme.SelectionStyle == MenuSelectionStyle.Fill;
            if (fill)
            {
                if (ConsoleTheme.SelectionUsesCustomColor)
                {
                    int contrastRgb = ConsoleTheme.SelectionForeground == ConsoleColor.White ? 0xFFFFFF : 0x000000;
                    WriteCustomColor(contrastRgb, ConsoleTheme.SelectionCustomRgb);
                }
                else
                {
                    Console.BackgroundColor = ConsoleTheme.SelectionBackground;
                    Console.ForegroundColor = ConsoleTheme.SelectionForeground;
                }
            }
            else if (selected && ConsoleTheme.SelectionUsesCustomColor)
            {
                ApplyContentColors(ConsoleTheme.MenuText);
                WriteCustomColor(ConsoleTheme.SelectionCustomRgb, null);
            }
            else
                ApplyContentColors(selected ? ConsoleTheme.SelectionBackground : ConsoleTheme.MenuText);
        }

        private static bool IsInputWaiting()
        {
            try { return Console.KeyAvailable; }
            catch { return false; }
        }

        private sealed class PlainMenuOptionRenderer : IMenuOptionRenderer
        {
            public bool Draw(
                string text,
                int index,
                int baseLeft,
                int baseTop,
                bool selected,
                bool animate,
                int animationDelay,
                int previewStart,
                ConsoleColor? previewColor,
                int previewLength,
                bool highlightOnly)
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

                    bool brackets = selected && ConsoleTheme.SelectionStyle == MenuSelectionStyle.Brackets;
                    bool arrow = selected && ConsoleTheme.SelectionStyle == MenuSelectionStyle.Arrow;
                    bool fill = selected && ConsoleTheme.SelectionStyle == MenuSelectionStyle.Fill;
                    int plainLeft = Math.Min(2, Math.Max(0, geometry.BufferWidth - 1));
                    string option = brackets ? "[" + text + "]" : arrow ? ">" + text + " " : " " + text + " ";
                    int clearWidth = Math.Max(1, Math.Min(option.Length, geometry.BufferWidth - plainLeft));

                    void ApplySelectionColors()
                    {
                        ApplyContentColors(ConsoleTheme.MenuText);
                        if (fill)
                        {
                            if (ConsoleTheme.SelectionUsesCustomColor)
                            {
                                int contrastRgb = ConsoleTheme.SelectionForeground == ConsoleColor.White ? 0xFFFFFF : 0x000000;
                                WriteCustomColor(contrastRgb, ConsoleTheme.SelectionCustomRgb);
                            }
                            else
                            {
                                Console.BackgroundColor = ConsoleTheme.SelectionBackground;
                                Console.ForegroundColor = ConsoleTheme.SelectionForeground;
                            }
                        }
                        else if (selected)
                        {
                            if (ConsoleTheme.SelectionUsesCustomColor)
                                WriteCustomColor(ConsoleTheme.SelectionCustomRgb, null);
                            else
                                ApplyContentColors(ConsoleTheme.SelectionBackground);
                        }
                        else
                        {
                            ApplyContentColors(ConsoleTheme.MenuText);
                        }
                    }

                    ApplyContentColors(ConsoleTheme.MenuText);
                    Console.SetCursorPosition(plainLeft, row);
                    Console.Write(new string(' ', clearWidth));
                    Console.SetCursorPosition(plainLeft, row);
                    ApplySelectionColors();

                    string visibleOption = option.Substring(0, clearWidth);
                    int adjustedPreviewStart = previewStart < 0 ? -1 : previewStart + 1;
                    int suffixStart = Math.Max(0, visibleOption.Length - 1);
                    if (highlightOnly && selected && adjustedPreviewStart >= 1 && adjustedPreviewStart < suffixStart)
                    {
                        int segmentEnd = previewLength > 0
                            ? Math.Min(suffixStart, adjustedPreviewStart + previewLength)
                            : suffixStart;
                        Console.Write(visibleOption.Substring(0, 1));
                        ApplyContentColors(ConsoleTheme.MenuText);
                        Console.Write(visibleOption.Substring(1, adjustedPreviewStart - 1));
                        ApplySelectionColors();
                        Console.Write(visibleOption.Substring(adjustedPreviewStart, segmentEnd - adjustedPreviewStart));
                        ApplyContentColors(ConsoleTheme.MenuText);
                        Console.Write(visibleOption.Substring(segmentEnd, suffixStart - segmentEnd));
                        ApplySelectionColors();
                        Console.Write(visibleOption.Substring(suffixStart));
                    }
                    else if (!previewColor.HasValue || adjustedPreviewStart < 0 || adjustedPreviewStart >= suffixStart)
                    {
                        Console.Write(visibleOption);
                    }
                    else
                    {
                        int previewEnd = previewLength > 0
                            ? Math.Min(suffixStart, adjustedPreviewStart + previewLength)
                            : suffixStart;
                        Console.Write(visibleOption.Substring(0, adjustedPreviewStart));
                        Console.ResetColor();
                        ApplyPreviewColor(previewColor.Value);
                        Console.Write(visibleOption.Substring(adjustedPreviewStart, previewEnd - adjustedPreviewStart));
                        ApplySelectionColors();
                        Console.Write(visibleOption.Substring(previewEnd));
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

        private static void ApplyPreviewColor(ConsoleColor color)
        {
            if (ConsoleTheme.HasContentBackground)
            {
                ApplyContentColors(color);
                return;
            }
            if (color == ConsoleColor.Black)
            {
                Console.BackgroundColor = ConsoleColor.Gray;
                Console.ForegroundColor = ConsoleColor.Black;
                return;
            }
            Console.ForegroundColor = color;
        }

        private static readonly IMenuOptionRenderer graphicalMenuRenderer = new GraphicalMenuOptionRenderer();
        private static readonly IMenuOptionRenderer plainMenuRenderer = new PlainMenuOptionRenderer();

        public static bool Enabled
        {
            get { return Volatile.Read(ref consoleGraphicsEnabled); }
            set
            {
                Volatile.Write(ref consoleGraphicsEnabled, value);
                Volatile.Write(ref graphicsTemporarilySuspended, false);
                if (!value)
                {
                    Volatile.Write(ref reservedBottomRows, 0);
                    StopBorderAnimation();
                    ClearRemoteSnakes();
                }
            }
        }

        public static void SuspendTemporarily()
        {
            Volatile.Write(ref consoleGraphicsEnabled, false);
            Volatile.Write(ref graphicsTemporarilySuspended, true);
            Volatile.Write(ref reservedBottomRows, 0);
            StopBorderAnimation();
        }

        public static void ResumeTemporarily()
        {
            Volatile.Write(ref consoleGraphicsEnabled, true);
            Volatile.Write(ref graphicsTemporarilySuspended, false);
            lock (borderAnimationLock)
                InvalidateBorderLocked();
        }

        public static bool IsTemporarilySuspended => Volatile.Read(ref graphicsTemporarilySuspended);

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

        public static char BorderSnakeGlyph
        {
            get
            {
                lock (borderAnimationLock)
                    return borderSnakeGlyph;
            }
            set
            {
                if (!IsValidSnakeGlyph(value.ToString()))
                    throw new ArgumentOutOfRangeException(nameof(value));
                lock (borderAnimationLock)
                {
                    borderSnakeGlyph = value;
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

        public static int CurrentBorderSnakeReferenceStep
        {
            get
            {
                lock (borderAnimationLock)
                {
                    int perimeterLength = 2 * drawnBorderWidth + 2 * (drawnBorderHeight - 2);
                    return ToReferenceStep(borderAnimationStep, perimeterLength);
                }
            }
        }

        public static int CurrentBorderSnakeReferenceDelayMilliseconds
        {
            get
            {
                lock (borderAnimationLock)
                {
                    int perimeterLength = 2 * drawnBorderWidth + 2 * (drawnBorderHeight - 2);
                    int localDelay = Volatile.Read(ref borderAnimationDelayMilliseconds);
                    if (perimeterLength <= 0)
                        return localDelay;
                    long referenceDelay = (long)localDelay * perimeterLength / SnakeProtocol.ReferencePerimeterLength;
                    return (int)Math.Max(20, Math.Min(1000, referenceDelay));
                }
            }
        }

        private static int ToReferenceStep(int localStep, int localPerimeterLength)
        {
            return localPerimeterLength > 0
                ? (int)((long)localStep * SnakeProtocol.ReferencePerimeterLength / localPerimeterLength)
                : localStep;
        }

        private static int ToLocalStep(int referenceStep, int localPerimeterLength)
        {
            return (int)((long)referenceStep * localPerimeterLength / SnakeProtocol.ReferencePerimeterLength);
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
            int animationDelay,
            int previewStart = -1,
            ConsoleColor? previewColor = null,
            int previewLength = 0,
            bool highlightOnly = false)
        {
            IMenuOptionRenderer renderer = Enabled ? graphicalMenuRenderer : plainMenuRenderer;
            return renderer.Draw(
                text,
                index,
                baseLeft,
                baseTop,
                selected,
                animate,
                animationDelay,
                previewStart,
                previewColor,
                previewLength,
                highlightOnly);
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
                lock (borderAnimationLock)
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
            if (ConsoleWindowState.ClassicWindow == IntPtr.Zero) return;
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
            if (ConsoleWindowState.ClassicWindow == IntPtr.Zero) return;
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

                    ApplyContentColors(color);
                    Console.SetCursorPosition(1, safeRow);
                    Console.Write(new string(' ', contentWidth));

                    int left = 1 + Math.Max(0, (contentWidth - text.Length) / 2);
                    Console.SetCursorPosition(left, safeRow);
                    ApplyContentColors(color);
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

            SetReservedBottomRows(3);
            string safeAddress = String.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address;
            IPAddress parsedAddress;
            if (IPAddress.TryParse(safeAddress, out parsedAddress) &&
                parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                safeAddress = "[" + safeAddress + "]";

            string endpoint = port > 0 ? safeAddress + ":" + port : safeAddress;
            int separatorLength = Math.Max(8, Math.Min(24, endpoint.Length + 4));
            ConsoleColor stateColor = ConsoleTheme.SystemText;
            WriteBottomStatus(Lang.Get(online ? TextId.HubOnline : TextId.HubOffline), stateColor, 2);
            WriteBottomStatus(endpoint, stateColor, 1);
            WriteBottomStatus(new string('-', separatorLength), stateColor);
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
            ConsoleColor foreground = Console.ForegroundColor;
            Console.ResetColor();
            if (ConsoleTheme.HasContentBackground)
                Console.Write("\u001b[49m");
            Console.SetCursorPosition(ContentLeft, safeRow);
            Console.Write(new string(' ', ContentWidth));
            ApplyContentColors(foreground);
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

            Console.ForegroundColor = ConsoleTheme.Corners;
            Console.SetCursorPosition(x, y);
            Console.Write('+');
            Console.SetCursorPosition(right, y);
            Console.Write('+');
            Console.SetCursorPosition(x, bottom);
            Console.Write('+');
            Console.SetCursorPosition(right, bottom);
            Console.Write('+');

            Console.ForegroundColor = ConsoleTheme.Border;
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
            Volatile.Write(ref borderAnimationRunning, 1);
            Task.Run(() => AnimateBorderAsync(version));
        }

        private static void StopBorderAnimation()
        {
            Interlocked.Increment(ref borderAnimationVersion);
        }

        internal static void EnsureBorderAnimationRunning()
        {
            if (Enabled && borderIsDrawn && Volatile.Read(ref borderAnimationRunning) == 0)
                StartBorderAnimation();
        }

        private static async Task AnimateBorderAsync(int version)
        {
            try
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

                            waitMilliseconds = AdvanceAndRenderBorderTickLocked(Stopwatch.GetTimestamp());
                        }
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            }
            finally
            {
                if (Volatile.Read(ref borderAnimationVersion) == version)
                    Volatile.Write(ref borderAnimationRunning, 0);
            }
        }

        private static int AdvanceAndRenderBorderTickLocked(long now)
        {
            ConsoleGeometry geometry;
            if (!TryCaptureConsoleGeometry(out geometry))
                return AnimationClockIntervalMilliseconds;

            int width = geometry.DrawableWidth;
            int height = geometry.DrawableHeight;
            if (width < 4 || height < 4 || !borderIsDrawn ||
                drawnBorderWidth != width || drawnBorderHeight != height)
                return AnimationClockIntervalMilliseconds;

            int perimeterLength = 2 * width + 2 * (height - 2);
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

            return GetNextAnimationDelayLocked(now);
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
            // Step must stay proportional to real elapsed time - Client.TryGetSnakeProfile on the
            // hub extrapolates other participants' step with this same formula.
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
            bool paused = false,
            char glyph = '-')
        {
            if (!NetWorker.IsNicknameValid(participant) || !IsVisibleSnakeColor(color) ||
                !IsValidSnakeGlyph(glyph.ToString()))
                return;

            lock (borderAnimationLock)
            {
                if (!Enabled && !IsTemporarilySuspended)
                    return;

                if (remoteSnakes.Count >= ServerInterface.MaxConnectedClients && !remoteSnakes.ContainsKey(participant))
                    return;

                int localPerimeterLength = 2 * drawnBorderWidth + 2 * (drawnBorderHeight - 2);
                int clampedDelay = Math.Max(20, Math.Min(1000, delayMilliseconds));
                int localStep;
                int localDelay;
                if (localPerimeterLength > 0)
                {
                    localStep = ToLocalStep(step, localPerimeterLength);
                    localDelay = Math.Max(1, (int)((long)clampedDelay * SnakeProtocol.ReferencePerimeterLength / localPerimeterLength));
                }
                else
                {
                    localStep = step;
                    localDelay = clampedDelay;
                }
                remoteSnakes[participant] = new SnakeState
                {
                    Color = color,
                    Glyph = glyph,
                    DelayMilliseconds = localDelay,
                    Step = localStep,
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

        public static bool IsValidSnakeGlyph(string value)
        {
            return !String.IsNullOrEmpty(value) && value.Length == 1 &&
                   "-~_=.:*+#".IndexOf(value[0]) >= 0;
        }

        public static void InvalidateVisualTheme()
        {
            lock (borderAnimationLock)
            {
                InvalidateBorderLocked();
                Interlocked.Increment(ref visualThemeRevision);
            }
        }

        internal static int VisualThemeRevision => Volatile.Read(ref visualThemeRevision);

        private static void ResetBorderAttributeCacheLocked(int width, int height)
        {
            forceFullBorderResync = true;
            int perimeterLength = 2 * width + 2 * (height - 2);
            baseBorderAttributes = new ushort[perimeterLength];
            desiredBorderAttributes = new ushort[perimeterLength];
            renderedBorderAttributes = new ushort[perimeterLength];
            baseBorderCharacters = new char[perimeterLength];
            desiredBorderCharacters = new char[perimeterLength];
            renderedBorderCharacters = new char[perimeterLength];

            for (int index = 0; index < perimeterLength; index++)
            {
                BorderCell cell = GetBorderCell(index, width, height);
                ushort attribute = GetBaseBorderAttribute(cell, width, height);
                baseBorderAttributes[index] = attribute;
                desiredBorderAttributes[index] = attribute;
                renderedBorderAttributes[index] = attribute;
                char character = GetBaseBorderCharacter(cell, width, height);
                baseBorderCharacters[index] = character;
                desiredBorderCharacters[index] = character;
                renderedBorderCharacters[index] = character;
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

        private static void SetBorderBaseCharactersLocked(
            int left,
            int top,
            string text,
            int width,
            int height)
        {
            if (baseBorderCharacters == null || text == null)
                return;
            for (int index = 0; index < baseBorderCharacters.Length; index++)
            {
                BorderCell cell = GetBorderCell(index, width, height);
                int source = cell.X - left;
                if (cell.Y != top || source < 0 || source >= text.Length)
                    continue;
                baseBorderCharacters[index] = text[source];
                desiredBorderCharacters[index] = text[source];
                renderedBorderCharacters[index] = text[source];
            }
        }

        private static void RestoreBorderSignatureLocked(int width, int height)
        {
            if (signatureLength <= 0)
                return;

            bool positionIsValid = signatureTop == height - 1 &&
                                   signatureLeft >= 1 &&
                                   signatureLeft + signatureLength < width;
            if (positionIsValid)
            {
                Console.SetCursorPosition(signatureLeft, signatureTop);
                Console.ForegroundColor = ConsoleTheme.Border;
                Console.Write(new string('-', signatureLength));
                Console.ResetColor();
                SetBorderBaseAttributeRangeLocked(
                    signatureLeft,
                    signatureTop,
                    signatureLength,
                    (ushort)ConsoleTheme.Border,
                    width,
                    height);
                SetBorderBaseCharactersLocked(
                    signatureLeft,
                    signatureTop,
                    new string('-', signatureLength),
                    width,
                    height);
            }

            signatureLeft = 0;
            signatureTop = 0;
            signatureLength = 0;
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
                baseBorderCharacters == null || desiredBorderCharacters == null || renderedBorderCharacters == null ||
                baseBorderAttributes.Length != perimeterLength ||
                desiredBorderAttributes.Length != perimeterLength || renderedBorderAttributes.Length != perimeterLength)
            {
                ResetBorderAttributeCacheLocked(width, height);
            }

            Array.Copy(baseBorderAttributes, desiredBorderAttributes, perimeterLength);
            Array.Copy(baseBorderCharacters, desiredBorderCharacters, perimeterLength);

            foreach (SnakeState snake in remoteSnakes.Values)
                OverlaySnakeLocked(snake.Step, snake.Color, snake.Glyph, perimeterLength);

            OverlaySnakeLocked(borderAnimationStep, borderSnakeColor, borderSnakeGlyph, perimeterLength);

            bool resyncAll = forceFullBorderResync;
            bool terminalTheme = ConsoleTheme.HasContentBackground;
            var frame = terminalTheme ? new System.Text.StringBuilder("\u001b7") : null;
            for (int index = 0; index < perimeterLength; index++)
            {
                ushort desired = desiredBorderAttributes[index];
                char desiredCharacter = desiredBorderCharacters[index];
                if (!resyncAll &&
                    renderedBorderAttributes[index] == desired &&
                    renderedBorderCharacters[index] == desiredCharacter)
                    continue;

                BorderCell cell = GetBorderCell(index, width, height);
                if (terminalTheme)
                {
                    frame.Append("\u001b[").Append(cell.Y + 1).Append(';').Append(cell.X + 1).Append('H')
                        .Append("\u001b[").Append(ConsoleTheme.ForegroundSgr(ConsoleTheme.ContentColor(
                            (ConsoleColor)(desired & 15), ConsoleTheme.BackgroundCustomRgb)))
                        .Append(";49m").Append(desiredCharacter);
                    continue;
                }
                if (!SetBorderCell(cell, desiredCharacter, desired))
                    continue;
                renderedBorderAttributes[index] = desired;
                renderedBorderCharacters[index] = desiredCharacter;
            }
            if (terminalTheme && frame.Length > 2)
            {
                frame.Append("\u001b8");
                Console.Write(frame.ToString());
                Array.Copy(desiredBorderAttributes, renderedBorderAttributes, perimeterLength);
                Array.Copy(desiredBorderCharacters, renderedBorderCharacters, perimeterLength);
            }
            forceFullBorderResync = false;
        }

        private static void TryRenderSnakeLayerLocked()
        {
            try
            {
                RenderSnakeLayerLocked();
            }
            catch (Exception)
            {
                baseBorderAttributes = null;
                desiredBorderAttributes = null;
                renderedBorderAttributes = null;
                baseBorderCharacters = null;
                desiredBorderCharacters = null;
                renderedBorderCharacters = null;
                forceFullBorderResync = true;
            }
        }

        private static void OverlaySnakeLocked(int step, ConsoleColor color, char glyph, int perimeterLength)
        {
            int normalizedStep = NormalizeBorderStep(step, perimeterLength);
            int visibleLength = Math.Min(BorderSnakeLength, perimeterLength);
            for (int offset = 0; offset < visibleLength; offset++)
            {
                int index = NormalizeBorderStep(normalizedStep - offset, perimeterLength);
                desiredBorderAttributes[index] = (ushort)color;
                desiredBorderCharacters[index] = glyph;
            }
        }

        private static char GetBaseBorderCharacter(BorderCell cell, int width, int height)
        {
            bool corner = (cell.X == 0 || cell.X == width - 1) &&
                          (cell.Y == 0 || cell.Y == height - 1);
            if (corner)
                return '+';
            return cell.Y == 0 || cell.Y == height - 1 ? '-' : '|';
        }

        private static ushort GetBaseBorderAttribute(BorderCell cell, int width, int height)
        {
            bool isCorner = (cell.X == 0 || cell.X == width - 1) &&
                            (cell.Y == 0 || cell.Y == height - 1);
            return (ushort)(isCorner ? ConsoleTheme.Corners : ConsoleTheme.Border);
        }

        private static bool SetBorderCell(BorderCell cell, char character, ushort attributes)
        {
            borderCellBuffer[0] = new ConsoleCell { Character = character, Attributes = attributes };
            var region = new SmallRectangle
            {
                Left = (short)cell.X, Right = (short)cell.X,
                Top = (short)cell.Y, Bottom = (short)cell.Y
            };
            return WriteConsoleOutput(consoleOutputHandle, borderCellBuffer,
                new ConsoleCoordinate(1, 1), new ConsoleCoordinate(0, 0), ref region) &&
                region.Left == cell.X && region.Right == cell.X && region.Top == cell.Y && region.Bottom == cell.Y;
        }

        internal static void WriteContentRow(int left, int top, char[] characters, int length, ConsoleColor foreground, ConsoleColor background)
        {
            if (consoleOutputHandle == IntPtr.Zero || consoleOutputHandle == invalidHandleValue ||
                length <= 0 || Console.IsOutputRedirected)
                return;

            if (ConsoleTheme.HasContentBackground)
            {
                lock (borderAnimationLock)
                {
                    Console.Write("\u001b7");
                    try
                    {
                        ApplyContentColors(foreground);
                        Console.SetCursorPosition(left, top);
                        Console.Write(characters, 0, length);
                    }
                    finally { Console.Write("\u001b8"); }
                }
                return;
            }

            if (rowAttributeBuffer.Length < length)
                rowAttributeBuffer = new ushort[length];
            ushort attribute = (ushort)((int)foreground | ((int)background << 4));
            for (int index = 0; index < length; index++)
                rowAttributeBuffer[index] = attribute;

            ConsoleCoordinate coordinate = new ConsoleCoordinate(left, top);
            uint written;
            WriteConsoleOutputCharacter(consoleOutputHandle, characters, (uint)length, coordinate, out written);
            WriteConsoleOutputAttribute(consoleOutputHandle, rowAttributeBuffer, (uint)length, coordinate, out written);
        }

        private static void ClearGraphicsInterior(int width, int height)
        {
            string emptyRow = new string(' ', Math.Max(0, width - 2));
            bool customBackground = ConsoleTheme.HasContentBackground;
            if (customBackground)
            {
                Console.ResetColor();
                Console.Write("\u001b[49m");
            }
            for (int row = 1; row < height - 1; row++)
            {
                Console.SetCursorPosition(1, row);
                Console.Write(emptyRow);
            }
            if (customBackground)
                Console.ResetColor();
        }

        private static void InvalidateBorderLocked()
        {
            borderIsDrawn = false;
            drawnBorderWidth = 0;
            drawnBorderHeight = 0;
            baseBorderAttributes = null;
            desiredBorderAttributes = null;
            renderedBorderAttributes = null;
            baseBorderCharacters = null;
            desiredBorderCharacters = null;
            renderedBorderCharacters = null;
            signatureLeft = 0;
            signatureTop = 0;
            signatureLength = 0;
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

                            RestoreBorderSignatureLocked(width, height);
                            string signature = String.IsNullOrEmpty(NetWorker.nickname)
                                ? "By alextmsv"
                                : NetWorker.nickname;
                            if (width >= signature.Length + 3 && height >= 4)
                            {
                                int newSignatureLeft = Math.Max(1, width - signature.Length - 2);
                                int newSignatureTop = height - 1;
                                Console.SetCursorPosition(newSignatureLeft, newSignatureTop);
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.Write(signature);
                                Console.ResetColor();
                                SetBorderBaseAttributeRangeLocked(
                                    newSignatureLeft,
                                    newSignatureTop,
                                    signature.Length,
                                    (ushort)ConsoleColor.DarkGray,
                                    width,
                                    height);
                                SetBorderBaseCharactersLocked(
                                    newSignatureLeft,
                                    newSignatureTop,
                                    signature,
                                    width,
                                    height);
                                signatureLeft = newSignatureLeft;
                                signatureTop = newSignatureTop;
                                signatureLength = signature.Length;
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
