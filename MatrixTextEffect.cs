using System;

namespace TCPTunnel
{
    internal sealed class MatrixTextEffect
    {
        private readonly string original;
        private readonly int prefixLength;
        private readonly int characterDelay;
        private int revealed = -1;
        private string display;
        internal string Display => display ??= original.Substring(0, VisibleLength) + new string(' ', original.Length - VisibleLength);
        internal int Duration { get; }
        internal bool Active { get; private set; } = true;
        internal int VisibleLength => prefixLength + Math.Max(0, revealed);
        internal MatrixTextEffect(string text, int delayMilliseconds = 20)
        {
            original = text; characterDelay = Math.Clamp(delayMilliseconds, 1, 500);
            int separator = text.IndexOf("]: ", StringComparison.Ordinal);
            prefixLength = separator >= 0 ? separator + 3 : 0;
            Duration = Math.Min(30000, Math.Max(1, (text.Length - prefixLength) * characterDelay));
            Tick(0);
        }
        internal static bool CanAnimate(string text)
        {
            if (String.IsNullOrEmpty(text) || text.Length > 2048) return false;
            foreach (char glyph in text)
                if (glyph > 0x2E7F || glyph is >= '\u1100' and <= '\u115F' || glyph is '\u2329' or '\u232A' || Char.IsSurrogate(glyph) || Char.IsControl(glyph) ||
                    Char.GetUnicodeCategory(glyph) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark or System.Globalization.UnicodeCategory.Format) return false;
            return true;
        }
        internal bool Tick(int elapsedMilliseconds)
        {
            if (!Active) return false;
            if (elapsedMilliseconds >= Duration) { Finish(); return true; }
            int count = Math.Min(original.Length - prefixLength, Math.Max(0, elapsedMilliseconds) / characterDelay);
            if (count == revealed) return false;
            revealed = count;
            display = null;
            return true;
        }
        internal void Finish() { Active = false; revealed = original.Length - prefixLength; display = original; }
    }
}
