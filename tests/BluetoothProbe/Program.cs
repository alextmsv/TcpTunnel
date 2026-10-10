using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

if (args.SequenceEqual(new[] { "--self-test" })) return await ProbeTests.RunAsync();
if (args.Length > 0 && args[0] is "--host" or "--scan" or "--connect") return await RadioTransferProbe.RunAsync(args);
if (args.Length > 0 && !args.SequenceEqual(new[] { "--advertise" }))
{
    Console.Error.WriteLine("Usage: BluetoothProbe [--advertise | --self-test | --host [seconds [clients]] | --scan [seconds] | --connect <12-hex-address>]");
    return 1;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
var report = new Dictionary<string, object?>
{
    ["UnpairedTransfer"] = "NOT_TESTED: requires a second Windows device",
    ["Mesh"] = "NOT_TESTED: requires at least three Windows devices"
};
try
{
    BluetoothAdapter? adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(timeout.Token);
    if (adapter is null) throw new InvalidOperationException("No default Bluetooth adapter");
    var radio = await adapter.GetRadioAsync().AsTask(timeout.Token);
    report["RadioState"] = radio.State.ToString();
    report["LowEnergy"] = adapter.IsLowEnergySupported;
    report["Classic"] = adapter.IsClassicSupported;
    report["CentralRole"] = adapter.IsCentralRoleSupported;
    report["PeripheralRole"] = adapter.IsPeripheralRoleSupported;
    report["ExtendedAdvertising"] = adapter.IsExtendedAdvertisingSupported;
    if (args.Contains("--advertise", StringComparer.Ordinal))
    {
        var publisher = new BluetoothLEAdvertisementPublisher();
        using (var writer = new DataWriter())
        {
            writer.WriteBytes(Encoding.ASCII.GetBytes("TCPTunnelProbe"));
            publisher.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(0xffff, writer.DetachBuffer()));
        }
        var status = new TaskCompletionSource<BluetoothLEAdvertisementPublisherStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(BluetoothLEAdvertisementPublisher sender, BluetoothLEAdvertisementPublisherStatusChangedEventArgs change)
        {
            if (change.Status is BluetoothLEAdvertisementPublisherStatus.Started or BluetoothLEAdvertisementPublisherStatus.Aborted)
                status.TrySetResult(change);
        }
        publisher.StatusChanged += Changed;
        try
        {
            publisher.Start();
            var change = await status.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            report["BlePublisher"] = change.Status.ToString();
            report["BleError"] = change.Error.ToString();
        }
        catch (Exception ex) { report["BlePublisher"] = ex.Message; }
        finally { publisher.Stop(); publisher.StatusChanged -= Changed; }

        using var listener = new StreamSocketListener();
        RfcommServiceProvider? provider = null;
        try
        {
            var id = RfcommServiceId.FromUuid(new Guid("f0829a7e-9731-4ebc-9479-3edb47aef8ab"));
            provider = await RfcommServiceProvider.CreateAsync(id).AsTask(timeout.Token);
            await listener.BindServiceNameAsync(id.AsString(), SocketProtectionLevel.PlainSocket).AsTask(timeout.Token);
            provider.StartAdvertising(listener, false);
            report["RfcommPlainListener"] = "STARTED (local API only; no remote connection tested)";
        }
        catch (Exception ex) { report["RfcommPlainListener"] = ex.Message; }
        finally { provider?.StopAdvertising(); }
    }
}
catch (Exception ex) { report["Error"] = ex.Message; }
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
if (report.ContainsKey("Error")) return 1;
bool advertisingStarted = Equals(report.GetValueOrDefault("BlePublisher"), "Started") &&
    ((string?)report.GetValueOrDefault("RfcommPlainListener"))?.StartsWith("STARTED", StringComparison.Ordinal) == true;
return args.Contains("--advertise", StringComparer.Ordinal) && !advertisingStarted ? 2 : 0;
