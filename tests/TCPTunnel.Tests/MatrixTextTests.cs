using TCPTunnel;

internal static class MatrixTextTests
{
    public static bool MatrixBoundedLocalEffectSelfTest()
    {
        const string text = "<<< [tmsvalex]: приветик";
        var effect = new MatrixTextEffect(text, 20);
        if (effect.Display.Length != text.Length || !effect.Display.StartsWith("<<< [") || effect.Display == text) return false;
        int prefix = text.IndexOf("]: ") + 3;
        if (effect.Display.Substring(prefix).Trim().Length != 0) return false;
        effect.Tick(20); if (effect.Display.Substring(prefix).TrimEnd() != "п") return false;
        effect.Tick(40); if (effect.Display.Substring(prefix).TrimEnd() != "пр") return false;
        effect.Tick(effect.Duration);
        var slow = new MatrixTextEffect(text, 500); slow.Tick(500);
        return !effect.Active && effect.Display == text && effect.Duration == (text.Length - prefix) * 20 && slow.Display.Substring(prefix).TrimEnd() == "п" &&
            new MatrixTextEffect(new string('A', 2048), 500).Duration == 30000 &&
            !MatrixTextEffect.CanAnimate(new string('A', 2049)) && !MatrixTextEffect.CanAnimate("a\u001bb");
    }
}
