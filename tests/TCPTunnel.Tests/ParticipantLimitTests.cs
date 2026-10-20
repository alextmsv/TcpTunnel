using TCPTunnel;

internal static class ParticipantLimitTests
{
    public static bool NumericParticipantInputSelfTest()
    {
        var input = new NumericOptionInput();
        var one = new ConsoleKeyInfo('1', ConsoleKey.D1, false, false, false);
        var two = new ConsoleKeyInfo('2', ConsoleKey.D2, false, false, false);
        if (!input.TryEdit(one, 64, out int value) || value != 1 || input.Text != "1") return false;
        if (!input.TryEdit(two, value, out value) || value != 12 || input.Text != "12") return false;
        if (!input.TryEdit(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), value, out value) || value != 12 || input.Editing) return false;
        if (!input.TryEdit(new ConsoleKeyInfo('0', ConsoleKey.D0, false, false, false), value, out value) || value != 0 || new HubOptions(HubIpMode.LanOnly, false, ParticipantLimit: value).IsValid) return false;
        if (!input.TryEdit(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false), value, out value) || input.Text != "" || value != 0) return false;
        if (!input.TryEdit(new ConsoleKeyInfo('6', ConsoleKey.D6, false, false, false), value, out value) || value != 6) return false;
        if (!input.TryEdit(new ConsoleKeyInfo('5', ConsoleKey.D5, false, false, false), value, out value) || value != 65) return false;
        return !new HubOptions(HubIpMode.LanOnly, false, ParticipantLimit: value).IsValid;
    }

    public static bool ParticipantLimitReservationSelfTest()
    {
        var admission = new ConnectionAdmission(2, true);
        using var first = admission.TryAcquire("tcp:first", false);
        using var second = admission.TryAcquire("bt:second", false);
        using var owner = admission.TryAcquire("local", true);
        if (first == null || second == null || owner == null || !first.Promote() || second.Promote() || !owner.Promote()) return false;
        if (!first.Promote()) return false;
        first.Dispose(); first.Dispose();
        if (!second.Promote()) return false;
        using var otherLocal = admission.TryAcquire("another-local", true);
        if (otherLocal == null || otherLocal.Promote()) return false;
        owner.Dispose();
        return otherLocal.Promote();
    }

    public static async Task<bool> ConcurrentParticipantLimitSelfTest()
    {
        var admission = new ConnectionAdmission(8, true);
        var leases = Enumerable.Range(0, 20).Select(i => admission.TryAcquire("source" + i, false)!).ToArray();
        try
        {
            bool[] accepted = await Task.WhenAll(leases.Select(lease => Task.Run(lease.Promote)));
            if (accepted.Count(value => value) != 7) return false;
            using var owner = admission.TryAcquire("local", true);
            return owner != null && owner.Promote() && new HubOptions(HubIpMode.LanOnly, false, ParticipantLimit: 1).IsValid &&
                !new HubOptions(HubIpMode.LanOnly, false, ParticipantLimit: 65).IsValid;
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
}
