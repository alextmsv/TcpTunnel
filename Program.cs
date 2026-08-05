using System;
using System.Collections.Generic;
using System.Threading;
namespace TCPTunnel
{
    internal class Program
    {
        public static readonly Menu menu = new Menu();

        static void Main(string[] args)
        {
            EmbeddedAssemblyResolver.Register();
            Lang.ApplyArguments(args);

            if (Array.Exists(args, argument => String.Equals(argument, "-self-test", StringComparison.OrdinalIgnoreCase)))
            {
                bool success = EmbeddedAssemblyResolver.VerifyEmbeddedOpenNat() &&
                               SnakeProtocol.RunSelfTest() &&
                               Lang.RunSelfTest() &&
                               SystemMessageProtocol.RunSelfTest() &&
                               ConsoleTitleAnimator.RunSelfTest();
                Console.WriteLine(Lang.Get(success ? TextId.SelfTestOk : TextId.SelfTestFailed));
                return;
            }

            Run(args);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Run(string[] args)
        {
            AppDomain.CurrentDomain.ProcessExit += delegate
            {
                ConsoleTitleAnimator.Stop();
                ServerInterface.StopServer();
            };
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                ServerInterface.StopServer();
                Environment.Exit(0);
            };

            menu.main(new List<string>(args));
        }

        public static void bye()
        {
            ConsoleGraphic.WriteContentLine(String.Empty);
            Program.matrix(Lang.Get(TextId.Goodbye));
            Console.ReadKey();
            ServerInterface.StopServer();
            Environment.Exit(0);
        }
        public static void matrix(string text, int sleep = 20, ConsoleColor color = ConsoleColor.White, bool shift = true)
        {
            if (String.IsNullOrEmpty(text))
                return;

            int left = shift ? Console.CursorLeft + 1 : Console.CursorLeft;
            if (left >= Console.BufferWidth)
                left = ConsoleGraphic.Enabled ? 1 : 0;
            Console.SetCursorPosition(left, Console.CursorTop);

            Console.ForegroundColor = color;
            foreach (char symbol in text)
            {
                Thread.Sleep(sleep / 2);

                if (symbol == '\r')
                    continue;

                if (symbol == '\n')
                {
                    Console.WriteLine();
                    if (ConsoleGraphic.Enabled && Console.CursorLeft == 0)
                        Console.SetCursorPosition(1, Console.CursorTop);
                    continue;
                }

                if (ConsoleGraphic.Enabled && shift && Console.CursorLeft >= Console.WindowWidth - 1)
                    Console.SetCursorPosition(1, Console.CursorTop + 1);

                Console.Write(symbol);
                Thread.Sleep(sleep / 2);
            }
            Console.ResetColor();
        }
        public static void bufferClear()
        {
            int currentTop = Console.CursorTop;
            int left = ConsoleGraphic.Enabled ? 1 : 0;
            int minimumTop = ConsoleGraphic.Enabled ? 1 : 0;
            int targetTop = Math.Max(minimumTop, currentTop - 1);
            int width = ConsoleGraphic.Enabled
                ? Math.Max(0, Console.WindowWidth - 2)
                : Console.BufferWidth;

            Console.SetCursorPosition(left, targetTop);
            Console.Write(new string(' ', width));
            Console.SetCursorPosition(left, targetTop);
        }
    }
}
