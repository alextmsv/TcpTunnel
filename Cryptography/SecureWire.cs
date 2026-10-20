#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel.Cryptography;

internal sealed record SecurePacket(byte Kind, byte[] Payload);
internal sealed record SecureBoot(byte[] Room, byte[] OwnerKey, byte[] CertificateHash, string OwnerName, byte[] Signature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ProtocolVersion = 0);
internal sealed record SecureHello(string Nickname, byte[] Package, byte[] Binding,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ProtocolVersion = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InvitationToken = null);
internal sealed record SecureRosterEntry(string Nickname, string Fingerprint, string? Snake = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OriginalNickname = null);
internal sealed record SecureRoster(byte[] Room, long Revision, SecureRosterEntry[] Members, byte[] Signature);

internal static class SecureWire
{
    internal const int MaximumPayload = 6 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    internal static T Parse<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Missing secure protocol data.");
    internal static byte[] Encode(byte kind, byte[] payload)
    {
        if (payload.Length > MaximumPayload) throw new InvalidDataException("Secure packet exceeds its limit.");
        var result = new byte[5 + payload.Length]; BinaryPrimitives.WriteInt32BigEndian(result, payload.Length + 1);
        result[4] = kind; payload.CopyTo(result, 5); return result;
    }
    internal static async Task WriteAsync(Stream stream, byte kind, byte[] payload, CancellationToken token)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(TimeSpan.FromMinutes(3));
        byte[] frame = Encode(kind, payload);
        for (int offset = 0; offset < frame.Length; offset += 16 * 1024)
        {
            using var chunk = CancellationTokenSource.CreateLinkedTokenSource(total.Token); chunk.CancelAfter(TimeSpan.FromSeconds(30));
            await stream.WriteAsync(frame.AsMemory(offset, Math.Min(16 * 1024, frame.Length - offset)), chunk.Token).ConfigureAwait(false);
            await stream.FlushAsync(chunk.Token).ConfigureAwait(false);
        }
    }
    internal static EncodedFrameBatch EncodeBatch(byte kind, byte[] payload)
    {
        if (payload.Length > MaximumPayload) throw new InvalidDataException("Secure packet exceeds its limit.");
        int length = payload.Length + 5;
        var chunks = new byte[(length + 16383) / 16384][];
        for (int index = 0; index < chunks.Length; index++)
        {
            int start = index * 16384; chunks[index] = new byte[Math.Min(16384, length - start)];
            if (index == 0)
            {
                BinaryPrimitives.WriteInt32BigEndian(chunks[0], payload.Length + 1); chunks[0][4] = kind;
                payload.AsSpan(0, chunks[0].Length - 5).CopyTo(chunks[0].AsSpan(5));
            }
            else payload.AsSpan(start - 5, chunks[index].Length).CopyTo(chunks[index]);
        }
        return EncodedFrameBatch.TakeOwnership(chunks);
    }
    internal static async Task<SecurePacket> ReadAsync(Stream stream, CancellationToken token, int maximum = MaximumPayload)
    {
        byte[] header = new byte[5];
        await stream.ReadExactlyAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await stream.ReadExactlyAsync(header.AsMemory(1, 4), timeout.Token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header) - 1;
        if (length < 0 || length > maximum) throw new InvalidDataException("Invalid secure packet length.");
        int kindLimit = header[4] switch
        {
            1 => MessageProtocol.MaxFrameBytes,
            2 or 3 or 4 or 7 => 256 * 1024,
            5 or 6 => MaximumPayload,
            10 or 11 => 32,
            _ => throw new InvalidDataException("Unknown secure packet kind.")
        };
        if (length > kindLimit || (header[4] is 10 or 11 && length != 32)) throw new InvalidDataException("Secure packet exceeds its kind limit.");
        var payload = new byte[length];
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(TimeSpan.FromMinutes(3));
        for (int offset = 0; offset < length; offset += 16 * 1024)
        {
            using var chunk = CancellationTokenSource.CreateLinkedTokenSource(total.Token); chunk.CancelAfter(TimeSpan.FromSeconds(30));
            await stream.ReadExactlyAsync(payload.AsMemory(offset, Math.Min(16 * 1024, length - offset)), chunk.Token).ConfigureAwait(false);
        }
        return new SecurePacket(header[4], payload);
    }
    internal static byte[] BootProof(SecureBoot boot)
    {
        if (boot.ProtocolVersion is not (0 or 2 or 3)) throw new InvalidDataException("Unsupported encrypted chat protocol.");
        if (boot.Room.Length != 32 || boot.OwnerKey.Length != 32 || boot.CertificateHash.Length != 32 || !NetWorker.IsNicknameValid(boot.OwnerName)) throw new InvalidDataException("Invalid secure Hub announcement.");
        return Proof(2, Json(boot with { Signature = Array.Empty<byte>() }));
    }
    internal static byte[] Proof(byte domain, byte[] body)
    {
        var result = new byte[33]; result[0] = domain; SHA256.HashData(body).CopyTo(result, 1); return result;
    }
    internal static byte[] Pack(IReadOnlyList<string> messages)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Utf8, true);
        if (messages.Count is < 1 or > 1024) throw new InvalidDataException("Invalid message batch.");
        writer.Write(messages.Count);
        foreach (string message in messages)
        {
            if (Utf8.GetByteCount(message) > MessageProtocol.MaxFrameBytes) throw new InvalidDataException("Chat frame exceeds its limit.");
            writer.Write(message);
            if (memory.Length > MaximumPayload - 4096) throw new InvalidDataException("Chat batch exceeds its limit.");
        }
        return memory.ToArray();
    }
    internal static string[] Unpack(byte[] data)
    {
        using var memory = new MemoryStream(data, false); using var reader = new BinaryReader(memory, Utf8);
        int count = reader.ReadInt32(); if (count is < 1 or > 1024) throw new InvalidDataException("Invalid chat batch count.");
        var messages = new string[count];
        for (int i = 0; i < count; i++)
        {
            int length = reader.Read7BitEncodedInt();
            if (length < 0 || length > MessageProtocol.MaxFrameBytes || length > memory.Length - memory.Position) throw new InvalidDataException("Invalid chat frame length.");
            messages[i] = Utf8.GetString(reader.ReadBytes(length));
        }
        if (memory.Position != memory.Length) throw new InvalidDataException("Unexpected trailing chat data.");
        return messages;
    }
    internal static string[] Render(string[] messages, string nickname)
    {
        if (!NetWorker.IsNicknameValid(nickname)) throw new InvalidDataException("Invalid verified sender.");
        if (messages.Length > 1)
        {
            var assembler = new ImageAnimationAssembler(); AnimatedImagePacket? packet = null;
            for (int i = 0; i < messages.Length; i++)
            {
                if (!ImageAnimationProtocol.TryParseClient(messages[i], out var frame)) throw new InvalidDataException("Invalid encrypted animation.");
                var result = assembler.Accept(frame, out packet);
                if (result == ImageAnimationAssemblyResult.Invalid || (result == ImageAnimationAssemblyResult.Completed) != (i == messages.Length - 1)) throw new InvalidDataException("Incomplete encrypted animation.");
            }
            return ImageAnimationProtocol.CreateServerTransfer(packet!, nickname);
        }
        string message = messages[0];
        if (FileTransferProtocol.IsControl(message))
        {
            if (!FileTransferProtocol.TryBind(message, nickname, out string file)) throw new InvalidDataException("Invalid encrypted file transfer.");
            return new[] { file };
        }
        if (ImageProtocol.IsImageControlMessage(message))
        {
            if (!ImageProtocol.TryParseClientFrame(message, out var image)) throw new InvalidDataException("Invalid encrypted image.");
            return new[] { ImageProtocol.CreateServerFrame(image, nickname) };
        }
        if (SnakeProtocol.TryParseClientProfile(message, out var profile)) return new[] { profile.Enabled ? SnakeProtocol.CreateSet(nickname, profile) : SnakeProtocol.CreateRemove(nickname) };
        if (message.Length > MessageProtocol.MaxMessageCharacters || SystemMessageProtocol.IsControlMessage(message) || LegacyEventProtocol.IsControlMessage(message) || SnakeProtocol.IsSnakeControlMessage(message) || ImageAnimationProtocol.IsAnimationControlMessage(message) || WhoisProtocol.IsControl(message) || HubStatusProtocol.IsControl(message)) throw new InvalidDataException("Invalid encrypted chat content.");
        return new[] { $"[{nickname}]: {message}" };
    }
}
