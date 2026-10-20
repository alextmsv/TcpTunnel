using TCPTunnel;

internal static class NavigationModeTests
{
    public static bool MouseKeyboardSwitchSequenceSelfTest()
    {
        var mode = new NavigationMode();
        if (mode.Mouse || mode.Click(false) || mode.Click(true) || mode.Mouse) return false;
        mode.Arrow();
        if (mode.Click(true) || mode.Mouse || !mode.Click(true) || !mode.Mouse) return false;
        if (mode.Arrow() || !mode.Mouse) return false;
        mode.Click(true);
        if (mode.Arrow() || !mode.Mouse || !mode.Arrow() || mode.Mouse) return false;
        return !mode.Arrow() && !mode.Mouse;
    }
}
