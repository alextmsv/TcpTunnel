using System;
using System.Collections.Generic;

namespace TCPTunnel
{
    internal static class InlineSuggestions
    {
        public static bool TryGetToken(string text, int cursorIndex, char trigger, out int tokenStart, out string prefix)
        {
            tokenStart = -1;
            prefix = String.Empty;
            if (text == null || cursorIndex <= 0 || cursorIndex > text.Length)
                return false;

            int scan = cursorIndex - 1;
            while (scan >= 0 && !Char.IsWhiteSpace(text[scan]) && text[scan] != trigger)
                scan--;

            if (scan < 0 || text[scan] != trigger)
                return false;

            tokenStart = scan;
            prefix = text.Substring(scan + 1, cursorIndex - scan - 1);
            return true;
        }

        public static string[] MatchPrefix(IReadOnlyList<string> candidates, string prefix, int maxResults)
        {
            if (candidates == null || candidates.Count == 0 || maxResults <= 0)
                return Array.Empty<string>();

            var matches = new List<string>();
            foreach (string candidate in candidates)
            {
                if (!String.IsNullOrEmpty(candidate) &&
                    candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    matches.Add(candidate);
            }

            matches.Sort((first, second) =>
            {
                int byLength = first.Length.CompareTo(second.Length);
                return byLength != 0 ? byLength : String.Compare(first, second, StringComparison.OrdinalIgnoreCase);
            });

            if (matches.Count > maxResults)
                matches.RemoveRange(maxResults, matches.Count - maxResults);
            return matches.ToArray();
        }

        internal static bool RunSelfTest()
        {
            bool tokenAtBareTrigger = TryGetToken("@", 1, '@', out int start1, out string prefix1) &&
                                       start1 == 0 && prefix1 == "";
            bool tokenAfterPartial = TryGetToken("hi @al", 6, '@', out int start2, out string prefix2) &&
                                      start2 == 3 && prefix2 == "al";
            bool noTokenWithoutTrigger = !TryGetToken("hello", 5, '@', out _, out _);
            bool noTokenAcrossWhitespace = !TryGetToken("@a b", 4, '@', out _, out _);
            bool noTokenAtStart = !TryGetToken("", 0, '@', out _, out _);
            bool tokenMidString = TryGetToken("hi @alex there", 6, '@', out int start3, out string prefix3) &&
                                   start3 == 3 && prefix3 == "al";

            string[] candidates = { "alextmsv", "tmsvalex", "bob", "alex" };
            string[] allMatches = MatchPrefix(candidates, "", 10);
            string[] prefixedA = MatchPrefix(candidates, "a", 10);
            string[] prefixedAd = MatchPrefix(candidates, "ad", 10);
            string[] capped = MatchPrefix(candidates, "a", 1);

            bool matchAllOk = allMatches.Length == 4;
            bool matchPrefixOk = prefixedA.Length == 2 && prefixedA[0] == "alex" && prefixedA[1] == "alextmsv";
            bool matchNoneOk = prefixedAd.Length == 0;
            bool matchCappedOk = capped.Length == 1 && capped[0] == "alex";
            bool caseInsensitiveOk = MatchPrefix(candidates, "ALEX", 10).Length == 2;

            return tokenAtBareTrigger && tokenAfterPartial && noTokenWithoutTrigger &&
                   noTokenAcrossWhitespace && noTokenAtStart && tokenMidString &&
                   matchAllOk && matchPrefixOk && matchNoneOk && matchCappedOk && caseInsensitiveOk;
        }
    }
}
