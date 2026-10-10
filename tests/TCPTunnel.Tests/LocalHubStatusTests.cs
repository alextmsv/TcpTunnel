using TCPTunnel;

internal static class LocalHubStatusTests
{
    public static bool LocalHubChangesSelfTest()
    {
        var monitor = new LocalHubStatusMonitor();
        var stopped = new LocalHubSnapshot(false, 0, "none", 0);
        var running = new LocalHubSnapshot(true, 9091, "UPnP active", 1);
        if (monitor.Observe(stopped, false).Count != 0 || monitor.Observe(running, false).Count == 0 ||
            monitor.Observe(running, false).Count != 0) return false;
        if (monitor.Observe(running with { Participants = 2 }, true).Count != 0) return false;
        if (monitor.Observe(running with { Participants = 3 }, false).Count != 1) return false;
        if (monitor.Observe(running with { Participants = 3, Nat = "failed" }, true).Count != 1) return false;
        return monitor.Observe(stopped, false).Count == 1 && monitor.Observe(stopped, false).Count == 0;
    }
}
