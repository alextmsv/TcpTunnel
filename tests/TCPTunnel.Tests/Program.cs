using System.Reflection;
using TCPTunnel;

static class Program
{
    static async Task<int> Main()
    {
        int failed = 0;
        foreach (Type type in typeof(Client).Assembly.GetTypes())
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
}
