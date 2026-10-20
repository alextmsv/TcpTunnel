#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace TCPTunnel.Cryptography;

internal sealed record SecureInvitation(int Version, string Room, string Owner, string Host, int Port, ulong BluetoothAddress,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Token = null)
{
    internal string Encode() => (Version == 3 ? "tct3:" : "tct2:") + Convert.ToBase64String(SecureWire.Json(this));
    internal static SecureInvitation Parse(string value)
    {
        if (value.Length > 4096 || !(value.StartsWith("tct2:", StringComparison.Ordinal) || value.StartsWith("tct3:", StringComparison.Ordinal))) throw new InvalidDataException("Invalid E2E invitation.");
        var invite = SecureWire.Parse<SecureInvitation>(Convert.FromBase64String(value.Substring(5)));
        if (invite.Version is not (2 or 3) || value[3] != (char)('0' + invite.Version) || (invite.Version == 3 ? !OneTimeInvitations.Valid(invite.Token) : invite.Token != null) || invite.Room == null || invite.Owner == null || invite.Room.Length != 64 || invite.Owner.Length != 64 || !invite.Room.All(Uri.IsHexDigit) || !invite.Owner.All(Uri.IsHexDigit) || invite.Host == null || invite.Host.Length > 253 || invite.Host.Any(Char.IsControl) || invite.Port is < 0 or > 65535 || (invite.Port == 0 && invite.BluetoothAddress == 0)) throw new InvalidDataException("Invalid E2E invitation.");
        return invite;
    }
    internal bool Matches(SecureBoot boot) => Convert.ToHexString(boot.Room).Equals(Room, StringComparison.OrdinalIgnoreCase) && NativeMlsSession.FingerprintOf(boot.OwnerKey).Equals(Owner, StringComparison.OrdinalIgnoreCase);
}
