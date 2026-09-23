using System;

namespace TCPTunnel
{
    internal static class MenuPresentation
    {
        private static readonly string[] logo = {
            @"  ______ _____  ____  ______                            __",
            @" /_  __// ___/ / __ \/_  __/__  __ ____   ____   ___   / /",
            @"  / /  / /    / /_/ / / /  / / / // __ \ / __ \ / _ \ / /",
            @" / /  / /___ / ____/ / /  / /_/ // / / // / / //  __// /",
            @"/_/   \____//_/     /_/   \____//_/ /_//_/ /_/ \___//_/"
        };

        internal static (string[] Header, int Top, int Spacing) Layout(int width, int height, int optionCount)
        {
            int contentHeight = Math.Max(0, height - 2);
            string subtitle = "// " + Lang.Get(TextId.MenuTagline);
            int logoWidth = 0;
            foreach (string line in logo) logoWidth = Math.Max(logoWidth, line.Length);
            string[] header;
            if (width - 2 >= logoWidth && contentHeight >= logo.Length + 3 + optionCount)
            {
                header = new string[logo.Length + 1];
                Array.Copy(logo, header, logo.Length);
                header[logo.Length] = subtitle;
            }
            else if (contentHeight >= optionCount + 4)
                header = new[] { "TCPTunnel", subtitle };
            else if (contentHeight >= optionCount + 2)
                header = new[] { "TCPTunnel" };
            else
                header = Array.Empty<string>();

            int top = header.Length == 0 ? 1 : header.Length + 2;
            int spacing = height - 2 - top >= (optionCount - 1) * 2 + 1 ? 2 : 1;
            return (header, top, spacing);
        }

        internal static bool DrawHeader(ConsoleGraphic.ConsoleGeometry geometry, string[] header)
        {
            try
            {
                int width = Math.Max(0, geometry.DrawableWidth - 2);
                int logoWidth = 0;
                foreach (string line in logo) logoWidth = Math.Max(logoWidth, line.Length);
                for (int index = 0; index < header.Length; index++)
                {
                    string text = header[index];
                    text = text.Substring(0, Math.Min(text.Length, width));
                    int blockWidth = header.Length == logo.Length + 1 && index < logo.Length ? logoWidth : text.Length;
                    Console.SetCursorPosition(1 + Math.Max(0, (width - blockWidth) / 2), index + 1);
                    Console.ForegroundColor = ConsoleTheme.MenuText;
                    Console.Write(text);
                }
                Console.ResetColor();
                return ConsoleGraphic.TryCaptureConsoleGeometry(out var after) && geometry.IsSameAs(after);
            }
            catch (ArgumentOutOfRangeException) { return false; }
            catch (System.IO.IOException) { return false; }
        }
    }
}
