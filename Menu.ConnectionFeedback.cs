using System;

namespace TCPTunnel;

public partial class Menu
{
    internal bool ShowPrivacyWarning(string text)
    {
        optionsFooter = text;
        try { return ReadOptionsSelection(Lang.Current == AppLanguage.Russian ? "Видимость IP адреса" : "IP address visibility",
            new[] { Lang.Get(TextId.Back), Lang.Current == AppLanguage.Russian ? "Продолжить" : "Continue" }, 0) == 1; }
        finally { optionsFooter = null; }
    }
    internal bool ShowConnectionFeedback(string text, bool confirmPlaintext)
    {
        optionsFooter = text;
        try
        {
            string[] choices = confirmPlaintext ? new[] { Lang.Get(TextId.Back), Lang.Get(TextId.ConnectionPlaintextOnce) } : new[] { Lang.Get(TextId.Back) };
            return ReadOptionsSelection(Lang.Get(TextId.ConnectionFailureTitle), choices, 0) == 1 && confirmPlaintext;
        }
        finally { optionsFooter = null; }
    }
}
