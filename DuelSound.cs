using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TCPTunnel;

internal static class DuelSound
{
    private static readonly Lazy<IntPtr[]> Waves = new(() =>
    {
        var result = new IntPtr[11];
        for (int i = 0; i < result.Length; i++)
        {
            byte[] wave = Wave(i + 1);
            result[i] = GCHandle.Alloc(wave, GCHandleType.Pinned).AddrOfPinnedObject();
        }
        return result;
    });

    internal static int Step(int streak) => (Math.Max(1, streak) - 1) % 11 + 1;
    internal static void Play(int streak) => PlaySound(Waves.Value[Step(streak) - 1], IntPtr.Zero, 0x7);

    internal static byte[] Wave(int step)
    {
        const int rate = 22050;
        bool melody = step == 11;
        int samples = (int)(rate * (melody ? 0.34 : 0.09));
        using var buffer = new MemoryStream(44 + 2 * samples);
        using var w = new BinaryWriter(buffer);
        w.Write("RIFF"u8); w.Write(36 + 2 * samples); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(2 * rate);
        w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(2 * samples);
        double pitch = 360 * Math.Pow(2, (step - 1) / 12.0);
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)rate;
            bool upper = melody && i >= 2205;
            double age = upper ? (i - 2205) / (double)rate : t;
            double length = melody ? (upper ? 0.24 : 0.10) : (samples - 1) / (double)rate;
            double envelope = Math.Min(1, age / 0.007) * Math.Pow(Math.Max(0, 1 - age / length), 1.65);
            double cycles = melody ? (upper ? 775 : 520) * age : pitch * t + 460 * t * t;
            w.Write((short)Math.Round(1800 * envelope * Math.Sin(2 * Math.PI * cycles)));
        }
        return buffer.ToArray();
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(IntPtr memory, IntPtr module, uint flags);
}
