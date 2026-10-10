using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TCPTunnel
{
    internal static class NicknamePolicy
    {
        private static readonly Lazy<Dictionary<int, string>> confusables = new Lazy<Dictionary<int, string>>(LoadConfusables);
        internal static bool IsLegacyProfileName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length is < 3 or > 20) return false;
            const string illegal = @"!@#$%^&*()[]{}+,/\|";
            foreach (char character in name)
                if (Char.IsWhiteSpace(character) || Char.IsControl(character) || illegal.IndexOf(character) >= 0) return false;
            try { _ = name.Normalize(); return true; } catch (ArgumentException) { return false; }
        }
        internal static bool IsValid(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length is < 3 or > 20) return false;
            try
            {
                if (name != name.Normalize(NormalizationForm.FormKC)) return false;
                foreach (Rune rune in name.EnumerateRunes())
                    if (!Rune.IsLetterOrDigit(rune) && rune.Value is not ('_' or '-' or '.')) return false;
                return true;
            }
            catch (ArgumentException) { return false; }
        }
        internal static string Skeleton(string name)
        {
            var result = new StringBuilder();
            foreach (Rune rune in name.ToLowerInvariant().Normalize(NormalizationForm.FormD).EnumerateRunes())
                result.Append(confusables.Value.TryGetValue(rune.Value, out var mapped) ? mapped : rune.ToString());
            return result.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        }
        internal static bool Same(string first, string second) => String.Equals(Skeleton(first), Skeleton(second), StringComparison.Ordinal);
        private static Dictionary<int, string> LoadConfusables()
        {
            using Stream resource = typeof(NicknamePolicy).Assembly.GetManifestResourceStream("TCPTunnel.Security.Confusables") ?? throw new InvalidDataException("Unicode security data is unavailable.");
            using var memory = new MemoryStream(); resource.CopyTo(memory); byte[] bytes = memory.ToArray();
            if (Convert.ToHexString(SHA256.HashData(bytes)) != "6ED3EE967C9DFDF6677D563C9985182FBC50A2EFB7D6059CD57B2E2CE18F5B92") throw new InvalidDataException("Unicode security data checksum mismatch.");
            var result = new Dictionary<int, string>();
            foreach (string row in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                string line = row.Split('#')[0].Trim(); if (line.Length == 0) continue;
                string[] fields = line.Split(';'); string source = fields[0].Trim();
                if (source.Contains(' ')) throw new InvalidDataException("Unsupported Unicode security mapping.");
                var target = new StringBuilder();
                foreach (string scalar in fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)) target.Append(Char.ConvertFromUtf32(Int32.Parse(scalar, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                result.Add(Int32.Parse(source, NumberStyles.HexNumber, CultureInfo.InvariantCulture), target.ToString());
            }
            return result;
        }
    }
}
