using System;
using System.IO;
using System.Reflection;

namespace TCPTunnel
{
    internal static class EmbeddedAssemblyResolver
    {
        private const string OpenNatAssemblyName = "Open.Nat";
        private const string OpenNatResourceName = "TCPTunnel.Dependencies.Open.Nat.dll";

        private static readonly object syncRoot = new object();
        private static Assembly loadedOpenNatAssembly;

        public static void Register()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        }

        public static bool VerifyEmbeddedOpenNat()
        {
            return Assembly.Load(OpenNatAssemblyName) != null;
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs eventArgs)
        {
            AssemblyName requestedAssembly;
            try
            {
                requestedAssembly = new AssemblyName(eventArgs.Name);
            }
            catch
            {
                return null;
            }

            if (!String.Equals(requestedAssembly.Name, OpenNatAssemblyName, StringComparison.OrdinalIgnoreCase))
                return null;

            lock (syncRoot)
            {
                if (loadedOpenNatAssembly != null)
                    return loadedOpenNatAssembly;

                Assembly applicationAssembly = Assembly.GetExecutingAssembly();
                using (Stream resource = applicationAssembly.GetManifestResourceStream(OpenNatResourceName))
                {
                    if (resource == null)
                        return null;

                    byte[] assemblyBytes = new byte[resource.Length];
                    int offset = 0;
                    while (offset < assemblyBytes.Length)
                    {
                        int read = resource.Read(assemblyBytes, offset, assemblyBytes.Length - offset);
                        if (read == 0)
                            throw new EndOfStreamException("Не удалось прочитать встроенную библиотеку Open.Nat.");
                        offset += read;
                    }

                    loadedOpenNatAssembly = Assembly.Load(assemblyBytes);
                    return loadedOpenNatAssembly;
                }
            }
        }
    }
}
