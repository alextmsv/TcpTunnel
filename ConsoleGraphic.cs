using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace TCPTunnel
{
    public class ConsoleGraphic
    {

        private static void DrawCorner(int x, int y, char cornerChar, int time = 20)
        {
            Console.SetCursorPosition(x, y);
            Program.matrix(cornerChar.ToString(), time, ConsoleColor.Blue, false);
        }
        private static void DrawLine(int startX, int startY, int length, char lineChar, bool isHorizontal = true, int time = 20)
        {
            for (int i = 0; i < length; i++)
            {
                Console.SetCursorPosition(startX + (isHorizontal ? i : 0), startY + (isHorizontal ? 0 : i));
                Program.matrix(lineChar.ToString(), time, ConsoleColor.Magenta, false);
            }
        }
        private static void DrawRectangle(int x, int y, int width, int height, int linetime, int cornertime)
        {
            char horizontalLine = '-';
            char verticalLine = '|';
            char corner = '+';
            DrawLine(x, y, width, horizontalLine, true, linetime);
            DrawLine(x, y + height - 1, width, horizontalLine, true, linetime);
            DrawLine(x, y, height, verticalLine, false, linetime);
            DrawLine(x + width - 1, y, height, verticalLine, false, linetime);
            DrawCorner(x, y, corner, cornertime);
            DrawCorner(x + width - 1, y, corner, cornertime);
            DrawCorner(x, y + height - 1, corner, cornertime);
            DrawCorner(x + width - 1, y + height - 1, corner, cornertime);
        }
        public void Clear(int ltime = 2, int ctime = 5)
        {
            Console.Clear();
            DrawRectangle(0, 0, Console.WindowWidth, Console.WindowHeight, ltime, ctime);
            int top = Console.CursorTop;
            int left = Console.CursorLeft;
            Console.SetCursorPosition(Console.WindowWidth-21, Console.WindowHeight-4);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("By alextmsv");
            Console.ResetColor();
            Console.SetCursorPosition(left, top);
        }
    }
}
