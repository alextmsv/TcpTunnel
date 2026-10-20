#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCPTunnel.Cryptography;

internal sealed record RoomIdentityBinding(string Nickname, string Fingerprint, bool Allowed);

internal sealed class RoomTrustStore(string directory)
{
    private sealed record Document(int Version, string Room, string Owner, RoomIdentityBinding[] Bindings);
    private readonly string directory = Path.GetFullPath(directory);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> loadedHashes = new(StringComparer.OrdinalIgnoreCase);
    internal string SnapshotHash(byte[] room, string owner) => loadedHashes.TryGetValue(PathFor(room, owner), out var hash) ? Convert.ToHexString(hash) : throw new IOException("Missing trust snapshot.");
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal RoomIdentityBinding[] Load(byte[] room, string owner)
    {
        string path = PathFor(room, owner);
        CheckDirectories();
        if (!File.Exists(path)) { loadedHashes.TryRemove(path, out _); return Array.Empty<RoomIdentityBinding>(); }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Room trust file links are not allowed.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 131072) throw new InvalidDataException("Invalid room trust file size.");
        byte[] protectedData = new byte[(int)file.Length]; file.ReadExactly(protectedData);
        loadedHashes[path] = SHA256.HashData(protectedData);
        byte[] plaintext = WindowsKeyProtection.Unprotect(protectedData, Entropy(room, owner));
        try
        {
            Document document = JsonSerializer.Deserialize<Document>(plaintext, JsonOptions) ?? throw new InvalidDataException("Missing room trust data.");
            if (document.Version != 1 || document.Room != Convert.ToHexString(room) || document.Owner != owner || document.Bindings == null || document.Bindings.Length > 512 ||
                document.Bindings.Select(binding => binding.Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 64 ||
                document.Bindings.GroupBy(binding => binding.Fingerprint, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 8))
                throw new InvalidDataException("Room trust data does not match the verified room owner.");
            return document.Bindings;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal void Save(byte[] room, string owner, RoomIdentityBinding[] bindings)
    {
        string path = PathFor(room, owner);
        using var lease = new ProfileWriteLease(path);
        if (bindings.Length > 512 || bindings.Select(binding => binding.Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 64 ||
            bindings.GroupBy(binding => binding.Fingerprint, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 8)) throw new ArgumentOutOfRangeException(nameof(bindings));
        Directory.CreateDirectory(directory); CheckDirectories();
        loadedHashes.TryGetValue(path, out byte[]? loadedHash);
        // WARNING: Never overwrite a trust snapshot changed by another Hub; doing so could erase pinned identities.
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Room trust file links are not allowed.");
            using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (current.Length > 131072 || loadedHash == null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), loadedHash))
                throw new IOException("Room trust changed in another application instance. Restart the Hub before updating approvals.");
        }
        else if (loadedHash != null) throw new IOException("Room trust file disappeared; approval update cancelled.");
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new Document(1, Convert.ToHexString(room), owner, bindings), JsonOptions);
        byte[] protectedData;
        try { protectedData = WindowsKeyProtection.Protect(plaintext, Entropy(room, owner)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using WindowsIdentity user = WindowsIdentity.GetCurrent();
            SecurityIdentifier sid = user.User ?? throw new InvalidOperationException("Windows user identity is unavailable.");
            var security = new FileSecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(sid);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            using (FileStream output = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, security))
            { output.Write(protectedData); output.Flush(flushToDisk: true); }
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Room trust file links are not allowed.");
            File.Move(temporary, path, overwrite: true);
            loadedHashes[path] = SHA256.HashData(protectedData);
        }
        finally { CryptographicOperations.ZeroMemory(protectedData); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string PathFor(byte[] room, string owner)
    {
        if (room.Length != 32 || owner.Length != 64 || !owner.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid room identity.");
        return Path.Combine(directory, Convert.ToHexString(room) + ".trust");
    }
    private static byte[] Entropy(byte[] room, string owner) => SHA256.HashData(Encoding.ASCII.GetBytes("TCPTunnel/RoomTrust/v1/" + Convert.ToHexString(room) + "/" + owner));
    private void CheckDirectories()
    {
        for (DirectoryInfo? entry = new(directory); entry != null; entry = entry.Parent)
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Room trust directories must not contain links.");
    }
}
