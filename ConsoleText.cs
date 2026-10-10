using System;
using System.Globalization;
using System.Text;

namespace TCPTunnel
{
    internal static class ConsoleText
    {
        internal static string Safe(string text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            var result = new StringBuilder(text.Length);
            foreach (char character in text)
            {
                UnicodeCategory category = Char.GetUnicodeCategory(character);
                result.Append(Char.IsControl(character) || category == UnicodeCategory.Format || category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator ? ' ' : character);
            }
            return result.ToString();
        }
    }
}
