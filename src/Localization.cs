// Pi Web Tray — UI strings (i18n)
//
// Standard .NET resource-based localization:
//   Resources/Strings.resx        neutral language (English)
//   Resources/Strings.zh-Hans.resx Simplified Chinese
//
// ResourceManager loads the neutral resources from the main assembly and culture-specific
// resources from the generated satellite assemblies. Additional languages can be added by
// creating Resources/Strings.<culture>.resx; --lang-check checks every installed culture.
//
// Language order: PI_WEB_TRAY_LANG > Language= in the ini > the system UI language.
// Accepted values: auto / en / zh / zh-Hant (zh-Hans, zh-CN, zh-SG, zh-TW, zh-HK and
// en-US/en-GB also work). zh and zh-* Simplified variants use zh-Hans; zh-Hant and
// Traditional variants use zh-Hant.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;

namespace PiWebTray
{
    internal static class L
    {
        internal const string Auto = "auto";

        private const string ResourceBaseName = "PiWebTray.Resources.Strings";
        private const string ResourceFileName = "pi-web-tray.resources.dll";

        private static readonly ResourceManager Manager =
            new ResourceManager(ResourceBaseName, typeof(L).Assembly);
        private static readonly Regex Placeholder = new Regex(
            @"\{(\d+)(?:[^}]*)\}", RegexOptions.Compiled);
        private static readonly CultureInfo EnglishCulture = LoadCulture("en", CultureInfo.InvariantCulture);
        private static readonly CultureInfo ChineseCulture = LoadCulture("zh-Hans", EnglishCulture);
        private static readonly CultureInfo TraditionalCulture = LoadCulture("zh-Hant", EnglishCulture);

        private static string _current = "en";
        private static CultureInfo _culture = EnglishCulture;

        internal static string Current { get { return _current; } }

        internal static int Count
        {
            get
            {
                Dictionary<string, string> neutral = LoadSet(CultureInfo.InvariantCulture);
                return neutral == null ? 0 : neutral.Count;
            }
        }

