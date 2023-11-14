using System;
using System.Threading;
namespace TCPTunnel
{
    internal class Program
    {
        public static Menu menu = new Menu();
        static void Main(string[] args)
        {
            menu.main();
        }
        
            public static void matrix(string text)
        {
            
            for (int i = 0; i < text.Length; i++)
            {
                Console.Write(text[i]);
                Thread.Sleep(20);
            }
        }
        public static void bufferClear()
        {
            int currentTop = Console.CursorTop;
            Console.SetCursorPosition(0, Math.Abs(currentTop - 1));
            Console.Write(new string(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, Math.Abs(currentTop - 1));
        }
        public static void keyNavigation()
        {

        }
        public static void centerText(string text)
        {
            int width = Console.WindowWidth;
            if (text.Length < width)
            {
                text = text.PadLeft((width - text.Length) / 2 + text.Length, ' ');
            }
            Console.WriteLine(text);
        }
    }
}
