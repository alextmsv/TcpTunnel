using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TCPTunnel
{
    public class ConsoleGraphic
    {

        private static void drawCorner(int x, int y, char cornerChar, int time = 20)
        {
            Console.SetCursorPosition(x, y);
            Program.matrix(cornerChar.ToString(), time, ConsoleColor.Blue);
        }

        private static void drawLine(int startX, int startY, int length, char lineChar, bool isHorizontal = true, int time = 20)
        {
            for (int i = 0; i < length; i++)
            {
                Console.SetCursorPosition(startX + (isHorizontal ? i : 0), startY + (isHorizontal ? 0 : i));
                Program.matrix(lineChar.ToString(), time, ConsoleColor.Magenta);
            }
        }

        private static void drawRectangle(int x, int y, int width, int height, int linetime, int cornertime)
        {
            char horizontalLine = '-';
            char verticalLine = '|';
            char corner = '+';
            drawLine(x, y, width, horizontalLine, true, linetime);
            drawLine(x, y + height - 1, width, horizontalLine, true, linetime);
            drawLine(x, y, height, verticalLine, false, linetime);
            drawLine(x + width - 1, y, height, verticalLine, false, linetime);
            drawCorner(x, y, corner, cornertime);
            drawCorner(x + width - 1, y, corner, cornertime);
            drawCorner(x, y + height - 1, corner, cornertime);
            drawCorner(x + width - 1, y + height - 1, corner, cornertime);

        }
        public static void Clear(int ltime = 3, int ctime = 10)
        {
            Console.Clear();
            drawRectangle(0, 0, 71, 16, ltime, ctime);
        }
    }
}