        internal static int LanguageCount
        {
            get
            {
                // English (neutral), Simplified Chinese and Traditional Chinese, plus any
                // additional installed satellite cultures.
                int extra = 0;
                foreach (CultureInfo culture in InstalledCultures())
                {
                    if (string.Equals(culture.Name, ChineseCulture.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(culture.Name, TraditionalCulture.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    extra++;
                }
                return 3 + extra;
            }
        }

        private static CultureInfo LoadCulture(string name, CultureInfo fallback)
        {
            try
            {
                return CultureInfo.GetCultureInfo(name);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        // "auto" or empty: follow the system; otherwise resolve an installed culture.
        internal static void Use(string setting)
        {
            string requested = Normalize(setting);
            if (requested.Length == 0) requested = DetectFromSystem();

            CultureInfo selected = ResolveCulture(requested);
            if (selected == null) selected = ResolveCulture(DetectFromSystem());
            if (selected == null) selected = EnglishCulture;

            _culture = selected;
            _current = CanonicalName(selected);
        }

        private static string Normalize(string setting)
        {
            if (string.IsNullOrEmpty(setting)) return "";
            string text = setting.Trim().ToLowerInvariant().Replace('_', '-');
            if (text.Length == 0 || text == Auto) return "";
            if (text == "zh" || text == "cn" || text == "chinese"
                || text == "zh-cn" || text == "zh-sg"
                || text == "zh-hans" || text.StartsWith("zh-hans-"))
            {
                return "zh-hans";
            }
            if (text == "zh-tw" || text == "zh-hk" || text == "zh-mo"
                || text == "zh-hant" || text.StartsWith("zh-hant-"))
            {
                return "zh-hant";
            }
            if (text == "en" || text == "english" || text.StartsWith("en-")) return "en";
            return text;
        }

        private static string DetectFromSystem()
        {
            try
            {
                string name = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
                if (name == "zh-cn" || name == "zh-sg"
                    || name == "zh-hans" || name.StartsWith("zh-hans-"))
                {
                    return "zh-hans";
                }
                if (name.StartsWith("zh-hant") || name.StartsWith("zh-tw")
                    || name.StartsWith("zh-hk") || name.StartsWith("zh-mo"))
                {
                    return "zh-hant";
                }
                return name;
            }
            catch (Exception)
            {
                return "en";
            }
        }

        private static CultureInfo ResolveCulture(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name == "en") return EnglishCulture;
            if (name == "zh-hans") return ChineseCulture;

            try
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(name);
                for (CultureInfo current = culture;
                     current != null && current.Name.Length > 0;
                     current = current.Parent)
                {
                    if (HasSatellite(current)) return current;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static bool HasSatellite(CultureInfo culture)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, culture.Name);
                return File.Exists(Path.Combine(dir, ResourceFileName));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static List<CultureInfo> InstalledCultures()
        {
            List<CultureInfo> cultures = new List<CultureInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (string directory in Directory.GetDirectories(baseDir))
                {
                    string name = Path.GetFileName(directory);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!File.Exists(Path.Combine(directory, ResourceFileName))) continue;

                    CultureInfo culture;
                    try
                    {
                        culture = CultureInfo.GetCultureInfo(name);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (culture.Name.Length == 0) continue;
                    if (seen.Add(culture.Name)) cultures.Add(culture);
                }
            }
            catch (Exception)
            {
            }
            return cultures;
        }

        private static string CanonicalName(CultureInfo culture)
        {
            if (culture.Name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)) return "zh";
            if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return culture.Name;
            if (culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
            return culture.Name;
        }

        internal static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return "!" + key + "!";
            try
            {
                string value = Manager.GetString(key, _culture);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            catch (MissingManifestResourceException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            // Show the key when a translation is missing; easier to spot than a blank string
            return "!" + key + "!";
        }

        internal static string T(string key, params object[] args)
        {
            string format = T(key);
            if (args == null) return format;
            try
            {
                return string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                return format;
            }
            catch (ArgumentNullException)
            {
                return format;
            }
        }

        // For --lang-check: every key needs non-empty English text and every installed
        // culture needs the same keys with non-empty text.
        internal static List<string> Problems()
        {
            List<string> broken = new List<string>();
            Dictionary<string, string> english = LoadSet(CultureInfo.InvariantCulture);
            if (english == null)
            {
                broken.Add("neutral resource set is missing");
                return broken;
            }
            if (english.Count == 0) broken.Add("neutral resource set is empty");

            foreach (KeyValuePair<string, string> pair in english)
            {
                if (string.IsNullOrEmpty(pair.Value))
                    broken.Add(pair.Key + ": empty English text");
            }

            // The project ships Simplified Chinese even if its satellite is missing.
            HashSet<string> checkedCultures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            checkedCultures.Add(ChineseCulture.Name);
            checkedCultures.Add(TraditionalCulture.Name);
            CheckCulture(english, ChineseCulture, broken);
            CheckCulture(english, TraditionalCulture, broken);
            foreach (CultureInfo culture in InstalledCultures())
            {
                if (checkedCultures.Add(culture.Name))
                    CheckCulture(english, culture, broken);
            }
            return broken;
        }

        private static void CheckCulture(
            Dictionary<string, string> english,
            CultureInfo culture,
            List<string> broken)
        {
            Dictionary<string, string> translated = LoadSet(culture);
            if (translated == null)
            {
                broken.Add(culture.Name + " resource set is missing");
                return;
            }

            foreach (KeyValuePair<string, string> pair in english)
            {
                string text;
                if (!translated.TryGetValue(pair.Key, out text))
                    broken.Add(culture.Name + " " + pair.Key + ": missing text");
                else if (string.IsNullOrEmpty(text))
                    broken.Add(culture.Name + " " + pair.Key + ": empty text");
                else if (!SamePlaceholders(pair.Value, text))
                    broken.Add(culture.Name + " " + pair.Key + ": placeholder mismatch");
            }

            foreach (string key in translated.Keys)
            {
                if (!english.ContainsKey(key))
                    broken.Add(culture.Name + " " + key + ": extra key");
            }
        }

        private static bool SamePlaceholders(string english, string translated)
        {
            List<int> expected = PlaceholderIndexes(english);
            List<int> actual = PlaceholderIndexes(translated);
            if (expected.Count != actual.Count) return false;
            for (int i = 0; i < expected.Count; i++)
            {
                if (expected[i] != actual[i]) return false;
            }
            return true;
        }

        private static List<int> PlaceholderIndexes(string text)
        {
            List<int> indexes = new List<int>();
            foreach (Match match in Placeholder.Matches(text ?? ""))
            {
                int index;
                if (int.TryParse(match.Groups[1].Value, out index)) indexes.Add(index);
            }
            indexes.Sort();
            return indexes;
        }

        private static Dictionary<string, string> LoadSet(CultureInfo culture)
        {
            try
            {
                ResourceSet set = Manager.GetResourceSet(culture, true, false);
                if (set == null) return null;

                Dictionary<string, string> values =
                    new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in set)
                {
                    string key = entry.Key as string;
                    if (key == null) continue;
                    values[key] = entry.Value as string;
                }
                return values;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
