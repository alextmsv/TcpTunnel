using System;

namespace TCPTunnel
{
    internal enum MenuSelectionStyle { Fill, Arrow, Brackets }
    internal enum BackgroundColorMode { Off = 0, WindowsTerminal = 2 }

    internal static class ConsoleTheme
    {
        public static MenuSelectionStyle SelectionStyle { get; set; } = MenuSelectionStyle.Fill;
        public static ConsoleColor Border { get; set; } = ConsoleColor.Magenta;
        public static ConsoleColor Corners { get; set; } = ConsoleColor.Blue;

        public static ConsoleColor SelectionBackground { get; set; } = ConsoleColor.Cyan;

        public static bool SelectionUsesCustomColor { get; private set; }
        public static int SelectionCustomRgb { get; private set; } = 0x2E7DE0;

        public static void SetCustomSelectionColor(int rgb)
        {
            SelectionCustomRgb = rgb & 0xFFFFFF;
            SelectionUsesCustomColor = true;
        }

        public static void ClearCustomSelectionColor() => SelectionUsesCustomColor = false;

        public static ConsoleColor SelectionForeground =>
            (SelectionUsesCustomColor ? IsDarkRgb(SelectionCustomRgb) : IsDarkColor(SelectionBackground))
                ? ConsoleColor.White : ConsoleColor.Black;

        public static BackgroundColorMode BackgroundMode { get; set; } = BackgroundColorMode.Off;
        public static int BackgroundCustomRgb { get; set; } = 0x101828;

        internal static bool HasContentBackground => WindowsTerminalTheme.HasSessionBackground;

        private static readonly int[] palette = { 0x000000, 0x000080, 0x008000, 0x008080,
                0x800000, 0x800080, 0x808000, 0xC0C0C0, 0x808080,
                0x0000FF, 0x00FF00, 0x00FFFF, 0xFF0000, 0xFF00FF, 0xFFFF00, 0xFFFFFF };
        internal static int ColorRgb(ConsoleColor color) => palette[(int)color & 15];

        internal static ConsoleColor ContentColor(ConsoleColor configured, int background)
        {
            return configured;
        }

        internal static int ForegroundSgr(ConsoleColor color)
        {
            int value = (int)color;
            return (value < 8 ? 30 : 90) + ((value & 4) >> 2) + (value & 2) + ((value & 1) << 2);
        }

        internal static int ReadableForeground(int foreground, int background)
        {
            static double Luminance(int rgb)
            {
                static double Linear(int value)
                {
                    double channel = value / 255.0;
                    return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
                }
                return 0.2126 * Linear((rgb >> 16) & 255) + 0.7152 * Linear((rgb >> 8) & 255) + 0.0722 * Linear(rgb & 255);
            }
            double fg = Luminance(foreground), bg = Luminance(background);
            if ((Math.Max(fg, bg) + 0.05) / (Math.Min(fg, bg) + 0.05) >= 4.5) return foreground;
            return bg > 0.179 ? 0x000000 : 0xFFFFFF;
        }

        private static bool IsDarkColor(ConsoleColor color) => color switch
        {
            ConsoleColor.Black or ConsoleColor.DarkBlue or ConsoleColor.DarkGreen or ConsoleColor.DarkCyan or
            ConsoleColor.DarkRed or ConsoleColor.DarkMagenta or ConsoleColor.DarkYellow or ConsoleColor.DarkGray or
            ConsoleColor.Blue or ConsoleColor.Red or ConsoleColor.Magenta => true,
            _ => false
        };

        internal static bool IsDarkRgb(int rgb)
        {
            int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            return luminance < 0.5;
        }

        public static ConsoleColor MenuText { get; set; } = ConsoleColor.White;
        public static ConsoleColor IncomingMarker { get; set; } = ConsoleColor.Green;
        public static ConsoleColor IncomingNickname { get; set; } = ConsoleColor.Yellow;
        public static ConsoleColor IncomingText { get; set; } = ConsoleColor.White;
        public static ConsoleColor OutgoingMarker { get; set; } = ConsoleColor.Cyan;
        public static ConsoleColor OutgoingNickname { get; set; } = ConsoleColor.DarkCyan;
        public static ConsoleColor OutgoingText { get; set; } = ConsoleColor.White;
        public static ConsoleColor InputPrompt { get; set; } = ConsoleColor.Cyan;
        public static ConsoleColor InputText { get; set; } = ConsoleColor.White;
        public static ConsoleColor SystemText { get; set; } = ConsoleColor.DarkGray;
        public static ConsoleColor SystemSuccess { get; set; } = ConsoleColor.Green;
        public static ConsoleColor SystemWarning { get; set; } = ConsoleColor.Yellow;
        public static ConsoleColor SystemError { get; set; } = ConsoleColor.Red;

        public static void Reset()
        {
            SelectionStyle = MenuSelectionStyle.Fill;
            Border = ConsoleColor.Magenta;
            Corners = ConsoleColor.Blue;
            SelectionBackground = ConsoleColor.Cyan;
            ClearCustomSelectionColor();
            BackgroundMode = BackgroundColorMode.Off;
            BackgroundCustomRgb = 0x101828;
            MenuText = ConsoleColor.White;
            IncomingMarker = ConsoleColor.Green;
            IncomingNickname = ConsoleColor.Yellow;
            IncomingText = ConsoleColor.White;
            OutgoingMarker = ConsoleColor.Cyan;
            OutgoingNickname = ConsoleColor.DarkCyan;
            OutgoingText = ConsoleColor.White;
            InputPrompt = ConsoleColor.Cyan;
            InputText = ConsoleColor.White;
            SystemText = ConsoleColor.DarkGray;
            SystemSuccess = ConsoleColor.Green;
            SystemWarning = ConsoleColor.Yellow;
            SystemError = ConsoleColor.Red;
        }

        public static ConsoleColor ValidateColor(int value, ConsoleColor fallback, bool allowBlack = false)
        {
            if (!Enum.IsDefined(typeof(ConsoleColor), value))
                return fallback;
            ConsoleColor color = (ConsoleColor)value;
            return !allowBlack && color == ConsoleColor.Black ? fallback : color;
        }
    }
}
