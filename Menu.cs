using System;
using System.Threading;

namespace TCPTunnel
{
    public class Menu
    {
        
        int top = Console.CursorTop;
        int left = Console.CursorLeft;
        int centerX = 24;
        int centerY = 7;
        public void mainMatrix(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                Console.Write(text[i]);
                Thread.Sleep(20);
            }
            Console.Clear();
            for (int j = text.Length - 1; j <= text.Length - 1; j--)
            {
                if (j < 0 || j - 1 < 0) break;
                text = text.TrimEnd(text[j]);
                Console.SetCursorPosition(centerX, centerY);
                Console.WriteLine(text);
                Thread.Sleep(20);
                Program.bufferClear();
            }
        }
        
        public void main() {
            Console.WindowWidth = 71;
            Console.WindowHeight = 16;
            Console.SetCursorPosition(centerX, centerY);
            mainMatrix("Добро пожаловать в чат");
            Console.Title = "Меню";
            Console.Clear();
            string[] choice = { "Создать сервер", "Войти на сервер", "Выход" };
            for(int i = 0; i<choice.Length; i++)
            {
                Program.matrix(choice[i]);
                Console.Write("\r\n");
            }
            int arrow=0;
            bool isMenu = true;
            while (isMenu)
            {
                Console.SetCursorPosition(top, left);
                for (int i = 0; i < choice.Length; i++)
                {
                    if (i == arrow) {
                        Console.BackgroundColor = Console.ForegroundColor;
                        Console.ForegroundColor = ConsoleColor.Black;
                    }
                    Console.WriteLine(choice[i]);
                    Console.ResetColor();
                }
                Console.WriteLine();
                switch (Console.ReadKey(true).Key.ToString())
                {
                    case "DownArrow":
                        if (arrow < choice.Length) arrow++;
                        continue;

                    case "UpArrow":
                        if (arrow > 0) arrow--;
                        continue;

                    case "Escape":
                    default:
                        Program.bufferClear();
                        Console.Title = choice[Math.Abs(arrow)];
                        break;
                }
                if (arrow == 0)
                {
                    Program.matrix("Введите порт сервера: ");
                    int port;
                    if (!Int32.TryParse(Console.ReadLine(), out port))
                        port = 9095;
                    Program.matrix($"Порт = {port}");
                    Thread.Sleep(20);
                    Console.SetCursorPosition(left + 1, top);
                    Console.Write(port);
                    Console.Clear();
                    NetWorker.doCreateServer(port);
                } else if(arrow == 1)
                {
                    Program.matrix("Введите IP адрес сервера [localhost]: ");
                    string ip = Console.ReadLine();
                    if (String.IsNullOrEmpty(ip))
                        ip = "localhost";

                    Program.matrix("Введите порт сервера [9095]: ");
                    int serverPort = 0;
                    if (!Int32.TryParse(Console.ReadLine(), out serverPort))
                        serverPort = 9095;

                    Program.matrix(">>> Попытка подключения к серверу: " + ip + ":" + serverPort);
                    Console.Clear();
                    NetWorker.DoConnect(ip, serverPort);
                    isMenu = false;
                }
                else
                {
                    Console.WriteLine("\r\n");
                    Program.matrix("До свидания");
                    Console.ReadKey();
                }
            }
            Console.WriteLine("\r\n");
            Program.matrix("До свидания");
            Console.ReadKey();
        } 
    }
}
