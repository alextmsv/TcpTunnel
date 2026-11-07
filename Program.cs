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
            if (args.Length == 1 && args[0] == BluetoothPolicy.HelperArgument)
            {
                Environment.ExitCode = BluetoothPolicy.RunElevatedHelper();
                return;
            }
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            ConsoleGraphic.DisableQuickEdit();
            ConsoleGraphic.EnableVirtualTerminalOutput();
            if (ImageViewer.TryRun(args))
                return;
            Lang.ApplyArguments(args);
            if (Array.Exists(args, argument => argument == "-third-party-notices"))
            {
                var assembly = typeof(Program).Assembly;
                foreach (string resource in assembly.GetManifestResourceNames())
                {
                    if (!resource.StartsWith("TCPTunnel.NativeCrypto.", StringComparison.Ordinal)) continue;
                    using var reader = new System.IO.StreamReader(assembly.GetManifestResourceStream(resource));
                    Console.WriteLine(resource); Console.WriteLine(reader.ReadToEnd());
                }
                return;
            }
            if (Array.Exists(args, argument => argument == "-e2e-test"))
            {
                bool secureSuccess = TCPTunnel.Cryptography.SecureBoundaryTests.SecureWireBoundsSelfTest().GetAwaiter().GetResult() &&
                    ImageViewer.PrivateViewerPipeSelfTest().GetAwaiter().GetResult() &&
                    TCPTunnel.Cryptography.SecureBoundaryTests.SecureProfileAndHubLifecycleSelfTest().GetAwaiter().GetResult() &&
                    TCPTunnel.Cryptography.SecureIntegrationTests.SecureTcpSelfTest().GetAwaiter().GetResult() && TCPTunnel.Cryptography.SecureIntegrationTests.SecureBluetoothTransportSelfTest().GetAwaiter().GetResult() &&
                    TCPTunnel.Cryptography.SecureIntegrationTests.SecureMixedTransportSelfTest().GetAwaiter().GetResult();
                Console.WriteLine(secureSuccess ? "TCPTunnel E2E test: OK" : "TCPTunnel E2E test: FAILED");
                Environment.ExitCode = secureSuccess ? 0 : 1; return;
            }

            if (Array.Exists(args, argument => String.Equals(argument, "-stress-test", StringComparison.OrdinalIgnoreCase)))
            {
                bool stressSuccess = StabilityTests.Run();
                Console.WriteLine(stressSuccess ? "TCPTunnel stress test: OK" : "TCPTunnel stress test: FAILED");
                Environment.ExitCode = stressSuccess ? 0 : 1;
                return;
            }

            if (Array.Exists(args, argument => String.Equals(argument, "-self-test", StringComparison.OrdinalIgnoreCase)))
            {
                bool success = SnakeProtocol.RunSelfTest() &&
                               ApplicationSettings.RunSelfTest() &&
                               LegacyEventProtocol.RunSelfTest() &&
                               Lang.RunSelfTest() &&
                               SystemMessageProtocol.RunSelfTest() &&
                               NetworkAddressResolver.RunSelfTest() &&
                               ImageProtocol.RunSelfTest() &&
                               ImageAnimationProtocol.RunSelfTest() &&
                               ImageInput.RunSelfTest() &&
                               ImageCodec.RunAnimationSelfTest() &&
                               ImageRenderer.RunSelfTest() &&
                               ImageRenderer.RunAnimationSelfTest() &&
                               ImageViewer.RunSelfTest() &&
                               ImageViewer.RunAnimationSelfTest() &&
                               ConsoleTitleAnimator.RunSelfTest() &&
                               HubBeaconCodec.RunSelfTest() &&
                               HubOptionRegistry.RunSelfTest() &&
                               UserInterface.RunCommandSelfTest();
                Console.WriteLine(Lang.Get(success ? TextId.SelfTestOk : TextId.SelfTestFailed));
                Environment.ExitCode = success ? 0 : 1;
                Console.Out.Flush();
                Thread.Sleep(TimeSpan.FromSeconds(10));
                return;
            }

            int snakeDiagnostics = Array.IndexOf(args, "-snake-diagnostics");
            ConsoleGraphic.UseBatchedBorderFrames = !Array.Exists(args, argument => argument == "-snake-native-border") && ConsoleGraphic.EnableVirtualTerminalOutput();
            if (snakeDiagnostics >= 0)
            {
                try
                {
                    if (snakeDiagnostics + 1 >= args.Length) throw new ArgumentException("Usage: -snake-diagnostics <new-log.jsonl>");
                    SnakeFrameDiagnostics.Start(args[snakeDiagnostics + 1]);
                }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
                { Console.WriteLine(error.Message); Environment.ExitCode = 1; return; }
            }
            ApplicationSettings.Initialize();
            Run(args);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Run(string[] args)
        {
            AppDomain.CurrentDomain.ProcessExit += delegate
            {
                ConsoleTitleAnimator.Stop();
                ConsoleInput.Restore();
                WindowsTerminalTheme.TryClearBackground(out _);
                ConsoleWindowState.StopTracking();
                ApplicationSettings.CaptureAndSave();
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
            ConsoleInput.ReadKey();
            ServerInterface.StopServer();
            Environment.Exit(0);
        }
        public static void matrix(string text, int sleep = 20, ConsoleColor color = ConsoleColor.White, bool shift = true)
        {
            if (String.IsNullOrEmpty(text))
                return;

            lock (ConsoleGraphic.borderAnimationLock)
            {
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
