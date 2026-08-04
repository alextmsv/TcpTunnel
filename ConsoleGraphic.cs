using System;

namespace TCPTunnel
{
    public class ConsoleGraphic
    {
        public static bool Enabled { get; set; } = true;

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
                // Терминал сам управляет viewport.
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

        public void Clear(int lineTime = 2, int cornerTime = 5)
        {
            AlignViewport();
            Console.ResetColor();
            Console.Clear();

            if (!Enabled || Console.WindowWidth < 4 || Console.WindowHeight < 4)
            {
                Console.SetCursorPosition(0, 0);
                return;
            }

            DrawRectangle(0, 0, Console.WindowWidth, Console.WindowHeight);

            const string signature = "By alextmsv";
            int signatureLeft = Math.Max(1, Console.WindowWidth - signature.Length - 9);
            int signatureTop = Math.Max(1, Console.WindowHeight - 4);
            Console.SetCursorPosition(signatureLeft, signatureTop);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(signature);
            Console.ResetColor();

            // Весь последующий вывод должен начинаться внутри рамки.
            Console.SetCursorPosition(1, 1);
        }
    }
}
