using TCPTunnel;

internal static class BluetoothHardwareSmoke
{
    internal static int Run()
    {
        BluetoothAvailability availability = BluetoothSupport.Check(true);
        Console.WriteLine("Hub role availability: " + availability);
        if (availability != BluetoothAvailability.Available)
            return 1;

        int participants = 3;
        int admitted = 0;
        BluetoothHubStartStatus status = BluetoothHubHost.TryStart(
            BluetoothHubMode.LanAndBluetooth,
            "smokeHubНик",
            connection => { Interlocked.Increment(ref admitted); connection.Dispose(); },
            () => Volatile.Read(ref participants),
            out BluetoothHubHost? host,
            out string error);
        Console.WriteLine("Start: " + status + (error == null ? "" : " (" + error + ")"));
        if (status != BluetoothHubStartStatus.Started || host == null)
            return 2;

        using (host)
        {
            Thread.Sleep(1000);
            Volatile.Write(ref participants, 5);
            Thread.Sleep(3500);
        }
        Console.WriteLine("Beacon republished with a new participant count and stopped cleanly; admitted connections: " + admitted);
        return 0;
    }
}
