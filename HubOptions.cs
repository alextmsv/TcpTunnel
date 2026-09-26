using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TCPTunnel
{
    internal enum HubIpMode { Public = 0, LanOnly = 1, Off = 2 }

    internal sealed record HubOptions(HubIpMode IpMode, bool Bluetooth)
    {
        internal static HubOptions Default { get; } = new HubOptions(HubIpMode.Public, false);

        internal bool IsValid => Enum.IsDefined(IpMode) && (IpMode != HubIpMode.Off || Bluetooth);
        internal bool UsesTcp => IpMode != HubIpMode.Off;
        internal bool UsesPortMapping => IpMode == HubIpMode.Public;

        internal BluetoothHubMode BeaconMode => IpMode switch
        {
            HubIpMode.Public => BluetoothHubMode.PublicAndBluetooth,
            HubIpMode.LanOnly => BluetoothHubMode.LanAndBluetooth,
            _ => BluetoothHubMode.BluetoothOnly
        };
    }

    internal sealed class HubOptionContext
    {
        internal HubOptions Options { get; set; } = HubOptions.Default;
        internal BluetoothAvailability Bluetooth { get; init; }
        internal bool BluetoothAvailable => Bluetooth == BluetoothAvailability.Available;
    }

    internal readonly record struct HubOptionView(string Text, int HighlightStart, int HighlightLength, bool SelectHighlightOnly);

    internal sealed class HubOptionDescriptor
    {
        internal string Id { get; init; }
        internal Func<HubOptionContext, HubOptionView> Render { get; init; }
        internal Func<HubOptionContext, int, HubOptions> Change { get; init; }
    }

    internal static class HubOptionRegistry
    {
        private static readonly HubIpMode[] IpCycle = { HubIpMode.Public, HubIpMode.LanOnly, HubIpMode.Off };

        internal static IReadOnlyList<HubOptionDescriptor> All { get; } = new[]
        {
            new HubOptionDescriptor { Id = "mode", Render = RenderMode, Change = ChangeMode },
            new HubOptionDescriptor { Id = "bluetooth", Render = RenderBluetooth, Change = ChangeBluetooth }
        };

        private static HubOptionView RenderMode(HubOptionContext context)
        {
            string left = Lang.Get(TextId.HubModePublicSide);
            string right = Lang.Get(TextId.HubModeLanSide);
            string indicator = context.Options.IpMode switch
            {
                HubIpMode.LanOnly => "[ -> ]",
                HubIpMode.Off => "[ BT ]",
                _ => "[ <- ]"
            };
            string text = left + " " + indicator + " " + right;
            return context.Options.IpMode switch
            {
                HubIpMode.LanOnly => new HubOptionView(text, left.Length + indicator.Length + 2, right.Length, true),
                HubIpMode.Off => new HubOptionView(text, left.Length + 1, indicator.Length, true),
                _ => new HubOptionView(text, 0, left.Length, true)
            };
        }

        private static HubOptions ChangeMode(HubOptionContext context, int direction)
        {
            HubIpMode[] allowed = context.BluetoothAvailable ? IpCycle : IpCycle.Where(mode => mode != HubIpMode.Off).ToArray();
            HubIpMode next = OptionNavigation.Next(allowed, context.Options.IpMode, direction == 0 ? 1 : direction);
            bool bluetooth = next == HubIpMode.Off || (context.Options.IpMode != HubIpMode.Off && context.Options.Bluetooth);
            return new HubOptions(next, bluetooth && context.BluetoothAvailable);
        }

        private static HubOptionView RenderBluetooth(HubOptionContext context)
        {
            if (!context.BluetoothAvailable)
                return new HubOptionView(Lang.Get(TextId.HubBluetoothUnavailableRow), -1, 0, false);
            string mark = context.Options.Bluetooth ? "[ V ]" : "[   ]";
            string text = Lang.Get(TextId.HubBluetoothRow) + " " + mark;
            return new HubOptionView(text, context.Options.Bluetooth ? text.Length - mark.Length : -1, mark.Length, false);
        }

        private static HubOptions ChangeBluetooth(HubOptionContext context, int direction)
        {
            if (!context.BluetoothAvailable || context.Options.IpMode == HubIpMode.Off)
                return context.Options;
            return context.Options with { Bluetooth = !context.Options.Bluetooth };
        }

        internal static string Explanation(HubOptions options)
        {
            TextId main = options.IpMode switch
            {
                HubIpMode.LanOnly => TextId.HubExplainLan,
                HubIpMode.Off => TextId.HubExplainBluetooth,
                _ => TextId.HubExplainPublic
            };
            string text = Lang.Get(main);
            if (options.Bluetooth && options.IpMode != HubIpMode.Off)
                text += "\n\n" + Lang.Get(TextId.HubExplainBluetooth);
            return text;
        }

        internal static bool RunSelfTest()
        {
            var withBt = new HubOptionContext { Bluetooth = BluetoothAvailability.Available };
            var withoutBt = new HubOptionContext { Bluetooth = BluetoothAvailability.NoAdapter };
            HubOptionDescriptor mode = All.First(option => option.Id == "mode");
            HubOptionDescriptor bluetooth = All.First(option => option.Id == "bluetooth");

            withBt.Options = mode.Change(withBt, 1);
            bool lan = withBt.Options == new HubOptions(HubIpMode.LanOnly, false);
            withBt.Options = mode.Change(withBt, 1);
            bool btOnly = withBt.Options == new HubOptions(HubIpMode.Off, true) && withBt.Options.IsValid;
            HubOptions locked = bluetooth.Change(withBt, 1);
            bool cannotDisableOnlyTransport = locked == withBt.Options;
            withBt.Options = mode.Change(withBt, 1);
            bool wrapped = withBt.Options.IpMode == HubIpMode.Public;
            withBt.Options = bluetooth.Change(withBt, 1);
            bool combined = withBt.Options == new HubOptions(HubIpMode.Public, true) &&
                            withBt.Options.BeaconMode == BluetoothHubMode.PublicAndBluetooth;

            withoutBt.Options = mode.Change(withoutBt, -1);
            bool skipsBluetoothOnly = withoutBt.Options.IpMode == HubIpMode.LanOnly && !withoutBt.Options.Bluetooth;
            bool invalidRejected = !new HubOptions(HubIpMode.Off, false).IsValid;

            HubOptionView view = mode.Render(new HubOptionContext { Options = new HubOptions(HubIpMode.LanOnly, false) });
            bool highlight = view.Text.Substring(view.HighlightStart, view.HighlightLength) == Lang.Get(TextId.HubModeLanSide) &&
                             view.SelectHighlightOnly;

            string bluetoothText = Lang.Get(TextId.HubExplainBluetooth);
            bool explanations = Explanation(new HubOptions(HubIpMode.Public, true)).EndsWith(bluetoothText) &&
                                Explanation(new HubOptions(HubIpMode.Public, true)).StartsWith(Lang.Get(TextId.HubExplainPublic)) &&
                                !Explanation(new HubOptions(HubIpMode.LanOnly, false)).Contains(bluetoothText) &&
                                Explanation(new HubOptions(HubIpMode.Off, true)) == bluetoothText;

            return lan && btOnly && cannotDisableOnlyTransport && wrapped && combined &&
                   skipsBluetoothOnly && invalidRejected && highlight && explanations && LanPolicy.RunSelfTest();
        }
    }

    internal static class LanPolicy
    {
        internal static bool IsAllowed(IPAddress remote)
        {
            if (remote == null)
                return false;
            if (remote.IsIPv4MappedToIPv6)
                remote = remote.MapToIPv4();
            if (IPAddress.IsLoopback(remote))
                return true;
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
                        continue;
                    foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
                        if (IsInSubnet(remote, address.Address, address.PrefixLength))
                            return true;
                }
            }
            catch (NetworkInformationException) { }
            return false;
        }

        internal static bool IsInSubnet(IPAddress remote, IPAddress local, int prefixLength)
        {
            if (local.IsIPv4MappedToIPv6)
                local = local.MapToIPv4();
            if (remote.AddressFamily != local.AddressFamily)
                return false;
            byte[] a = remote.GetAddressBytes();
            byte[] b = local.GetAddressBytes();
            int bits = a.Length * 8;
            if (prefixLength <= 0 || prefixLength > bits)
                return false;
            if (remote.AddressFamily == AddressFamily.InterNetworkV6 && remote.ScopeId != 0 && local.ScopeId != 0 && remote.ScopeId != local.ScopeId)
                return false;
            int fullBytes = prefixLength / 8;
            for (int index = 0; index < fullBytes; index++)
                if (a[index] != b[index])
                    return false;
            int remainder = prefixLength % 8;
            if (remainder == 0)
                return true;
            int mask = 0xFF << (8 - remainder) & 0xFF;
            return (a[fullBytes] & mask) == (b[fullBytes] & mask);
        }

        internal static bool RunSelfTest()
        {
            IPAddress local = IPAddress.Parse("192.168.1.10");
            return IsInSubnet(IPAddress.Parse("192.168.1.77"), local, 24) &&
                   !IsInSubnet(IPAddress.Parse("192.168.2.77"), local, 24) &&
                   IsInSubnet(IPAddress.Parse("10.0.5.1"), IPAddress.Parse("10.0.0.2"), 12) &&
                   !IsInSubnet(IPAddress.Parse("10.16.0.1"), IPAddress.Parse("10.0.0.2"), 12) &&
                   !IsInSubnet(IPAddress.Parse("8.8.8.8"), local, 0) &&
                   !IsInSubnet(IPAddress.Parse("fe80::1"), local, 24) &&
                   IsAllowed(IPAddress.Loopback) && IsAllowed(IPAddress.IPv6Loopback) &&
                   !IsAllowed(IPAddress.Parse("203.0.113.9")) && !IsAllowed(null);
        }
    }
}
