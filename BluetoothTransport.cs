using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Radios;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace TCPTunnel
{
    internal enum BluetoothAvailability { Available, UnsupportedOs, NoAdapter, RadioOff, RoleUnsupported, AccessDenied, Failed }

    internal static class BluetoothSupport
    {
        internal static readonly Guid ServiceUuid = new("2c72e9d1-fd0f-4a41-a4c7-9d1f94e38c75");
        private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);

        internal static RfcommServiceId ServiceId => RfcommServiceId.FromUuid(ServiceUuid);

        internal static bool IsOsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

        internal static BluetoothAvailability Check(bool hubRole)
        {
            if (!IsOsSupported)
                return BluetoothAvailability.UnsupportedOs;
            try
            {
                using var timeout = new CancellationTokenSource(ApiTimeout);
                return CheckAsync(hubRole, timeout.Token).GetAwaiter().GetResult();
            }
            catch (UnauthorizedAccessException) { return BluetoothAvailability.AccessDenied; }
            catch (Exception) { return BluetoothAvailability.Failed; }
        }

        private static async Task<BluetoothAvailability> CheckAsync(bool hubRole, CancellationToken token)
        {
            BluetoothAdapter adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(token).ConfigureAwait(false);
            if (adapter == null)
                return BluetoothAvailability.NoAdapter;
            if (!adapter.IsClassicSupported || !adapter.IsLowEnergySupported ||
                (hubRole ? !adapter.IsPeripheralRoleSupported : !adapter.IsCentralRoleSupported))
                return BluetoothAvailability.RoleUnsupported;
            Radio radio = await adapter.GetRadioAsync().AsTask(token).ConfigureAwait(false);
            if (radio == null)
                return BluetoothAvailability.NoAdapter;
            return radio.State == RadioState.On ? BluetoothAvailability.Available : BluetoothAvailability.RadioOff;
        }

        internal static TextId Describe(BluetoothAvailability availability) => availability switch
        {
            BluetoothAvailability.UnsupportedOs => TextId.BluetoothUnsupportedOs,
            BluetoothAvailability.NoAdapter => TextId.BluetoothNoAdapter,
            BluetoothAvailability.RadioOff => TextId.BluetoothRadioOff,
            BluetoothAvailability.RoleUnsupported => TextId.BluetoothRoleUnsupported,
            BluetoothAvailability.AccessDenied => TextId.BluetoothAccessDenied,
            BluetoothAvailability.Failed => TextId.BluetoothFailed,
            _ => TextId.BluetoothReady
        };

        internal static string FormatAddress(ulong address) => address.ToString("X12");
    }

    internal sealed class BluetoothChatConnection : IChatConnection
    {
        private readonly StreamSocket socket;
        private readonly IDisposable device;
        private readonly IDisposable service;
        private int disposed;

        public Stream Stream { get; }
        public IPAddress RemoteIpAddress => null;
        public ChatTransport Transport => ChatTransport.Bluetooth;
        internal ulong RemoteAddress { get; }

        internal BluetoothChatConnection(StreamSocket socket, ulong remoteAddress, IDisposable device = null, IDisposable service = null)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            this.device = device;
            this.service = service;
            RemoteAddress = remoteAddress;
            Stream = new DuplexStream(socket.InputStream.AsStreamForRead(), socket.OutputStream.AsStreamForWrite());
        }

        internal static ulong ParseRemoteAddress(StreamSocket socket)
        {
            try
            {
                string text = socket.Information.RemoteAddress?.CanonicalName ?? socket.Information.RemoteHostName?.CanonicalName ?? "";
                string hex = new string(text.Where(Uri.IsHexDigit).ToArray());
                return hex.Length == 12 ? Convert.ToUInt64(hex, 16) : 0;
            }
            catch (Exception) { return 0; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            try { Stream.Dispose(); } catch { }
            try { socket.Dispose(); } catch { }
            try { service?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
        }
    }

    internal enum BluetoothHubStartStatus { Started, AdvertisingBlockedByPolicy, Failed }

    internal sealed class BluetoothHubHost : IDisposable
    {
        private static readonly TimeSpan PublisherStartTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan BeaconRefreshInterval = TimeSpan.FromSeconds(2);

        private readonly object gate = new object();
        private readonly StreamSocketListener listener = new StreamSocketListener();
        private readonly Action<IChatConnection> admit;
        private readonly Func<int> participantCount;
        private readonly BluetoothHubMode mode;
        private readonly string nickname;
        private readonly uint instance;
        private RfcommServiceProvider provider;
        private BluetoothLEAdvertisementPublisher publisher;
        private Timer refreshTimer;
        private ulong address;
        private int advertisedCount = -1;
        private int refreshing;
        private bool disposed;

        private BluetoothHubHost(BluetoothHubMode mode, string nickname, Action<IChatConnection> admit, Func<int> participantCount)
        {
            this.mode = mode;
            this.nickname = nickname;
            this.admit = admit;
            this.participantCount = participantCount;
            uint value;
            do { value = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)); } while (value == 0);
            instance = value;
        }

        internal static BluetoothHubStartStatus TryStart(
            BluetoothHubMode mode,
            string nickname,
            Action<IChatConnection> admit,
            Func<int> participantCount,
            out BluetoothHubHost host,
            out string error)
        {
            host = null;
            var candidate = new BluetoothHubHost(mode, nickname, admit, participantCount);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                (BluetoothHubStartStatus status, string message) = candidate.StartAsync(timeout.Token).GetAwaiter().GetResult();
                error = message;
                if (status != BluetoothHubStartStatus.Started)
                {
                    candidate.Dispose();
                    return status;
                }
                host = candidate;
                return status;
            }
            catch (Exception ex)
            {
                candidate.Dispose();
                error = ex.Message;
                return BluetoothHubStartStatus.Failed;
            }
        }

        private async Task<(BluetoothHubStartStatus, string)> StartAsync(CancellationToken token)
        {
            BluetoothAdapter adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(token).ConfigureAwait(false);
            if (adapter == null)
                return (BluetoothHubStartStatus.Failed, Lang.Get(TextId.BluetoothNoAdapter));
            address = adapter.BluetoothAddress;

            listener.ConnectionReceived += OnConnectionReceived;
            provider = await RfcommServiceProvider.CreateAsync(BluetoothSupport.ServiceId).AsTask(token).ConfigureAwait(false);
            await listener.BindServiceNameAsync(BluetoothSupport.ServiceId.AsString(), SocketProtectionLevel.PlainSocket)
                .AsTask(token).ConfigureAwait(false);
            provider.StartAdvertising(listener, false);

            int count = participantCount();
            BluetoothError publishError = await PublishAsync(count, token).ConfigureAwait(false);
            if (publishError == BluetoothError.DisabledByPolicy)
                return (BluetoothHubStartStatus.AdvertisingBlockedByPolicy, publishError.ToString());
            if (publishError != BluetoothError.Success)
                return (BluetoothHubStartStatus.Failed, publishError.ToString());

            refreshTimer = new Timer(_ => RefreshBeacon(), null, BeaconRefreshInterval, BeaconRefreshInterval);
            return (BluetoothHubStartStatus.Started, null);
        }

        private async Task<BluetoothError> PublishAsync(int count, CancellationToken token)
        {
            var next = new BluetoothLEAdvertisementPublisher();
            using (var writer = new DataWriter())
            {
                writer.WriteBytes(HubBeaconCodec.Encode(address, instance, mode, count, nickname));
                next.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(HubBeaconCodec.CompanyId, writer.DetachBuffer()));
            }

            var outcome = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(BluetoothLEAdvertisementPublisher sender, BluetoothLEAdvertisementPublisherStatusChangedEventArgs args)
            {
                if (args.Status == BluetoothLEAdvertisementPublisherStatus.Started)
                    outcome.TrySetResult(BluetoothError.Success);
                else if (args.Status == BluetoothLEAdvertisementPublisherStatus.Aborted)
                    outcome.TrySetResult(args.Error == BluetoothError.Success ? BluetoothError.OtherError : args.Error);
            }

            next.StatusChanged += Changed;
            BluetoothLEAdvertisementPublisher previous;
            lock (gate)
            {
                if (disposed)
                    return BluetoothError.OtherError;
                previous = publisher;
                publisher = next;
            }
            try { previous?.Stop(); } catch { }
            next.Start();
            BluetoothError result;
            try
            {
                result = await outcome.Task.WaitAsync(PublisherStartTimeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                result = BluetoothError.OtherError;
            }
            finally
            {
                next.StatusChanged -= Changed;
            }
            if (result == BluetoothError.Success)
                advertisedCount = count;
            return result;
        }

        private void RefreshBeacon()
        {
            if (Interlocked.Exchange(ref refreshing, 1) != 0)
                return;
            try
            {
                int count = participantCount();
                if (count == advertisedCount)
                    return;
                using var timeout = new CancellationTokenSource(PublisherStartTimeout + TimeSpan.FromSeconds(1));
                PublishAsync(count, timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception) { }
            finally { Volatile.Write(ref refreshing, 0); }
        }

        private void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            StreamSocket socket = args.Socket;
            bool accept;
            lock (gate)
                accept = !disposed;
            if (!accept)
            {
                socket.Dispose();
                return;
            }
            BluetoothChatConnection connection;
            try
            {
                connection = new BluetoothChatConnection(socket, BluetoothChatConnection.ParseRemoteAddress(socket));
            }
            catch (Exception)
            {
                socket.Dispose();
                return;
            }
            admit(connection);
        }

        public void Dispose()
        {
            BluetoothLEAdvertisementPublisher toStop;
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                toStop = publisher;
                publisher = null;
            }
            refreshTimer?.Dispose();
            try { toStop?.Stop(); } catch { }
            try { provider?.StopAdvertising(); } catch { }
            listener.ConnectionReceived -= OnConnectionReceived;
            try { listener.Dispose(); } catch { }
        }
    }

    internal static class BluetoothConnector
    {
        internal static BluetoothChatConnection Connect(ulong address, out string error)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                BluetoothChatConnection connection = ConnectAsync(address, timeout.Token).GetAwaiter().GetResult();
                error = null;
                return connection;
            }
            catch (OperationCanceledException)
            {
                error = Lang.Get(TextId.ConnectionTimedOut);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return null;
        }

        private static async Task<BluetoothChatConnection> ConnectAsync(ulong address, CancellationToken token)
        {
            BluetoothDevice device = await BluetoothDevice.FromBluetoothAddressAsync(address).AsTask(token).ConfigureAwait(false)
                ?? throw new IOException(Lang.Get(TextId.BluetoothHubGone));
            RfcommDeviceServicesResult services = null;
            RfcommDeviceService selected = null;
            StreamSocket socket = null;
            try
            {
                services = await device.GetRfcommServicesForIdAsync(BluetoothSupport.ServiceId, BluetoothCacheMode.Uncached)
                    .AsTask(token).ConfigureAwait(false);
                if (services.Error != BluetoothError.Success || services.Services.Count == 0)
                    throw new IOException(Lang.Get(TextId.BluetoothHubGone));
                selected = services.Services[0];
                socket = new StreamSocket();
                await socket.ConnectAsync(selected.ConnectionHostName, selected.ConnectionServiceName, SocketProtectionLevel.PlainSocket)
                    .AsTask(token).ConfigureAwait(false);
                var connection = new BluetoothChatConnection(socket, address, device, selected);
                foreach (RfcommDeviceService other in services.Services)
                    if (!ReferenceEquals(other, selected)) other.Dispose();
                return connection;
            }
            catch
            {
                socket?.Dispose();
                if (services != null)
                    foreach (RfcommDeviceService service in services.Services) service.Dispose();
                device.Dispose();
                throw;
            }
        }
    }

    internal sealed class DiscoveredHub
    {
        internal HubBeacon Beacon { get; init; }
        internal int SignalDbm { get; init; }
        internal ulong Address => Beacon.Address;
        internal uint Instance => Beacon.Instance;
    }

    internal sealed class BluetoothHubScanner : IDisposable
    {
        private const int MaxEntries = 64;
        private const double SmoothingFactor = 0.3;
        private static readonly TimeSpan EntryLifetime = TimeSpan.FromSeconds(8);

        private readonly object gate = new object();
        private readonly Dictionary<(ulong Address, uint Instance), (HubBeacon Beacon, double Rssi, long Seen)> entries = new();
        private readonly BluetoothLEAdvertisementWatcher watcher;
        private bool stopped;

        internal BluetoothError? Failure { get; private set; }
        internal int Version { get; private set; }

        internal BluetoothHubScanner()
        {
            watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
            watcher.Received += OnReceived;
            watcher.Stopped += OnStopped;
        }

        internal void Start() => watcher.Start();

        internal IReadOnlyList<DiscoveredHub> Snapshot()
        {
            lock (gate)
            {
                RemoveStaleLocked(Stopwatch.GetTimestamp());
                return entries.Values
                    .Select(entry => new DiscoveredHub { Beacon = entry.Beacon, SignalDbm = (int)Math.Round(entry.Rssi) })
                    .OrderByDescending(hub => hub.SignalDbm)
                    .ThenBy(hub => hub.Address)
                    .ToArray();
            }
        }

        internal int? GetSignal(ulong address)
        {
            lock (gate)
            {
                long now = Stopwatch.GetTimestamp();
                RemoveStaleLocked(now);
                double? best = null;
                foreach (var entry in entries)
                    if (entry.Key.Address == address && (best == null || entry.Value.Rssi > best))
                        best = entry.Value.Rssi;
                return best.HasValue ? (int)Math.Round(best.Value) : null;
            }
        }

        private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.RawSignalStrengthInDBm == 127)
                return;
            foreach (BluetoothLEManufacturerData data in args.Advertisement.ManufacturerData)
            {
                if (data.CompanyId != HubBeaconCodec.CompanyId || data.Data.Length != HubBeaconCodec.Length)
                    continue;
                byte[] bytes = new byte[HubBeaconCodec.Length];
                using (DataReader reader = DataReader.FromBuffer(data.Data))
                    reader.ReadBytes(bytes);
                if (!HubBeaconCodec.TryDecode(bytes, out HubBeacon beacon))
                    continue;

                lock (gate)
                {
                    long now = Stopwatch.GetTimestamp();
                    RemoveStaleLocked(now);
                    var key = (beacon.Address, beacon.Instance);
                    if (entries.TryGetValue(key, out var existing))
                    {
                        double smoothed = existing.Rssi + SmoothingFactor * (args.RawSignalStrengthInDBm - existing.Rssi);
                        entries[key] = (beacon, smoothed, now);
                    }
                    else if (entries.Count < MaxEntries)
                    {
                        entries[key] = (beacon, args.RawSignalStrengthInDBm, now);
                    }
                    else
                    {
                        continue;
                    }
                    Version++;
                }
            }
        }

        private void RemoveStaleLocked(long now)
        {
            List<(ulong, uint)> stale = null;
            foreach (var entry in entries)
                if (Stopwatch.GetElapsedTime(entry.Value.Seen, now) > EntryLifetime)
                    (stale ??= new List<(ulong, uint)>()).Add(entry.Key);
            if (stale == null)
                return;
            foreach (var key in stale)
                entries.Remove(key);
            Version++;
        }

        private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            lock (gate)
            {
                if (!stopped && args.Error != BluetoothError.Success)
                {
                    Failure = args.Error;
                    Version++;
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
                stopped = true;
            try { watcher.Stop(); } catch { }
            watcher.Received -= OnReceived;
            watcher.Stopped -= OnStopped;
        }
    }
}
