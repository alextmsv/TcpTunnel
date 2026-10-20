using System;
using System.Globalization;

namespace TCPTunnel;

internal sealed class NumericOptionInput
{
    private string digits;
    internal bool Editing => digits != null;
    internal string Text => digits;
    internal bool TryEdit(ConsoleKeyInfo key, int current, out int value)
    {
        value = current;
        if (key.KeyChar is >= '0' and <= '9')
        {
            digits ??= String.Empty;
            if (digits.Length < 2) digits += key.KeyChar;
            value = digits.Length == 0 ? 0 : Int32.Parse(digits, CultureInfo.InvariantCulture);
            return true;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            digits ??= current.ToString(CultureInfo.InvariantCulture);
            if (digits.Length > 0) digits = digits.Substring(0, digits.Length - 1);
            value = digits.Length == 0 ? 0 : Int32.Parse(digits, CultureInfo.InvariantCulture);
            return true;
        }
        bool commit = Editing && key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar;
        digits = null;
        return commit;
    }
    internal void Reset() => digits = null;
}
