using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TCPTunnel
{
    internal static class BluetoothPolicy
    {
        internal const string HelperArgument = "-bt-allow-advertising";
        private const string PolicyKey = @"SOFTWARE\Microsoft\PolicyManager\current\device\Bluetooth";
        private const string PolicyValue = "AllowAdvertising";
        private const int ErrorCancelled = 1223;
        private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(60);

        internal static int RunElevatedHelper()
        {
            try
            {
                using RegistryKey key = Registry.LocalMachine.CreateSubKey(PolicyKey, true);
                key.SetValue(PolicyValue, 1, RegistryValueKind.DWord);
                return 0;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        internal static bool RequestAllowAdvertising(out string error)
        {
            error = null;
            string processPath = Environment.ProcessPath;
            if (String.IsNullOrEmpty(processPath))
            {
                error = Lang.Get(TextId.BluetoothElevationFailed);
                return false;
            }

            string arguments = HelperArgument;
            if (String.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                arguments = "\"" + Environment.GetCommandLineArgs()[0] + "\" " + HelperArgument;

            var start = new ProcessStartInfo(processPath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using Process helper = Process.Start(start);
                if (helper == null || !helper.WaitForExit((int)HelperTimeout.TotalMilliseconds))
                {
                    error = Lang.Get(TextId.BluetoothElevationFailed);
                    return false;
                }
                if (helper.ExitCode != 0)
                {
                    error = Lang.Get(TextId.BluetoothElevationFailed);
                    return false;
                }
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                error = Lang.Get(TextId.BluetoothElevationDeclined);
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
