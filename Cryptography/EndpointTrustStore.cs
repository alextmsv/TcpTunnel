#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TCPTunnel.Cryptography;

internal sealed class EndpointTrustStore(string directory)
{
    private static readonly string Domain = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("TCPTunnel/EndpointPin/v1")));
    private readonly RoomTrustStore storage = new(directory);
    private readonly string directory = Path.GetFullPath(directory);
    private static byte[] Id(string endpoint) => SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.ToLowerInvariant()));
    internal void Check(string endpoint, string fingerprint)
    {
        if (String.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("A transport endpoint is required.");
        byte[] id = Id(endpoint);
        if (!File.Exists(Path.Combine(directory, Convert.ToHexString(id) + ".trust"))) return;
        var pins = storage.Load(id, Domain);
        if (pins.Length != 1 || !pins[0].Allowed || !pins[0].Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException(Lang.Get(TextId.CryptoOwnerChanged));
    }
    internal void Save(string endpoint, string fingerprint)
    {
        Check(endpoint, fingerprint);
        storage.Save(Id(endpoint), Domain, new[] { new RoomIdentityBinding("hubOwner", fingerprint, true) });
    }
}
