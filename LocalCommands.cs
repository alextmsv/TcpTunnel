using System;

namespace TCPTunnel
{
    internal static class LocalCommands
    {
        internal static bool Handle(string input, Action<AppLanguage> changeLanguage, Action<string> write, Action showInterface = null)
        {
            if (String.IsNullOrEmpty(input) || input[0] != '\\' || input.StartsWith("\\\\", StringComparison.Ordinal)) return false;
            if (ImageInput.Classify(input, out _) != ImageInputKind.NotImage) return false;
            string[] parts = input.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0].Equals("\\lang", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 2 && (parts[1].Equals("ru", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("en", StringComparison.OrdinalIgnoreCase)))
                    changeLanguage(parts[1].Equals("ru", StringComparison.OrdinalIgnoreCase) ? AppLanguage.Russian : AppLanguage.English);
                else write(Lang.Get(TextId.LocalLangUsage));
            }
            else if (parts[0].Equals("\\interface", StringComparison.OrdinalIgnoreCase) && parts.Length == 1 && showInterface != null) showInterface();
            else write(Lang.Get(TextId.LocalCommandUnknown, ConsoleText.Safe(parts[0])));
            return true;
        }
    }
}
