using System;
using System.Buffers.Binary;
using System.Text;

namespace TCPTunnel
{
    internal enum BluetoothHubMode : byte { BluetoothOnly = 0, PublicAndBluetooth = 1, LanAndBluetooth = 2 }

    internal readonly record struct HubBeacon(
        ulong Address,
        uint Instance,
        BluetoothHubMode Mode,
        int Participants,
        string Nickname,
        bool NicknameTruncated);

    internal static class HubBeaconCodec
    {
        internal const ushort CompanyId = 0xFFFF;
        internal const int Length = 24;
        private const byte Version = 1;
        private const int NicknameOffset = 14;
        private const int NicknameBytes = Length - NicknameOffset;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static byte[] Encode(ulong address, uint instance, BluetoothHubMode mode, int participants, string nickname)
        {
            if (address == 0 || address > 0xFFFFFFFFFFFF || instance == 0 || !Enum.IsDefined(mode) || !NetWorker.IsNicknameValid(nickname))
                throw new ArgumentOutOfRangeException(nameof(address));

            byte[] bytes = new byte[Length];
            bytes[0] = (byte)'T';
            bytes[1] = (byte)'T';
            int nicknameLength = FitNickname(nickname, out bool truncated);
            bytes[2] = (byte)((Version << 4) | ((byte)mode << 1) | (truncated ? 1 : 0));
            for (int index = 0; index < 6; index++)
                bytes[3 + index] = (byte)(address >> (8 * index));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9), instance);
            bytes[13] = (byte)Math.Clamp(participants, 0, 255);
            StrictUtf8.GetBytes(nickname.AsSpan(0, nicknameLength), bytes.AsSpan(NicknameOffset));
            return bytes;
        }

        internal static bool TryDecode(ReadOnlySpan<byte> bytes, out HubBeacon beacon)
        {
            beacon = default;
            if (bytes.Length != Length || bytes[0] != 'T' || bytes[1] != 'T' || (bytes[2] >> 4) != Version)
                return false;

            var mode = (BluetoothHubMode)((bytes[2] >> 1) & 0x3);
            if (!Enum.IsDefined(mode))
                return false;
            bool truncated = (bytes[2] & 1) != 0;

            ulong address = 0;
            for (int index = 0; index < 6; index++)
                address |= (ulong)bytes[3 + index] << (8 * index);
            uint instance = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(9));
            if (address == 0 || instance == 0)
                return false;

            ReadOnlySpan<byte> nicknameBytes = bytes.Slice(NicknameOffset);
            int end = nicknameBytes.IndexOf((byte)0);
            if (end >= 0)
            {
                if (nicknameBytes.Slice(end).IndexOfAnyExcept((byte)0) >= 0)
                    return false;
                nicknameBytes = nicknameBytes.Slice(0, end);
            }

            string nickname;
            try { nickname = StrictUtf8.GetString(nicknameBytes); }
            catch (DecoderFallbackException) { return false; }

            if (truncated ? !IsNicknamePrefix(nickname) : !NetWorker.IsNicknameValid(nickname))
                return false;

            beacon = new HubBeacon(address, instance, mode, bytes[13], nickname, truncated);
            return true;
        }

        private static int FitNickname(string nickname, out bool truncated)
        {
            int length = 0;
            int bytes = 0;
            while (length < nickname.Length)
            {
                int step = Char.IsHighSurrogate(nickname[length]) && length + 1 < nickname.Length ? 2 : 1;
                int size = StrictUtf8.GetByteCount(nickname.AsSpan(length, step));
                if (bytes + size > NicknameBytes)
                    break;
                bytes += size;
                length += step;
            }
            truncated = length < nickname.Length;
            return length;
        }

        private static bool IsNicknamePrefix(string prefix) =>
            prefix.Length > 0 && NetWorker.IsNicknameValid(prefix.PadRight(3, '_'));

        internal static bool RunSelfTest()
        {
            byte[] latin = Encode(0xD8F2CA96C336, 0x1234ABCD, BluetoothHubMode.PublicAndBluetooth, 5, "alextmsv");
            bool latinOk = latin.Length == Length && TryDecode(latin, out HubBeacon first) &&
                first.Address == 0xD8F2CA96C336 && first.Instance == 0x1234ABCD &&
                first.Mode == BluetoothHubMode.PublicAndBluetooth && first.Participants == 5 &&
                first.Nickname == "alextmsv" && !first.NicknameTruncated;

            byte[] cyrillic = Encode(1, 7, BluetoothHubMode.BluetoothOnly, 300, "Длинныйник");
            bool truncatedOk = TryDecode(cyrillic, out HubBeacon second) && second.NicknameTruncated &&
                second.Nickname == "Длинн" && second.Participants == 255;

            byte[] tampered = (byte[])latin.Clone();
            tampered[NicknameOffset] = 0x1B;
            byte[] badVersion = (byte[])latin.Clone();
            badVersion[2] = 0x20;
            byte[] garbageAfterEnd = Encode(2, 9, BluetoothHubMode.LanAndBluetooth, 1, "bob");
            garbageAfterEnd[Length - 1] = (byte)'x';

            bool rejectsBad = !TryDecode(tampered, out _) && !TryDecode(badVersion, out _) &&
                              !TryDecode(garbageAfterEnd, out _) && !TryDecode(latin.AsSpan(0, Length - 1), out _);
            return latinOk && truncatedOk && rejectsBad;
        }
    }
}
