#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace TCPTunnel;

internal sealed class FileTransferSession : IDisposable
{
    private sealed class Transfer : IDisposable
    {
        internal required FileTransferPacket Offer;
        internal required DateTimeOffset Expires;
        internal ECDiffieHellman? Exchange;
        internal byte[]? Secret;
        internal string? Source;
        internal DateTime SourceWrite;
        internal string? Temporary;
        internal FileStream? File;
        internal IncrementalHash? Hash;
        internal long Received;
        internal long Sequence;
        internal int Number;
        internal bool Sending;
        internal readonly CancellationTokenSource Cancellation = new();
        public void Dispose()
        {
            File?.Dispose(); File = null; Hash?.Dispose(); Hash = null; Exchange?.Dispose(); Exchange = null;
            if (Secret != null) CryptographicOperations.ZeroMemory(Secret);
            Cancellation.Dispose();
            if (Temporary != null) { try { System.IO.File.Delete(Temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } Temporary = null; }
        }
    }
    private readonly Func<string, CancellationToken, Task> send;
    private readonly Action<string> notice;
    private readonly Func<string> nickname;
    private readonly string desktop;
    private readonly CancellationTokenSource stop;
    private readonly Dictionary<string, Transfer> transfers = new(StringComparer.Ordinal);
    private readonly HashSet<string> peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly SemaphoreSlim outgoing = new(2, 2);
    private readonly ConcurrentDictionary<int, Task> workers = new();
    private readonly Channel<string> notices = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Timer expiry;
    private int nextNumber;
    private bool disposed;
    private bool available;
    internal FileTransferSession(Func<string> nickname, Func<string, CancellationToken, Task> send, Action<string> notice, CancellationToken token, string? desktop = null)
    {
        this.nickname = nickname; this.send = send; this.notice = notice;
        this.desktop = Path.GetFullPath(desktop ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        expiry = new Timer(_ => Expire(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _ = PublishNoticesAsync();
    }
    private void Notify(string text) => notices.Writer.TryWrite(text);
    private async Task PublishNoticesAsync()
    {
        try { await foreach (string text in notices.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false)) notice(text); }
        catch (OperationCanceledException) { }
    }
    private static string Text(string ru, string en) => Lang.Current == AppLanguage.Russian ? ru : en;
    internal Task AdvertiseAsync() => Send(new("hello"));
    internal void SetAvailable(bool value) { Task change = SetAvailableAsync(value); Run(() => change); }
    internal Task SetAvailableAsync(bool value)
    {
        lock (gate)
        {
            if (disposed || available == value) return Task.CompletedTask;
            available = value;
            if (!value) { Disable(); return Task.CompletedTask; }
        }
        return AdvertiseAsync();
    }
    private Task Send(FileTransferPacket packet) => send(FileTransferProtocol.Encode(packet), stop.Token);
    internal bool HandleCommand(string input)
    {
        int split = input.IndexOf(' ');
        string command = split < 0 ? input : input[..split];
        if (!command.Equals("/send", StringComparison.OrdinalIgnoreCase) && !command.Equals("/receive", StringComparison.OrdinalIgnoreCase)) return false;
        string arguments = split < 0 ? "" : input[(split + 1)..].Trim();
        try
        {
            if (command.Equals("/send", StringComparison.OrdinalIgnoreCase))
            {
                int space = arguments.IndexOf(' ');
                if (space <= 0) throw new ArgumentException("/send @nickname 'C:\\path\\file.ext'");
                string target = arguments[..space].TrimStart('@');
                string path = arguments[(space + 1)..].Trim();
                if (path.Length >= 2 && (path[0] == '\'' && path[^1] == '\'' || path[0] == '"' && path[^1] == '"')) path = path[1..^1];
                Offer(target, path);
            }
            else
            {
                string[] values = arguments.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length is < 1 or > FileTransferProtocol.MaximumPending || values.Any(value => !Int32.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int number) || number <= 0)) throw new ArgumentException("/receive 1,2,3");
                foreach (int number in values.Select(value => Int32.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).Distinct())
                {
                    try { Receive(number); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
                    { Notify(Text("Файл: ", "File: ") + ex.Message); }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        { Notify(Text("Файл: ", "File: ") + ex.Message); }
        return true;
    }
    private void Offer(string target, string path)
    {
        if (!NetWorker.IsNicknameValid(target) || target.Equals(nickname(), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(Text("Неверный получатель.", "Invalid recipient."));
        string full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || info.Length > FileTransferProtocol.MaximumFileBytes) throw new IOException(Text("Файл недоступен или превышает 256 МиБ.", "File unavailable or exceeds 256 MiB."));
        string name = info.Name;
        if (!FileTransferProtocol.SafeName(name)) throw new IOException(Text("Недопустимое имя файла (максимум 120 символов).", "Unsafe filename (maximum 120 characters)."));
        lock (gate)
        {
            if (disposed) return;
            if (!available || !peers.Contains(target)) throw new IOException(Text("Получатель не подтвердил поддержку /send; обновите оба клиента и Hub.", "Recipient has not advertised /send support; update both clients and Hub."));
            if (transfers.Count >= FileTransferProtocol.MaximumPending) throw new IOException(Text("Слишком много передач (максимум 16).", "Too many transfers (maximum 16)."));
            var exchange = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var packet = new FileTransferPacket("offer", Guid.NewGuid().ToString("N"), target, Name: name, Size: info.Length, Key: Convert.ToBase64String(exchange.ExportSubjectPublicKeyInfo()));
            transfers.Add(packet.Id, new Transfer { Offer = packet with { Sender = nickname() }, Exchange = exchange, Source = full, SourceWrite = info.LastWriteTimeUtc, Expires = DateTimeOffset.UtcNow.AddMinutes(5) });
            Run(async () =>
            {
                try { await Send(packet).ConfigureAwait(false); }
                catch { lock (gate) { if (transfers.TryGetValue(packet.Id, out var failed)) Remove(failed); } throw; }
            });
        }
        Notify(Text("Запрос отправлен. Ожидание /receive у получателя.", "Offer sent. Waiting for the recipient's /receive."));
    }
    private void Receive(int number)
    {
        lock (gate)
        {
            if (disposed) return;
            Transfer transfer = transfers.Values.FirstOrDefault(t => t.Number == number) ?? throw new IOException(Text("Запрос не найден или истёк.", "Offer not found or expired."));
            if (transfer.File != null || transfer.Expires < DateTimeOffset.UtcNow) throw new IOException(Text("Запрос уже принят или истёк.", "Offer already accepted or expired."));
            if (transfers.Values.Count(t => t.File != null) >= 2 || transfers.Values.Where(t => t.File != null).Sum(t => t.Offer.Size) + transfer.Offer.Size > 512L * 1024 * 1024)
                throw new IOException(Text("Одновременно можно принимать два файла (всего до 512 МиБ).", "At most two incoming files (512 MiB total) may be accepted."));
            transfer.Expires = DateTimeOffset.UtcNow.AddMinutes(30);
            DirectoryInfo directory = new(desktop);
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(Text("Рабочий стол недоступен или является ссылкой.", "Desktop unavailable or is a link."));
            try
            {
                transfer.Exchange = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                transfer.Secret = Derive(transfer.Exchange, transfer.Offer.Key, transfer.Offer);
                transfer.Temporary = Path.Combine(desktop, ".tcptunnel-" + Guid.NewGuid().ToString("N") + ".partial");
                transfer.File = new FileStream(transfer.Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileTransferProtocol.ChunkBytes, FileOptions.SequentialScan);
                if (OperatingSystem.IsWindows()) System.IO.File.WriteAllText(transfer.Temporary + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n", Encoding.ASCII);
                transfer.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                string publicKey = Convert.ToBase64String(transfer.Exchange.ExportSubjectPublicKeyInfo());
                Run(() => Send(new("accept", transfer.Offer.Id, transfer.Offer.Sender, Key: publicKey)));
            }
            catch { Remove(transfer); throw; }
        }
    }
    internal async Task<bool> HandleAsync(string frame)
    {
        if (!FileTransferProtocol.IsControl(frame)) return false;
        if (!FileTransferProtocol.TryParse(frame, out var packet) || packet.Sender.Length == 0) return true;
        if (packet.Target.Length != 0 && !packet.Target.Equals(nickname(), StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            Transfer? start = null;
            lock (gate)
            {
                if (disposed || !available) return true;
                if (packet.Kind == "hello")
                {
                    if (!packet.Sender.Equals(nickname(), StringComparison.OrdinalIgnoreCase) && peers.Count < 64 && peers.Add(packet.Sender)) Run(() => Send(new("hello", Target: packet.Sender)));
                    return true;
                }
                if (packet.Kind == "offer")
                {
                    if (!peers.Contains(packet.Sender) || transfers.Count >= FileTransferProtocol.MaximumPending || transfers.ContainsKey(packet.Id)) return true;
                    var transfer = new Transfer { Offer = packet, Expires = DateTimeOffset.UtcNow.AddMinutes(5), Number = checked(++nextNumber) };
                    transfers.Add(packet.Id, transfer);
                    Notify(Text($"{transfer.Number}. @{packet.Sender} хочет отправить {packet.Name} ({packet.Size:N0} байт). Скачать на рабочий стол? /receive {transfer.Number}", $"{transfer.Number}. @{packet.Sender} offers {packet.Name} ({packet.Size:N0} bytes). Download to Desktop? /receive {transfer.Number}"));
                    return true;
                }
                if (!transfers.TryGetValue(packet.Id, out var item)) return true;
                string peer = item.Source == null ? item.Offer.Sender : item.Offer.Target;
                if (!peer.Equals(packet.Sender, StringComparison.OrdinalIgnoreCase)) return true;
                if (item.Expires < DateTimeOffset.UtcNow) { Remove(item); return true; }
                if (packet.Kind == "reject") { Remove(item); Notify(Text("Передача отклонена или недоступна.", "Transfer declined or unavailable.")); return true; }
                if (packet.Kind == "accept" && item.Source != null && !item.Sending)
                { item.Secret = Derive(item.Exchange!, packet.Key, item.Offer); item.Expires = DateTimeOffset.UtcNow.AddMinutes(30); item.Sending = true; start = item; }
                else if (item.Source == null && item.File != null && item.Secret != null)
                {
                    if (packet.Kind is not ("chunk" or "end")) throw new InvalidDataException("Unexpected file transfer state.");
                    if (packet.Sequence != item.Sequence) throw new InvalidDataException("Invalid file chunk sequence.");
                    byte[] data = Decrypt(item, packet);
                    try
                    {
                    if (packet.Kind == "chunk")
                    {
                        if (item.Received + data.Length > item.Offer.Size) throw new InvalidDataException("File exceeds its declared size.");
                        item.File.Write(data); item.Hash!.AppendData(data); item.Received += data.Length; item.Sequence++;
                    }
                    else if (packet.Kind == "end")
                    {
                        if (item.Received != item.Offer.Size || !CryptographicOperations.FixedTimeEquals(item.Hash!.GetHashAndReset(), data)) throw new InvalidDataException("File integrity check failed.");
                        item.File.Flush(true); item.File.Dispose(); item.File = null;
                        string final = UniqueDestination(item.Offer.Name);
                        System.IO.File.Move(item.Temporary!, final, false); item.Temporary = null; Remove(item);
                        Notify(Text("Файл сохранён: ", "File saved: ") + final);
                    }
                    }
                    finally { CryptographicOperations.ZeroMemory(data); }
                }
            }
            if (start != null) Run(() => SendFileAsync(start));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            lock (gate) { if (transfers.TryGetValue(packet.Id, out var failed)) Remove(failed); }
            Run(() => Send(new("reject", packet.Id, packet.Sender)));
            Notify(Text("Передача прервана: ", "Transfer stopped: ") + ex.Message);
        }
        await Task.CompletedTask; return true;
    }
    private async Task SendFileAsync(Transfer transfer)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, transfer.Cancellation.Token);
        bool acquired = false;
        byte[] buffer = new byte[FileTransferProtocol.ChunkBytes];
        try
        {
            await outgoing.WaitAsync(cancellation.Token).ConfigureAwait(false); acquired = true;
            var info = new FileInfo(transfer.Source!);
            if (!info.Exists || info.Length != transfer.Offer.Size || info.LastWriteTimeUtc != transfer.SourceWrite || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Source file changed after the offer.");
            using var file = new FileStream(transfer.Source!, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferProtocol.ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != transfer.Offer.Size) throw new IOException("Source file changed.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long sent = 0; long sequence = 0;
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow > transfer.Expires) throw new IOException("Transfer expired.");
                int count = await file.ReadAsync(buffer, cancellation.Token).ConfigureAwait(false);
                if (count == 0) break;
                if ((sent += count) > transfer.Offer.Size) throw new IOException("Source file grew.");
                hash.AppendData(buffer.AsSpan(0, count));
                await send(FileTransferProtocol.Encode(Encrypt(transfer, "chunk", sequence++, buffer.AsSpan(0, count))), cancellation.Token).ConfigureAwait(false);
                await Task.Delay(8, cancellation.Token).ConfigureAwait(false);
            }
            if (sent != transfer.Offer.Size) throw new IOException("Source file changed.");
            await send(FileTransferProtocol.Encode(Encrypt(transfer, "end", sequence, hash.GetHashAndReset())), cancellation.Token).ConfigureAwait(false);
            Notify(Text("Файл отправлен: ", "File sent: ") + transfer.Offer.Name);
        }
        catch
        {
            if (!stop.IsCancellationRequested) Run(() => Send(new("reject", transfer.Offer.Id, transfer.Offer.Target)));
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (acquired) outgoing.Release();
            lock (gate) Remove(transfer, true);
        }
    }
    private string UniqueDestination(string name)
    {
        string candidate = Path.Combine(desktop, name);
        for (int i = 1; System.IO.File.Exists(candidate) || Directory.Exists(candidate); i++)
        {
            if (i > 1000) throw new IOException("Cannot choose an unused destination.");
            candidate = Path.Combine(desktop, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
        }
        return candidate;
    }
    private static byte[] Derive(ECDiffieHellman local, string peer, FileTransferPacket offer)
    {
        using var remote = ECDiffieHellman.Create();
        byte[] publicKey = Convert.FromBase64String(peer);
        remote.ImportSubjectPublicKeyInfo(publicKey, out int consumed);
        if (consumed != publicKey.Length || remote.KeySize != 256 || remote.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) throw new CryptographicException("Invalid transfer key.");
        return local.DeriveKeyFromHash(remote.PublicKey, HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes("TCPTunnel.FILE1\0" + offer.Id), Context(offer));
    }
    private static byte[] Context(FileTransferPacket offer) => Encoding.UTF8.GetBytes(FileTransferProtocol.Encode(offer));
    private static byte[] Nonce(long sequence)
    {
        byte[] nonce = new byte[12]; BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), sequence); return nonce;
    }
    private static FileTransferPacket Encrypt(Transfer transfer, string kind, long sequence, ReadOnlySpan<byte> clear)
    {
        byte[] data = new byte[clear.Length + 16];
        using var cipher = new AesGcm(transfer.Secret!, 16);
        cipher.Encrypt(Nonce(sequence), clear, data.AsSpan(0, clear.Length), data.AsSpan(clear.Length), Aad(transfer.Offer, kind));
        return new(kind, transfer.Offer.Id, transfer.Offer.Target, Sequence: sequence, Data: Convert.ToBase64String(data));
    }
    private static byte[] Decrypt(Transfer transfer, FileTransferPacket packet)
    {
        byte[] data = Convert.FromBase64String(packet.Data);
        if (data.Length < 16) throw new InvalidDataException("Missing file authentication tag.");
        byte[] clear = new byte[data.Length - 16];
        using var cipher = new AesGcm(transfer.Secret!, 16);
        try { cipher.Decrypt(Nonce(packet.Sequence), data.AsSpan(0, clear.Length), data.AsSpan(clear.Length), clear, Aad(transfer.Offer, packet.Kind)); return clear; }
        catch { CryptographicOperations.ZeroMemory(clear); throw; }
    }
    private static byte[] Aad(FileTransferPacket offer, string kind) => Encoding.UTF8.GetBytes(kind + "\0" + FileTransferProtocol.Encode(offer));
    private void Run(Func<Task> action)
    {
        Task worker = Task.Run(async () =>
        {
            try { await action().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or ObjectDisposedException)
            { if (!stop.IsCancellationRequested) Notify(Text("Файл: ", "File: ") + ex.Message); }
        });
        workers[worker.Id] = worker;
        _ = worker.ContinueWith(done => workers.TryRemove(done.Id, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void Remove(Transfer transfer, bool completed = false)
    {
        transfers.Remove(transfer.Offer.Id);
        if (transfer.Sending && !completed) { transfer.Cancellation.Cancel(); return; }
        transfer.Dispose();
    }
    internal void RemovePeer(string name)
    {
        lock (gate)
        {
            peers.Remove(name);
            foreach (var transfer in transfers.Values.Where(t => t.Offer.Sender.Equals(name, StringComparison.OrdinalIgnoreCase) || t.Offer.Target.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray()) Remove(transfer);
        }
    }
    internal void RenamePeer(string previous)
    {
        RemovePeer(previous);
        lock (gate) { if (available && !disposed) Run(AdvertiseAsync); }
    }
    internal void Disable()
    {
        lock (gate)
        {
            peers.Clear();
            foreach (var transfer in transfers.Values.ToArray()) Remove(transfer);
        }
    }
    private void Expire()
    {
        lock (gate)
        {
            if (disposed) return;
            foreach (var transfer in transfers.Values.Where(t => t.Expires < DateTimeOffset.UtcNow && !t.Sending).ToArray()) Remove(transfer);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; expiry.Dispose(); stop.Cancel();
            notices.Writer.TryComplete();
            foreach (var transfer in transfers.Values.Where(t => !t.Sending).ToArray()) Remove(transfer);
            peers.Clear();
        }
        try { Task.WaitAll(workers.Values.ToArray(), TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}


