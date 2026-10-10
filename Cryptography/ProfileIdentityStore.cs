#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace TCPTunnel.Cryptography;

internal sealed record ProfileIdentityReference(Guid IdentityId, string Fingerprint);

internal sealed class ProfileIdentityStore
{
    private readonly string directory;
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("TCPTunnel-DPAPI-Identity-v1\0");
    internal ProfileIdentityStore(string directory) => this.directory = Path.GetFullPath(directory);

    internal ProfileIdentityReference Create(Guid identityId)
    {
        string path = KeyPath(identityId);
        Directory.CreateDirectory(directory);
        RejectReparsePoints(directory);
        if (File.Exists(path)) throw new IOException("An identity already exists for this profile.");
        using var session = new NativeMlsSession();
        byte[] key = session.ExportIdentityMaterial();
        byte[] protectedKey;
        try { protectedKey = WindowsKeyProtection.Protect(key, Entropy(identityId)); }
        finally { CryptographicOperations.ZeroMemory(key); }
        bool created = false;
        try
        {
            using WindowsIdentity user = WindowsIdentity.GetCurrent();
            SecurityIdentifier sid = user.User ?? throw new InvalidOperationException("Windows user identity is unavailable.");
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(sid);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            // WARNING: Apply the private ACL during creation, before any key data is written.
            using var output = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, security);
            created = true;
            output.Write(Header); output.Write(protectedKey); output.Flush(flushToDisk: true);
            return new ProfileIdentityReference(identityId, session.Fingerprint);
        }
        catch { if (created) File.Delete(path); throw; }
        finally { CryptographicOperations.ZeroMemory(protectedKey); }
    }

    internal NativeMlsSession Open(ProfileIdentityReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (String.IsNullOrEmpty(reference.Fingerprint) || reference.Fingerprint.Length != 64 || !reference.Fingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid public identity fingerprint.");
        string path = KeyPath(reference.IdentityId);
        RejectReparsePoints(directory);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Identity file links are not allowed.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length <= Header.Length || file.Length > 4096) throw new InvalidDataException("Invalid identity file size.");
        byte[] contents = new byte[(int)file.Length]; file.ReadExactly(contents);
        if (!contents.AsSpan(0, Header.Length).SequenceEqual(Header)) throw new InvalidDataException("Invalid identity file version.");
        byte[] key = WindowsKeyProtection.Unprotect(contents.AsSpan(Header.Length).ToArray(), Entropy(reference.IdentityId));
        try
        {
            var session = new NativeMlsSession(key);
            if (!String.Equals(session.Fingerprint, reference.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                session.Dispose();
                throw new InvalidDataException("The stored key does not match the profile's pinned identity.");
            }
            return session;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private string KeyPath(Guid identityId)
    {
        if (identityId == Guid.Empty) throw new ArgumentException("Profile identity ids cannot be empty.", nameof(identityId));
        return Path.Combine(directory, identityId.ToString("N") + ".key");
    }
    private static byte[] Entropy(Guid identityId) => SHA256.HashData(Encoding.ASCII.GetBytes("TCPTunnel/ProfileIdentity/v1/" + identityId.ToString("N")));
    private static void RejectReparsePoints(string path)
    {
        for (DirectoryInfo? entry = new(path); entry != null; entry = entry.Parent)
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Identity directories must not contain links.");
    }
}

internal static class WindowsKeyProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { internal int Length; internal IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static byte[] Protect(byte[] data, byte[] entropy) => Transform(data, entropy, true);
    internal static byte[] Unprotect(byte[] data, byte[] entropy) => Transform(data, entropy, false);

    private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        if (data.Length is < 1 or > 32768 || entropy.Length != 32) throw new ArgumentException("Invalid DPAPI input.");
        GCHandle inputHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        Blob output = default;
        try
        {
            var input = new Blob { Length = data.Length, Data = inputHandle.AddrOfPinnedObject() };
            var optionalEntropy = new Blob { Length = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            const uint UiForbidden = 1;
            bool success = protect
                ? CryptProtectData(ref input, null, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect or open this profile identity.");
            if (output.Length is < 1 or > 32768 || output.Data == IntPtr.Zero) throw new CryptographicException("Invalid DPAPI output.");
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                if (!protect && output.Length is > 0 and <= 32768)
                    for (int index = 0; index < output.Length; index++) Marshal.WriteByte(output.Data, index, 0);
                LocalFree(output.Data);
            }
            entropyHandle.Free(); inputHandle.Free();
        }
    }
}
