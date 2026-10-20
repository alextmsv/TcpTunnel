using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using TCPTunnel.Cryptography;

namespace TCPTunnel;

internal enum ConnectionSecurityMode { RequireE2E, AllowPlaintext }
internal sealed record PlaintextConsent(string[] Endpoints, KeyValuePair<string, EndpointTrustStore.Pin>[] Pins);
internal sealed class PlaintextConsentRequiredException(PlaintextConsent proposal) : IOException(Lang.Get(TextId.ConnectionPinnedPlaintextError))
{
    internal PlaintextConsent Proposal { get; } = proposal;
}

internal static class ConnectionSecurityPolicy
{
    internal static ConnectionSecurityMode CommandLineDefault => DefaultForArguments(Environment.GetCommandLineArgs());
    internal static ConnectionSecurityMode DefaultForArguments(string[] arguments) => Array.Exists(arguments, argument => argument == "--allow-unencrypted") ? ConnectionSecurityMode.AllowPlaintext : ConnectionSecurityMode.RequireE2E;
    internal static void ValidatePlaintext(bool invitationExpected, bool privateLocal, ConnectionSecurityMode mode, params string[] endpoints)
        => ValidatePlaintextWithConsent(invitationExpected, privateLocal, mode, null, endpoints);
    internal static void ValidatePlaintextWithConsent(bool invitationExpected, bool privateLocal, ConnectionSecurityMode mode, PlaintextConsent consent, params string[] endpoints)
    {
        if (invitationExpected) throw new IOException(Lang.Get(TextId.ConnectionE2ERequiredError));
        if (privateLocal) return;
        var pins = new EndpointTrustStore(Path.Combine(ApplicationSettings.DataDirectory, "trusted-endpoints"));
        if (mode != ConnectionSecurityMode.AllowPlaintext) throw new IOException(Lang.Get(TextId.ConnectionE2ERequiredError));
        var names = endpoints.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var bindings = names.Select(name => new KeyValuePair<string, EndpointTrustStore.Pin>(name, pins.ReadPin(name))).Where(pair => pair.Value != null).ToArray();
        if (consent != null)
        {
            if (!names.SequenceEqual(consent.Endpoints, StringComparer.OrdinalIgnoreCase) || !bindings.SequenceEqual(consent.Pins))
                throw new IOException(Lang.Get(TextId.ConnectionConsentChanged));
        }
        else if (bindings.Length > 0) throw new PlaintextConsentRequiredException(new PlaintextConsent(names, bindings));
    }

    internal static string TcpEndpoint(string host, int port)
    {
        host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (System.Net.IPAddress.TryParse(host, out var address)) host = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        return "tcp:" + host + ":" + port;
    }
}
