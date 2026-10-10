#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]

namespace TCPTunnel.Cryptography;

internal enum MlsError { InvalidArgument = 1, InvalidState = 2, RejectedMessage = 3, InternalFailure = 4, ResourceLimit = 5, PoisonedState = 6 }
internal sealed class MlsException(MlsError code) : Exception($"MLS operation failed: {code}.") { internal MlsError Code { get; } = code; }
internal sealed record MembershipChange(byte[] Commit, byte[] Welcome);
internal enum MlsReceivedKind { Application = 1, Commit = 2, Removed = 3 }
internal sealed record MlsReceived(MlsReceivedKind Kind, string? SenderFingerprint, byte[] Content);

internal sealed class NativeMlsSession : IDisposable
{
    private const int MaximumInput = 8 * 1024 * 1024;
    private const int MaximumHandshake = 256 * 1024;
    private readonly object gate = new();
    private readonly MlsHandle handle;
    internal string Fingerprint { get; }

    internal NativeMlsSession(byte[]? identityMaterial = null)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("MLS encryption requires Windows x64.");
        if (NativeMethods.AbiVersion() != 5) throw new InvalidOperationException("Unsupported MLS native ABI.");
        ulong token;
        if (identityMaterial == null) Check(NativeMethods.Create(out token));
        else
        {
            if (identityMaterial.Length is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(identityMaterial));
            Check(NativeMethods.ImportIdentity(identityMaterial, (nuint)identityMaterial.Length, out token));
        }
        handle = new MlsHandle(token);
        try
        {
            byte[] publicKey = Execute(1, Array.Empty<byte>());
            if (publicKey.Length != 32) throw new InvalidDataException("Invalid MLS public key.");
            byte[] prefix = Encoding.ASCII.GetBytes("TCPTunnel/Identity/v1/Ed25519\0");
            byte[] identity = new byte[prefix.Length + publicKey.Length];
            prefix.CopyTo(identity, 0); publicKey.CopyTo(identity, prefix.Length);
            Fingerprint = Convert.ToHexString(SHA256.HashData(identity));
        }
        catch { handle.Dispose(); throw; }
    }

    internal byte[] CreateKeyPackage() => Execute(2, Array.Empty<byte>());
    internal byte[] GroupIdentities() => Execute(14, Array.Empty<byte>());
    internal byte[] ExportIdentityMaterial() => Execute(10, Array.Empty<byte>());
    internal byte[] PublicKey => Execute(1, Array.Empty<byte>());
    internal byte[] KeyPackagePublicKey(byte[] keyPackage) { ValidateHandshake(keyPackage); return Execute(13, keyPackage); }
    internal byte[] SignIdentityProof(byte[] payload)
    {
        if (payload.Length is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(payload));
        return Execute(11, payload);
    }
    internal bool VerifyIdentityProof(byte[] publicKey, byte[] payload, byte[] signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64 || payload.Length is < 1 or > 256) return false;
        byte[] input = new byte[96 + payload.Length]; publicKey.CopyTo(input, 0); signature.CopyTo(input, 32); payload.CopyTo(input, 96);
        try { Execute(12, input); return true; }
        catch (MlsException error) when (error.Code == MlsError.RejectedMessage) { return false; }
    }
    internal static string FingerprintOf(byte[] publicKey)
    {
        if (publicKey.Length != 32) throw new ArgumentException("Invalid Ed25519 public key.", nameof(publicKey));
        byte[] prefix = Encoding.ASCII.GetBytes("TCPTunnel/Identity/v1/Ed25519\0");
        byte[] identity = new byte[prefix.Length + publicKey.Length]; prefix.CopyTo(identity, 0); publicKey.CopyTo(identity, prefix.Length);
        return Convert.ToHexString(SHA256.HashData(identity));
    }
    internal void CreateGroup(byte[] groupId)
    {
        if (groupId.Length != 32) throw new ArgumentException("Group ids must contain 32 random bytes.", nameof(groupId));
        Execute(3, groupId);
    }
    internal MembershipChange AddMember(byte[] keyPackage)
    {
        ValidateHandshake(keyPackage);
        byte[] output = Execute(4, keyPackage);
        int offset = 0;
        byte[] commit = ReadPart(output, ref offset);
        byte[] welcome = ReadPart(output, ref offset);
        if (offset != output.Length) throw new InvalidDataException("Unexpected membership output.");
        return new MembershipChange(commit, welcome);
    }
    internal void Join(byte[] welcome) { ValidateHandshake(welcome); Execute(5, welcome); }
    internal void MergePendingCommit() => Execute(6, Array.Empty<byte>());
    internal byte[] Encrypt(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length > MaximumInput - MaximumHandshake) throw new ArgumentOutOfRangeException(nameof(plaintext));
        return Execute(7, plaintext);
    }
    internal MlsReceived Process(byte[] message)
    {
        byte[] output = Execute(8, message);
        try
        {
            if (output.Length == 0) throw new InvalidDataException("Missing MLS result.");
            var kind = (MlsReceivedKind)output[0];
            if (kind == MlsReceivedKind.Application && output.Length >= 33)
                return new MlsReceived(kind, Convert.ToHexString(output.AsSpan(1, 32)), output.AsSpan(33).ToArray());
            if ((kind == MlsReceivedKind.Commit || kind == MlsReceivedKind.Removed) && output.Length == 1)
                return new MlsReceived(kind, null, Array.Empty<byte>());
            throw new InvalidDataException("Invalid MLS result.");
        }
        finally { CryptographicOperations.ZeroMemory(output); }
    }
    internal byte[] RemoveMember(string fingerprint)
    {
        byte[] identity = Convert.FromHexString(fingerprint);
        if (identity.Length != 32) throw new ArgumentException("Invalid fingerprint.", nameof(fingerprint));
        return Execute(9, identity);
    }

    private byte[] Execute(uint operation, byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaximumInput) throw new ArgumentOutOfRangeException(nameof(input));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            int result = NativeMethods.Execute(handle, operation, input, (nuint)input.Length);
            if (result is (int)MlsError.InternalFailure or (int)MlsError.PoisonedState) handle.Dispose();
            Check(result);
            Check(NativeMethods.OutputSize(handle, out nuint size));
            if (size > MaximumInput + MaximumHandshake) { handle.Dispose(); throw new InvalidDataException("MLS output exceeds its bound."); }
            byte[] output = new byte[(int)size];
            try
            {
                // WARNING: Copy the completed result; executing the operation again would advance MLS state twice.
                Check(NativeMethods.CopyOutput(handle, output, size));
                return output;
            }
            catch { CryptographicOperations.ZeroMemory(output); throw; }
        }
    }
    private static void Check(int error) { if (error != 0) throw new MlsException((MlsError)error); }
    private static void ValidateHandshake(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaximumHandshake) throw new ArgumentOutOfRangeException(nameof(input));
    }
    private static byte[] ReadPart(byte[] output, ref int offset)
    {
        if (output.Length - offset < 4) throw new InvalidDataException("Truncated membership output.");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(offset, 4)); offset += 4;
        if (length > MaximumHandshake || length > output.Length - offset) throw new InvalidDataException("Invalid membership output length.");
        byte[] part = output.AsSpan(offset, (int)length).ToArray(); offset += (int)length;
        return part;
    }
    public void Dispose() { lock (gate) handle.Dispose(); }

    private sealed class MlsHandle : SafeHandle
    {
        internal MlsHandle(ulong token) : base(IntPtr.Zero, true) => SetHandle((IntPtr)checked((long)token));
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => NativeMethods.Destroy((ulong)handle.ToInt64()) == 0;
    }

    private static class NativeMethods
    {
        private const string Library = "tcptunnel_crypto";
        [DllImport(Library, EntryPoint = "tc_mls_abi_version", CallingConvention = CallingConvention.Cdecl)] internal static extern uint AbiVersion();
        [DllImport(Library, EntryPoint = "tc_mls_create", CallingConvention = CallingConvention.Cdecl)] internal static extern int Create(out ulong handle);
        [DllImport(Library, EntryPoint = "tc_mls_import_identity", CallingConvention = CallingConvention.Cdecl)] internal static extern int ImportIdentity(byte[] input, nuint length, out ulong handle);
        [DllImport(Library, EntryPoint = "tc_mls_destroy", CallingConvention = CallingConvention.Cdecl)] internal static extern int Destroy(ulong handle);
        [DllImport(Library, EntryPoint = "tc_mls_execute", CallingConvention = CallingConvention.Cdecl)] internal static extern int Execute(MlsHandle handle, uint operation, byte[] input, nuint length);
        [DllImport(Library, EntryPoint = "tc_mls_output_size", CallingConvention = CallingConvention.Cdecl)] internal static extern int OutputSize(MlsHandle handle, out nuint length);
        [DllImport(Library, EntryPoint = "tc_mls_copy_output", CallingConvention = CallingConvention.Cdecl)] internal static extern int CopyOutput(MlsHandle handle, [Out] byte[] output, nuint capacity);
    }
}
