using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

internal static class RadioTransferProbe
{
    private static readonly RfcommServiceId Service = RfcommServiceId.FromUuid(new Guid("f0829a7e-9731-4ebc-9479-3edb47aef8ab"));

    internal static async Task<int> RunAsync(string[] args)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args[0] == "--connect")
            {
                if (args.Length != 2 || args[1].Length != 12 || !ulong.TryParse(args[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address) || address == 0)
                    throw new ArgumentException("Use --connect followed by the 12-hex address from the agreed host's scan result.");
                stop.CancelAfter(TimeSpan.FromSeconds(60));
                await ConnectAsync(address, stop.Token);
            }
            else
            {
                bool host = args[0] == "--host";
                int seconds = host ? 60 : 15;
                int clients = 1;
                if (args.Length > (host ? 3 : 2) || (args.Length >= 2 && (!int.TryParse(args[1], out seconds) || seconds < 1 || seconds > 300)))
                    throw new ArgumentException("Duration must be 1..300 seconds.");
                if (args.Length == 3 && (!int.TryParse(args[2], out clients) || clients < 1 || clients > 7))
                    throw new ArgumentException("Client count must be 1..7.");
                if (host)
                {
                    stop.CancelAfter(TimeSpan.FromSeconds(seconds));
                    await HostAsync(clients, stop.Token);
                }
                else await ScanAsync(seconds, stop.Token);
            }
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("CANCELLED/TIMEOUT: remote transfer was not proven."); return 3; }
        catch (Exception ex) { Console.Error.WriteLine($"FAILED: {ex.GetType().Name}, 0x{ex.HResult:X8}: {ex.Message}"); return 2; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static async Task HostAsync(int clients, CancellationToken token)
    {
        using var lifecycle = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var listener = new StreamSocketListener();
        var pending = Channel.CreateBounded<StreamSocket>(clients);
        var sockets = new List<StreamSocket>(clients);
        void Accepted(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            if (!pending.Writer.TryWrite(args.Socket)) args.Socket.Dispose();
        }
        listener.ConnectionReceived += Accepted;
        RfcommServiceProvider? provider = null;
        var publisher = new BluetoothLEAdvertisementPublisher();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(BluetoothLEAdvertisementPublisher sender, BluetoothLEAdvertisementPublisherStatusChangedEventArgs state)
        {
            if (state.Status == BluetoothLEAdvertisementPublisherStatus.Started) started.TrySetResult();
            else if (state.Status == BluetoothLEAdvertisementPublisherStatus.Aborted)
            {
                started.TrySetException(new IOException("BLE advertising: " + state.Error));
                lifecycle.Cancel();
            }
        }
        publisher.StatusChanged += Changed;
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(token) ?? throw new IOException("No Bluetooth adapter");
            ulong instance;
            do { instance = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)); } while (instance == 0);
            using (var writer = new DataWriter())
            {
                writer.WriteBytes(ProbeBeacon.Encode(adapter.BluetoothAddress, instance));
                publisher.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(0xffff, writer.DetachBuffer()));
            }
            provider = await RfcommServiceProvider.CreateAsync(Service).AsTask(token);
            await listener.BindServiceNameAsync(Service.AsString(), SocketProtectionLevel.PlainSocket).AsTask(token);
            provider.StartAdvertising(listener, false);
            publisher.Start();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Console.WriteLine($"READY classic-address={adapter.BluetoothAddress:X12}; instance={instance:X16}; waiting for {clients} probe client(s). Ctrl+C cancels.");
            while (sockets.Count < clients)
            {
                var accepted = await pending.Reader.ReadAsync(lifecycle.Token);
                sockets.Add(accepted);
                Console.WriteLine($"CLIENT {sockets.Count}/{clients} CONNECTED: {RemoteName(accepted)}");
            }
            pending.Writer.TryComplete();
            if (clients > 1)
            {
                int distinct = sockets.Select(RemoteName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                Console.WriteLine($"ALL {clients} CLIENTS CONNECTED SIMULTANEOUSLY ({distinct} distinct remote address(es)); serving in parallel.");
            }
            await Task.WhenAll(sockets.Select((socket, index) => ServeClientAsync(socket, index + 1, lifecycle.Token)));
            Console.WriteLine($"TRANSFER COMPLETE: {clients} client(s), 262144 bytes echoed each. Verify every client's unpaired result.");
        }
        finally
        {
            pending.Writer.TryComplete();
            listener.ConnectionReceived -= Accepted;
            while (pending.Reader.TryRead(out var extra)) extra.Dispose();
            foreach (var socket in sockets) socket.Dispose();
            publisher.Stop(); publisher.StatusChanged -= Changed;
            provider?.StopAdvertising();
        }
    }

    private static async Task ServeClientAsync(StreamSocket socket, int number, CancellationToken token)
    {
        try
        {
            using var input = socket.InputStream.AsStreamForRead();
            using var output = socket.OutputStream.AsStreamForWrite();
            await ProbeWire.ServeAsync(input, output, token);
            Console.WriteLine($"CLIENT {number} DONE: {RemoteName(socket)}; protection={socket.Information.ProtectionLevel}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"Client {number} ({RemoteName(socket)}): {ex.Message}", ex);
        }
    }

    private static string RemoteName(StreamSocket socket)
    {
        try { return socket.Information.RemoteHostName?.CanonicalName ?? socket.Information.RemoteAddress?.CanonicalName ?? "unknown"; }
        catch { return "unknown"; }
    }

    private static async Task ConnectAsync(ulong address, CancellationToken token)
    {
        using var device = await BluetoothDevice.FromBluetoothAddressAsync(address).AsTask(token) ?? throw new IOException("Bluetooth device unavailable");
        bool pairedBefore = device.DeviceInformation.Pairing.IsPaired;
        if (pairedBefore) throw new IOException("This pair is already paired; use an unpaired test pair. No pairing is removed automatically.");
        var services = await device.GetRfcommServicesForIdAsync(Service, BluetoothCacheMode.Uncached).AsTask(token);
        try
        {
            if (services.Error != BluetoothError.Success || services.Services.Count != 1)
                throw new IOException($"Probe service unavailable: {services.Error}; services={services.Services.Count}");
            var service = services.Services[0];
            using var socket = new StreamSocket();
            await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName, SocketProtectionLevel.PlainSocket).AsTask(token);
            using var input = socket.InputStream.AsStreamForRead();
            using var output = socket.OutputStream.AsStreamForWrite();
            var elapsed = Stopwatch.StartNew();
            await ProbeWire.ExchangeAsync(input, output, token);
            var after = await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(device.DeviceInformation.Id).AsTask(token);
            if (after.Pairing.IsPaired) throw new IOException("Transfer completed but the devices became paired; unpaired requirement FAILED.");
            Console.WriteLine($"PASS UNPAIRED EXCHANGE: 262144 bytes each direction; exact echo + SHA256 verified; {elapsed.ElapsedMilliseconds} ms; protection={socket.Information.ProtectionLevel}; pairedBefore=false; pairedAfter=false.");
        }
        finally { foreach (var service in services.Services) service.Dispose(); }
    }

    private static async Task ScanAsync(int seconds, CancellationToken token)
    {
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        var found = new Dictionary<ProbeBeacon, (short Rssi, long Seen)>();
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object gate = new();
        void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            foreach (var data in args.Advertisement.ManufacturerData)
            {
                if (data.CompanyId != 0xffff || data.Data.Length != 19) continue;
                using var reader = DataReader.FromBuffer(data.Data);
                byte[] bytes = new byte[19]; reader.ReadBytes(bytes);
                if (!ProbeBeacon.TryDecode(bytes, out var beacon) || args.RawSignalStrengthInDBm == 127) continue;
                lock (gate)
                {
                    long now = Stopwatch.GetTimestamp();
                    foreach (var stale in found.Where(pair => Stopwatch.GetElapsedTime(pair.Value.Seen, now).TotalSeconds > 8).Select(pair => pair.Key).ToArray()) found.Remove(stale);
                    if (found.Count < 64 || found.ContainsKey(beacon)) found[beacon] = (args.RawSignalStrengthInDBm, now);
                }
            }
        }
        void Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            if (args.Error != BluetoothError.Success) failure.TrySetException(new IOException("BLE scan: " + args.Error));
        }
        watcher.Received += Received; watcher.Stopped += Stopped;
        try
        {
            Console.WriteLine($"Scanning TCPTunnel test beacons for {seconds} seconds; no automatic connection.");
            watcher.Start();
            Task duration = Task.Delay(TimeSpan.FromSeconds(seconds), token);
            await await Task.WhenAny(duration, failure.Task);
            lock (gate)
            {
                var live = found.Where(pair => Stopwatch.GetElapsedTime(pair.Value.Seen).TotalSeconds <= 8).OrderByDescending(pair => pair.Value.Rssi).ToArray();
                foreach (var pair in live)
                    Console.WriteLine($"address={pair.Key.Address:X12}; instance={pair.Key.Instance:X16}; RSSI={pair.Value.Rssi} dBm");
                if (live.Length == 0) Console.WriteLine("NO MATCHING BEACONS. Remote discovery/transfer NOT_PROVEN.");
            }
        }
        finally { watcher.Stop(); watcher.Received -= Received; watcher.Stopped -= Stopped; }
    }
}
