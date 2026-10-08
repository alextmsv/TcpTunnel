using System;
using System.Collections.Generic;
using System.IO;

namespace TCPTunnel
{
    // WARNING: Mutating or exposing these buffers can corrupt every recipient's queued frames.
    internal sealed class EncodedFrameBatch
    {
        private readonly byte[][] frames;
        internal int Count => frames.Length;
        internal int ByteCount { get; }
        internal ReadOnlyMemory<byte> this[int index] => frames[index];

        private EncodedFrameBatch(byte[][] frames)
        {
            this.frames = frames;
            long total = 0;
            foreach (byte[] frame in frames) total += frame.Length;
            if (total > Int32.MaxValue) throw new InvalidDataException("Send batch is too large.");
            ByteCount = (int)total;
        }

        internal static EncodedFrameBatch Encode(IReadOnlyList<string> messages)
        {
            if (messages == null) throw new ArgumentNullException(nameof(messages));
            var frames = new byte[messages.Count][];
            for (int index = 0; index < frames.Length; index++)
                frames[index] = MessageProtocol.EncodeFrame(messages[index] ??
                    throw new ArgumentException("A message batch cannot contain null values.", nameof(messages)));
            return new EncodedFrameBatch(frames);
        }

        internal static EncodedFrameBatch Snapshot(IReadOnlyList<byte[]> source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var frames = new byte[source.Count][];
            for (int index = 0; index < frames.Length; index++)
                frames[index] = (byte[])(source[index] ??
                    throw new ArgumentException("A frame cannot be null.", nameof(source))).Clone();
            return new EncodedFrameBatch(frames);
        }
    }
}
