using System.Reflection;
using TCPTunnel;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--bt-hub-smoke")
            return BluetoothHardwareSmoke.Run();
        if (args.Length == 1 && args[0] == "--e2e-test")
            return await TCPTunnel.Cryptography.SecureIntegrationTests.SecureTcpSelfTest() && await TCPTunnel.Cryptography.SecureIntegrationTests.SecureBluetoothTransportSelfTest() && await TCPTunnel.Cryptography.SecureIntegrationTests.SecureMixedTransportSelfTest() ? 0 : 1;
        if (args.Length >= 2 && args[0].StartsWith("--", StringComparison.Ordinal))
        {
            RunProbe(args[0], args[1]);
            return 0;
        }

        int failed = 0;
        Assembly[] assemblies = { typeof(Client).Assembly, Assembly.GetExecutingAssembly() };
        foreach (Type type in assemblies.SelectMany(a => a.GetTypes()).Distinct())
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name.EndsWith("SelfTest", StringComparison.Ordinal) && m.GetParameters().Length == 0))
        {
            try
            {
                object? result = method.Invoke(null, null);
                bool ok = result is Task<bool> task ? await task : result is true;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {type.Name}.{method.Name}");
                if (!ok) failed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {type.Name}.{method.Name}: {ex.InnerException?.Message ?? ex.Message}");
                failed++;
            }
        }
        return failed;
    }

    private static void RunProbe(string mode, string resultPath)
    {
        try
        {
            switch (mode)
            {
                case "--geometry-probe":
                    PseudoConsoleTests.OpenConsoleOutput();
                    File.WriteAllText(resultPath,
                        $"{Console.WindowWidth},{Console.WindowHeight},{Console.BufferWidth},{Console.BufferHeight}");
                    break;
                case "--menu-probe":
                    File.WriteAllText(resultPath, ConsoleMenuProbe.Run() ? "PASS" : "FAIL");
                    break;
                case "--resize-menu-probe":
                    File.WriteAllText(resultPath, MenuRenderProbe.RunResize(resultPath) ? "PASS" : "FAIL");
                    break;
                case "--resize-main-menu-probe":
                    File.WriteAllText(resultPath, MenuRenderProbe.RunResize(resultPath, main: true) ? "PASS" : "FAIL");
                    break;
                case "--exit-menu-probe":
                    MenuRenderProbe.RunResize(resultPath, exit: true);
                    break;
                case "--appearance-probe":
                    File.WriteAllText(resultPath, MenuRenderProbe.Run() ? "PASS" : "FAIL");
                    break;
                default:
                    File.WriteAllText(resultPath, "FAIL unknown probe: " + mode);
                    break;
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, "FAIL " + ex.Message);
        }
    }
}
