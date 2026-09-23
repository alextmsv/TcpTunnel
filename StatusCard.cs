using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;

namespace TCPTunnel
{
    internal sealed class StatusCard
    {
        private readonly string[] left;
        private readonly string[] right;
        private int cachedWidth;
        private string cached;
        internal StatusCard(IEnumerable<string> left, IEnumerable<string> right = null)
        {
            this.left = left.Select(Safe).ToArray();
            this.right = right?.Select(Safe).ToArray() ?? Array.Empty<string>();
        }
        internal string Render(int width)
        {
            width = Math.Max(1, width);
            if (cached != null && cachedWidth == width) return cached;
            var rows = new List<string>();
            if (right.Length > 0 && width >= 64)
            {
                int rightStart = width / 2 + 2;
                for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
                    rows.Add(Fit(i < left.Length ? left[i] : "", rightStart - 3).PadRight(rightStart) +
                        Fit(i < right.Length ? right[i] : "", width - rightStart));
            }
            else
            {
                foreach (string line in left.Concat(right))
                {
                    for (int offset = 0; offset < Math.Max(1, line.Length); offset += width)
                        rows.Add(line.Substring(offset, Math.Min(width, line.Length - offset)));
                }
            }
            if (rows.Count == 0) rows.Add("");
            var result = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
                result.Append(i + 1 == rows.Count ? rows[i] : rows[i].PadRight(width));
            cachedWidth = width;
            return cached = result.ToString();
        }
        internal void WritePlain(TextWriter writer, int width)
        {
            width = Math.Max(1, width);
            string text = Render(width);
            for (int offset = 0; offset < Math.Max(1, text.Length); offset += width)
                writer.WriteLine(text.Substring(offset, Math.Min(width, text.Length - offset)).TrimEnd());
        }
        private static string Fit(string text, int width) => text.Length <= width ? text : text.Substring(0, Math.Max(0, width - 1)) + "…";
        private static string Safe(string text) => new string((text ?? "").Select(c => Char.IsControl(c) ? ' ' : c).ToArray());
    }
}
