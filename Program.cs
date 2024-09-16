using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
namespace TCPTunnel
{
    internal class Program
    {
        public static string[] publicArgs { get; private set; }
        public static Menu menu = new Menu();
        static void Main(string[] args)
        {
            publicArgs = args;
            List<string> arguments = new List<string>();
            foreach (string arg in args)
            {
                arguments.Add(arg);
            }
            menu.main(arguments);
        }
        
        public static void restart(string[] args)
        {
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            string arguments = string.Join(" ", args);
            Process.Start(exePath, arguments);
            Environment.Exit(0);
        }
        public static void waiting(string message, string ip, int port)
        {
            const int maxDots = 3;
            const int delay = 700;
            Console.Write(message);
            int startPosition = Console.CursorLeft;

            while (!NetWorker.connected)
            {
                for (int i = 0; i <= maxDots; i++)
                {
                    Console.SetCursorPosition(startPosition, Console.CursorTop);
                    Console.Write(new string('.', i));
                    Thread.Sleep(delay);
                    Console.SetCursorPosition(startPosition, Console.CursorTop);
                    Console.Write(new string(' ', maxDots));
                }
                UserInterface.DoConnect(ip, port);
            }
        }
        public static string ReadLineWithPrompt(string prompt)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }
        public static void bye()
        {
            Console.WriteLine("\r\n");
            Program.matrix("До свидания");
            Console.ReadKey();
            Environment.Exit(0);
        }
        public static void matrix(string text, int sleep = 20, ConsoleColor color = ConsoleColor.White)
        {
            Console.ForegroundColor = color;
            for (int i = 0; i < text.Length; i++)
            {
                Thread.Sleep(sleep / 2);
                Console.Write(text[i]);
                Thread.Sleep(sleep / 2);
            }
            Console.ResetColor();
        }
        //public static void matrixRemove(){
            
        //}
        public static void bufferClear()
        {
            int currentTop = Console.CursorTop;
            Console.SetCursorPosition(0, Math.Abs(currentTop - 1));
            Console.Write(new string(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, Math.Abs(currentTop - 1));
        }
    }
}
