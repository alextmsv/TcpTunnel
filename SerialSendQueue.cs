using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TCPTunnel
{
    internal sealed class SendQueueFullException : IOException
    {
        internal SendQueueFullException(string message) : base(message) { }
    }

    internal sealed class SerialSendQueue
    {
        private sealed class Item
        {
            internal readonly EncodedFrameBatch Frames;
            internal readonly int Bytes;
            internal readonly CancellationToken Token;
            internal readonly TaskCompletionSource Result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Item(EncodedFrameBatch frames, CancellationToken token) { Frames = frames; Bytes = frames.ByteCount; Token = token; }
        }

        private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> writer;
        private readonly Action abort;
        private readonly object gate = new object();
        private readonly Queue<Item> items = new Queue<Item>();
        private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int maxBytes;
        private readonly int maxOperations;
        private readonly TimeSpan timeout;
        private int bytes;
        private int operations;
        private bool closed;

        internal SerialSendQueue(Func<ReadOnlyMemory<byte>, CancellationToken, Task> writer, Action abort,
            int maxPendingBytes = 8 * 1024 * 1024, int maxPendingOperations = 256, TimeSpan? writeTimeout = null)
        {
            this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
            this.abort = abort ?? throw new ArgumentNullException(nameof(abort));
            if (maxPendingBytes < 1 || maxPendingOperations < 1) throw new ArgumentOutOfRangeException();
            maxBytes = maxPendingBytes; maxOperations = maxPendingOperations;
            timeout = writeTimeout ?? TimeSpan.FromSeconds(5);
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(writeTimeout));
            _ = RunAsync();
        }

        internal Task Completion => completion.Task;

        internal Task Enqueue(IReadOnlyList<byte[]> frames, CancellationToken cancellationToken) =>
            Enqueue(EncodedFrameBatch.Snapshot(frames), cancellationToken);

        internal Task Enqueue(EncodedFrameBatch frames, CancellationToken cancellationToken)
        {
            if (frames == null) throw new ArgumentNullException(nameof(frames));
            if (frames.Count == 0) return Task.CompletedTask;
            var item = new Item(frames, cancellationToken);
            lock (gate)
            {
                if (closed) { item.Result.TrySetException(new ObjectDisposedException(nameof(SerialSendQueue))); return item.Result.Task; }
                if (operations >= maxOperations || bytes > maxBytes - item.Bytes)
                {
                    var error = new SendQueueFullException(Lang.Get(TextId.ClientNotReceiving));
                    FailLocked(error); item.Result.TrySetException(error); return item.Result.Task;
                }
                operations++; bytes += item.Bytes; items.Enqueue(item); signal.Release();
            }
            return item.Result.Task;
        }

        internal void Close() { lock (gate) FailLocked(new ObjectDisposedException(nameof(SerialSendQueue))); }

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    Item item;
                    lock (gate) item = items.Count == 0 ? null : items.Dequeue();
                    if (item == null)
                    {
                        if (closed) break;
                        await signal.WaitAsync(stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    try
                    {
                        if (item.Token.IsCancellationRequested) item.Result.TrySetCanceled(item.Token);
                        else
                        {
                            for (int index = 0; index < item.Frames.Count; index++)
                            {
                                using var linked = CancellationTokenSource.CreateLinkedTokenSource(item.Token, stop.Token);
                                linked.CancelAfter(timeout);
                                await writer(item.Frames[index], linked.Token).WaitAsync(timeout, linked.Token).ConfigureAwait(false);
                            }
                            item.Result.TrySetResult();
                        }
                    }
                    catch (Exception ex) { item.Result.TrySetException(ex); lock (gate) FailLocked(ex); }
                    finally { lock (gate) { operations--; bytes -= item.Bytes; } }
                }
                completion.TrySetResult();
            }
            catch (OperationCanceledException) { completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }

        private void FailLocked(Exception error)
        {
            if (!closed)
            {
                closed = true;
                try { abort(); } catch { }
                stop.Cancel();
                try { signal.Release(); } catch { }
            }
            while (items.Count > 0) items.Dequeue().Result.TrySetException(error);
        }
    }
}
