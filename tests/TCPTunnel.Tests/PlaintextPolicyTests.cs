using System.Security.Cryptography;
using TCPTunnel;
using TCPTunnel.Cryptography;

internal static class PlaintextPolicyTests
{
    public static bool PinnedPlaintextOnceSelfTest()
    {
        string? previous = Environment.GetEnvironmentVariable("TCPTUNNEL_DATA_HOME");
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-plain-once-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", directory);
        try
        {
            string endpoint = ConnectionSecurityPolicy.TcpEndpoint("localhost", 9091), actual = ConnectionSecurityPolicy.TcpEndpoint("127.0.0.1", 9091);
            var pins = new EndpointTrustStore(Path.Combine(directory, "trusted-endpoints")); pins.Save(endpoint, new string('A', 64)); pins.Save(actual, new string('A', 64));
            var before = Directory.GetFiles(Path.Combine(directory, "trusted-endpoints")).ToDictionary(path => path, File.ReadAllBytes);
            PlaintextConsent consent;
            try { ConnectionSecurityPolicy.ValidatePlaintext(false, false, ConnectionSecurityMode.AllowPlaintext, endpoint, actual); return false; }
            catch (PlaintextConsentRequiredException ex) { consent = ex.Proposal; }
            ConnectionSecurityPolicy.ValidatePlaintextWithConsent(false, false, ConnectionSecurityMode.AllowPlaintext, consent, endpoint, actual);
            if (before.Any(pair => !pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)))) return false;
            try { ConnectionSecurityPolicy.ValidatePlaintextWithConsent(true, false, ConnectionSecurityMode.AllowPlaintext, consent, endpoint, actual); return false; } catch (IOException) { }
            try { ConnectionSecurityPolicy.ValidatePlaintextWithConsent(false, false, ConnectionSecurityMode.RequireE2E, consent, endpoint, actual); return false; } catch (IOException) { }
            try { ConnectionSecurityPolicy.ValidatePlaintextWithConsent(false, false, ConnectionSecurityMode.AllowPlaintext, consent, endpoint, "tcp:127.0.0.2:9091"); return false; } catch (IOException) { }
            pins.Save(endpoint, new string('A', 64));
            try { ConnectionSecurityPolicy.ValidatePlaintextWithConsent(false, false, ConnectionSecurityMode.AllowPlaintext, consent, endpoint, actual); return false; } catch (IOException) { }
            return true;
        }
        finally { Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", previous); }
    }
    public static bool PlaintextConsentAndDowngradeSelfTest()
    {
        string? previous = Environment.GetEnvironmentVariable("TCPTUNNEL_DATA_HOME");
        string directory = Path.Combine(Path.GetTempPath(), "TCPTunnel-plaintext-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", directory);
        string endpoint = ConnectionSecurityPolicy.TcpEndpoint("example", 9091);
        try
        {
            ConnectionSecurityPolicy.ValidatePlaintext(false, false, ConnectionSecurityMode.AllowPlaintext, endpoint);
            ConnectionSecurityPolicy.ValidatePlaintext(false, true, ConnectionSecurityMode.RequireE2E, endpoint);
            if (ConnectionSecurityPolicy.DefaultForArguments(new[] { "--allow-unencrypted" }) != ConnectionSecurityMode.AllowPlaintext || ConnectionSecurityPolicy.DefaultForArguments(Array.Empty<string>()) != ConnectionSecurityMode.RequireE2E) return false;
            try { ConnectionSecurityPolicy.ValidatePlaintext(false, false, ConnectionSecurityMode.RequireE2E, endpoint); return false; } catch (IOException) { }
            try { ConnectionSecurityPolicy.ValidatePlaintext(true, false, ConnectionSecurityMode.AllowPlaintext, endpoint); return false; } catch (IOException) { }
            var pins = new EndpointTrustStore(Path.Combine(directory, "trusted-endpoints"));
            pins.Save(endpoint, new string('A', 64));
            foreach (ConnectionSecurityMode mode in new[] { ConnectionSecurityMode.RequireE2E, ConnectionSecurityPolicy.DefaultForArguments(new[] { "--allow-unencrypted" }) })
            {
                try { ConnectionSecurityPolicy.ValidatePlaintext(false, false, mode, endpoint); return false; } catch (IOException) { }
            }
            string actual = ConnectionSecurityPolicy.TcpEndpoint("127.0.0.1", 9091);
            pins.Save(actual, new string('A', 64));
            try { ConnectionSecurityPolicy.ValidatePlaintext(false, false, ConnectionSecurityMode.AllowPlaintext, "tcp:new-alias:9091", actual); return false; } catch (IOException) { }
            string corrupt = ConnectionSecurityPolicy.TcpEndpoint("broken", 9091);
            string path = Path.Combine(directory, "trusted-endpoints", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(corrupt))) + ".trust");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            try { ConnectionSecurityPolicy.ValidatePlaintext(false, false, ConnectionSecurityMode.AllowPlaintext, corrupt); return false; } catch (Exception ex) when (ex is IOException or CryptographicException) { if (ex is PlaintextConsentRequiredException) return false; }
            return ConnectionSecurityPolicy.TcpEndpoint("[::ffff:127.0.0.1]", 9091) == actual;
        }
        finally { Environment.SetEnvironmentVariable("TCPTUNNEL_DATA_HOME", previous); }
    }

    public static bool MatrixSpeedProfileRoundtripSelfTest()
    {
        var saved = new AppProfile { MatrixText = true, MatrixCharacterDelay = 100 };
        var loaded = ApplicationSettings.ParseProfile(ApplicationSettings.ParseLines(ApplicationSettings.Serialize(saved)), new AppProfile());
        var old = ApplicationSettings.ParseProfile(new(), new AppProfile { MatrixCharacterDelay = 500 });
        var malformed = ApplicationSettings.ParseProfile(new() { ["matrixCharacterDelay"] = "payload" }, new AppProfile());
        var bounds = ApplicationSettings.ParseProfile(new() { ["matrixCharacterDelay"] = "2000" }, new AppProfile());
        return loaded.MatrixText && loaded.MatrixCharacterDelay == 100 && old.MatrixCharacterDelay == 20 && malformed.MatrixCharacterDelay == 20 && bounds.MatrixCharacterDelay == 500;
    }
}
