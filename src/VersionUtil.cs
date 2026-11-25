// Pi Web Tray — version parsing and semver comparison.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PiWebTray
{
    internal static class VersionUtil
    {
        // "v22.23.1" -> [22,23,1]; anything unparsable is ignored
        internal static int[] Parse(string raw)
        {
            string[] parts = CoreParts(raw);
            List<int> numbers = new List<int>();
            foreach (string part in parts)
            {
                int value;
                if (!int.TryParse(part, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value))
                {
                    break;
                }
                numbers.Add(value);
            }
            return numbers.ToArray();
        }

        // The numeric core as digit strings. Unlike Parse, an identifier longer
        // than Int32 is kept: Compare orders it by magnitude instead of dropping
        // the whole version to 0.0.0.
        private static string[] CoreParts(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new string[0];
            string text = raw.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);

            List<string> parts = new List<string>();
            foreach (string part in text.Split('.'))
            {
                StringBuilder digits = new StringBuilder();
                foreach (char c in part)
                {
                    if (c < '0' || c > '9') break;
                    digits.Append(c);
                }

                if (digits.Length == 0) break;
                parts.Add(digits.ToString());

                // "1-pre": the numeric part is the patch level, a pre-release suffix ends parsing
                if (digits.Length < part.Length) break;
            }
            return parts.ToArray();
        }

        // a < b -> -1, equal -> 0, a > b -> 1
        internal static int Compare(string a, string b)
        {
            string[] left = CoreParts(a);
            string[] right = CoreParts(b);
            int length = Math.Max(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                string l = i < left.Length ? left[i] : "0";
                string r = i < right.Length ? right[i] : "0";
                // Arbitrary-length digit comparison: an oversized core must not
                // overflow Int32 and silently compare as 0.0.0.
                int numeric = CompareNumericIdentifiers(l, r);
                if (numeric != 0) return numeric;
            }

            // Semver pre-release ordering: for equal numeric cores a stable
            // version is newer than any pre-release, and pre-release identifiers
            // are compared numerically/lexically. Without this, 0.4.0 and
            // 0.4.0-beta compare equal and a beta user never sees the stable
            // release.
            string leftPre = PreRelease(a);
            string rightPre = PreRelease(b);
            if (leftPre.Length == 0 && rightPre.Length == 0) return 0;
            if (leftPre.Length == 0) return 1;
            if (rightPre.Length == 0) return -1;
            return ComparePreRelease(leftPre, rightPre);
        }

        private static string PreRelease(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string text = raw.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            // Build metadata starts at '+'. It must not be mistaken for a
            // pre-release dash inside metadata such as "1.0.0+build-1".
            int plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);
            int dash = text.IndexOf('-');
            if (dash < 0) return "";
            return text.Substring(dash + 1);
        }

        private static int ComparePreRelease(string a, string b)
        {
            string[] left = a.Split('.');
            string[] right = b.Split('.');
            int length = Math.Max(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                if (i >= left.Length) return -1;
                if (i >= right.Length) return 1;

                bool leftNumeric = IsNumericIdentifier(left[i]);
                bool rightNumeric = IsNumericIdentifier(right[i]);

                if (leftNumeric && rightNumeric)
                {
                    int numeric = CompareNumericIdentifiers(left[i], right[i]);
                    if (numeric != 0) return numeric;
                    continue;
                }
                if (leftNumeric != rightNumeric)
                    return leftNumeric ? -1 : 1;

                // Semver orders alphanumeric identifiers by ASCII sort order, which is
                // case-sensitive; OrdinalIgnoreCase would treat "Beta" and "beta" as equal.
                int text = string.CompareOrdinal(left[i], right[i]);
                if (text < 0) return -1;
                if (text > 0) return 1;
            }
            return 0;
        }

        // A semver numeric identifier is a non-empty run of digits. int.TryParse must
        // not be used here: it also accepts signs/whitespace and silently overflows on
        // identifiers longer than Int32, which would fall back to a lexical compare.
        private static bool IsNumericIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        // Compare two digit-only identifiers of arbitrary length without overflow:
        // strip leading zeros, the longer number is larger, and equal-length digit
        // strings compare lexicographically in numeric order.
        private static int CompareNumericIdentifiers(string a, string b)
        {
            string left = a.TrimStart('0');
            string right = b.TrimStart('0');
            if (left.Length != right.Length) return left.Length < right.Length ? -1 : 1;
            int order = string.CompareOrdinal(left, right);
            if (order < 0) return -1;
            if (order > 0) return 1;
            return 0;
        }

        internal static bool AtLeast(string actual, string minimum)
        {
            return Compare(actual, minimum) >= 0;
        }
    }
}
