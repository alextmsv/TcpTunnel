using System;

namespace TCPTunnel
{
    internal static class BluetoothPolicy
    {
        internal const string HelperArgument = "-bt-allow-advertising";
        internal static int RunElevatedHelper() => 1;
        internal static bool RequestAllowAdvertising(out string error)
        {
            error = Lang.Current == AppLanguage.Russian
                ? "Реклама Bluetooth запрещена политикой Windows. TCPTunnel не изменяет политику администратора. Проверь настройку AllowAdvertising с администратором компьютера."
                : "Bluetooth advertising is disabled by Windows policy. TCPTunnel does not modify administrator policy. Ask the computer administrator to check AllowAdvertising.";
            return false;
        }
    }
}