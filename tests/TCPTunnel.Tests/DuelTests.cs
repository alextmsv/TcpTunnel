using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using TCPTunnel;

internal static class DuelTests
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);

    internal static bool DuelCommandSelfTest() =>
        DuelProtocol.TryCommand("/duel @pushkin hard 2m blitz", out var request) &&
        request is { Peer: "pushkin", Level: "hard", Minutes: 2, Blitz: true } &&
        DuelProtocol.TryCommand("/duel accept", out var accept) && accept.Kind == "accept" &&
        DuelProtocol.TryCommand("/duel practice", out var defaults) && defaults is { Kind: "practice", Level: "normal", Minutes: 1 } &&
        DuelProtocol.TryCommand("/duel practice hard 2m", out var practice) && practice is { Kind: "practice", Level: "hard", Minutes: 2 } &&
        !DuelProtocol.TryCommand("/duel practice hard 2m blitz", out _) &&
        !DuelProtocol.TryCommand("/duel @pushkin hard 2s", out _) &&
        !DuelProtocol.TryCommand("/duel @pushkin hard 16m", out _) &&
        !DuelProtocol.TryCommand("/duel @pushkin hard 2m other", out _) &&
        DuelProtocol.Hits(112, 100, 100, 100, 12) && !DuelProtocol.Hits(113, 100, 100, 100, 12);

    internal static bool DuelPracticeRoundSelfTest()
    {
        var id = Guid.NewGuid();
        var round = new DuelPracticeRound(new("practice", id, Level: "hard", Minutes: 1));
        if (!round.Advance(0) || round.Target is not { } target) return false;
        var shot = target with { Kind = "shot" };
        if (round.Shoot(shot with { Id = Guid.Empty }, 100) ||
            !round.Shoot(shot, 100) || round.Shoot(shot, 101) || round.Advance(200) || round.Score != 1) return false;
        if (!round.Advance(850) || round.Target is not { } next || round.Shoot(shot, 900) ||
            round.Shoot(next with { Kind = "shot" }, 1490)) return false;
        round.Advance(60000);
        return round.Target == null && round.Score == 1;
    }

    internal static bool DuelPracticeRecordSelfTest()
    {
        string path = Path.GetTempFileName();
        Guid identity = Guid.NewGuid();
        var profile = new ApplicationSettings.PracticeProfile(path, identity);
        try
        {
            File.WriteAllLines(path, new[] { "; keep me", "identityId=" + identity.ToString("N"), "customSetting=unchanged" });
            if (ApplicationSettings.ReadPracticeRecord(profile, "hard", 2) != 0 ||
                ApplicationSettings.SavePracticeRecord(profile, "hard", 2, 34) != (34, true) ||
                ApplicationSettings.SavePracticeRecord(profile, "hard", 2, 19) != (34, true) ||
                ApplicationSettings.SavePracticeRecord(profile, "easy", 2, 10) != (10, true) ||
                ApplicationSettings.ReadPracticeRecord(profile, "hard", 1) != 0 ||
                ApplicationSettings.ReadPracticeRecord(profile, "hard", 2) != 34 ||
                ApplicationSettings.SavePracticeRecord(profile with { Identity = Guid.NewGuid() }, "hard", 2, 50).Saved ||
                ApplicationSettings.SavePracticeRecord(profile, "hard", 2, Int32.MaxValue).Saved) return false;
            if (ApplicationSettings.SavePracticeStats(profile, "hard", 2, -1, 0, 0).Record.Points != -1 ||
                ApplicationSettings.SavePracticeStats(profile, "hard", 2, 28, 18, 12).Record != new ApplicationSettings.PracticeStats(28, 34, 12) ||
                ApplicationSettings.ReadPracticeStats(profile, "hard", 2) != new ApplicationSettings.PracticeStats(28, 34, 12)) return false;
            return File.ReadAllLines(path).Contains("; keep me") && File.ReadAllLines(path).Contains("customSetting=unchanged");
        }
        finally { File.Delete(path); }
    }

    internal static Task<bool> PlainDuelTcpSelfTest() => TestTcpAsync(false);
    internal static Task<bool> BlitzLatencyCompensationSelfTest() => TestTcpAsync(true);
    internal static Task<bool> ConfiguredMinefieldSelfTest() => TestTcpAsync(false, true);

    private static async Task<bool> TestTcpAsync(bool compensate, bool configured = false)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var alice = new TcpClient();
        using var bob = new TcpClient();
        var tasks = new List<Task>();
        async Task Join(TcpClient remote, string name)
        {
            await remote.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, stop.Token);
            var client = new Client(await listener.AcceptTcpClientAsync(stop.Token));
            NetWorker.broadcaster.AddConnection(client);
            tasks.Add(NetWorker.ServerClientLoopAsync(client, stop.Token));
            if (await MessageProtocol.ReadStringAsync(remote.GetStream(), stop.Token) != NetWorker.DO_AUTH_MESSAGE) throw new IOException("Auth prompt missing");
            await MessageProtocol.WriteStringAsync(remote.GetStream(), "REPLY:" + name, stop.Token);
            if (await MessageProtocol.ReadStringAsync(remote.GetStream(), stop.Token) != NetWorker.AUTH_OK_MESSAGE) throw new IOException("Auth failed");
        }
        try
        {
            await Join(alice, "testAlice");
            await Join(bob, "testBob");
            if (configured) return await TCPTunnel.Cryptography.SecureIntegrationTests.CheckDuelOptionsAsync(alice.GetStream(), bob.GetStream(), stop.Token, false);
            return await TCPTunnel.Cryptography.SecureIntegrationTests.CheckDuelAsync(alice.GetStream(), bob.GetStream(), stop.Token,
                compensate, !compensate, compensate ? 100 : 0);
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(tasks);
        }
    }

    internal static bool DuelArenaOptionsSelfTest()
    {
        var rules = new DuelPacket("offer", Guid.NewGuid(), Level:"hard", Minutes:2, Version:2, Mode:"moving", Size:"any");
        if (!DuelProtocol.ValidOptions(rules) || DuelProtocol.ValidOptions(rules with { Mode="minefield", Count=60 }) ||
            !DuelProtocol.TryCommand("/duel @pushkin",out var setup) || setup.Kind!="setup" ||
            DuelSound.Step(11)!=11 || DuelSound.Step(12)!=1 || DuelSound.Wave(11).Length<=DuelSound.Wave(10).Length) return false;
        foreach (int motion in new[] { 1, 2, 3 })
        foreach (int size in new[] { 12, 21, 30 })
        foreach (int age in new[] { 0, 100, 300, 639 })
        {
            var target = new DuelPacket("target", X:32,Y:160,Radius:size,Motion:motion,Seed:155);
            var p = DuelArena.Position(target,"hard",age);
            if (p.X<size || p.X>DuelProtocol.Width-size || p.Y<110+size || p.Y>DuelProtocol.Height-size) return false;
        }
        return DuelProtocol.MineLimit == 59;
    }

    internal static async Task<bool> DuelFixedWindowSelfTest()
    {
        var id = Guid.NewGuid();
        var sent = new TaskCompletionSource<DuelPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clicked = new TaskCompletionSource<DuelPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new DuelWindow(new("start", id, Level: "hard", Minutes: 1), packet =>
        {
            if (packet.Kind == "shot") clicked.TrySetResult(packet);
            else sent.TrySetResult(packet);
        });
        var handleField = typeof(DuelWindow).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)!;
        window.Start(false);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            IntPtr handle;
            while ((handle = (IntPtr)handleField.GetValue(window)!) == IntPtr.Zero)
            {
                if (sent.Task.IsCompleted) return false;
                await Task.Delay(10, limit.Token);
            }
            if (!GetClientRect(handle, out var initial) || initial.Right < 100 ||
                !SetWindowPos(handle, IntPtr.Zero, 0, 0, 100, 100, 0x16) ||
                !GetClientRect(handle, out var after) || after.Right != initial.Right || after.Bottom != initial.Bottom) return false;
            window.Receive(new("target", id, Slot: 3, X: 100, Y: 100));
            int mouse = (100 * after.Bottom / DuelProtocol.Height << 16) | (100 * after.Right / DuelProtocol.Width);
            if (!PostMessage(handle, 0x201, new IntPtr(1), new IntPtr(mouse))) return false;
            var shot = await clicked.Task.WaitAsync(limit.Token);
            if (shot.Id != id || shot.Slot != 3 || !DuelProtocol.Hits(shot.X, shot.Y, 100, 100, 12) ||
                !PostMessage(handle, 0x100, new IntPtr(27), IntPtr.Zero)) return false;
            var quit = await sent.Task.WaitAsync(limit.Token);
            return quit.Kind == "quit" && quit.Id == id;
        }
        finally { window.Close(); }
    }
}
