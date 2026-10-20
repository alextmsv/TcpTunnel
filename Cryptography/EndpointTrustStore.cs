#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TCPTunnel.Cryptography;

internal sealed class EndpointTrustStore(string directory)
{
    internal sealed record Pin(string Fingerprint, string Snapshot);
    private static readonly string Domain = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("TCPTunnel/EndpointPin/v1")));
    private readonly RoomTrustStore storage = new(directory);
    private readonly string directory = Path.GetFullPath(directory);
    private static byte[] Id(string endpoint) => SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.ToLowerInvariant()));
    internal bool HasPin(string endpoint)
    {
        if (String.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("A transport endpoint is required.");
        string path = Path.Combine(directory, Convert.ToHexString(Id(endpoint)) + ".trust");
        for (DirectoryInfo? entry = new(directory); entry != null; entry = entry.Parent)
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Endpoint trust directories must not contain links.");
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    internal void Check(string endpoint, string fingerprint)
    {
        if (String.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("A transport endpoint is required.");
        byte[] id = Id(endpoint);
        if (!HasPin(endpoint)) return;
        var pins = storage.Load(id, Domain);
        if (pins.Length != 1 || !pins[0].Allowed || !pins[0].Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException(Lang.Get(TextId.CryptoOwnerChanged));
    }
    internal Pin? ReadPin(string endpoint)
    {
        if (!HasPin(endpoint)) return null;
        byte[] id = Id(endpoint);
        try
        {
            var pins = storage.Load(id, Domain);
            if (pins.Length != 1 || !pins[0].Allowed || pins[0].Fingerprint.Length != 64 || !System.Linq.Enumerable.All(pins[0].Fingerprint, Uri.IsHexDigit))
                throw new InvalidDataException("Invalid endpoint trust binding.");
            return new Pin(pins[0].Fingerprint, storage.SnapshotHash(id, Domain));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or CryptographicException or System.Text.Json.JsonException)
        { throw new IOException(Lang.Get(TextId.ConnectionPinUnavailable), ex); }
    }
    internal void Save(string endpoint, string fingerprint)
    {
        Check(endpoint, fingerprint);
        storage.Save(Id(endpoint), Domain, new[] { new RoomIdentityBinding("hubOwner", fingerprint, true) });
    }
}
