using System;

namespace TCPTunnel
{
    internal static class ConsoleTheme
    {
        public static ConsoleColor Border { get; set; } = ConsoleColor.Magenta;
        public static ConsoleColor Corners { get; set; } = ConsoleColor.Blue;
        public static ConsoleColor SelectionBackground { get; set; } = ConsoleColor.Cyan;
        public static ConsoleColor SelectionForeground { get; set; } = ConsoleColor.Black;
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
            Border = ConsoleColor.Magenta;
            Corners = ConsoleColor.Blue;
            SelectionBackground = ConsoleColor.Cyan;
            SelectionForeground = ConsoleColor.Black;
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
