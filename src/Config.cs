// Pi Web Tray — configuration
//
// The config file is pi-web-tray.ini in %LOCALAPPDATA%\pi-web-tray (per user).
// A portable installation can set PI_WEB_TRAY_PORTABLE=1 to keep the ini and logs
// next to the executable; that is not recommended in a shared directory because
// the ini may contain registry credentials and the logs may contain user data.
// All paths support %ENV% expansion.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PiWebTray
{
    internal sealed class Config
    {
        // ---- defaults ----
        internal int Port = 30141;
        // The value persisted in the ini, before PI_WEB_TRAY_PORT is applied. The
        // settings dialog edits this value, so a temporary environment override can
        // never be written back as a permanent configuration change.
        internal int ConfiguredPort = 30141;
        internal bool PortFromEnvironment;
        internal bool AutostartAdmin;
        internal bool AutoInstall = true;
        // Pi Web runs without the separately installed Pi. When this is off,
        // the tray neither installs nor updates Pi, and a missing Pi is optional.
        internal bool InstallPi = true;
        internal bool AutoRestart;
        internal bool AutoCheckUpdates;
        internal string Language = L.Auto;
        internal string InstallTag = "latest";
        internal string NpmRegistry = "";
        internal string Proxy = "";
        internal string WorkingDir;
        // Raw ini values (before %ENV% expansion). Settings writes these back so a
        // deliberate template is not silently replaced by the expanded value.
        internal string ConfiguredWorkingDir = "";
        internal string ConfiguredNodePath = "";
        internal string ConfiguredInstallTag = "";
        internal string ConfiguredNpmRegistry = "";
        internal string ConfiguredProxy = "";
        internal string ConfiguredLanguage = "";
        internal string NodePath;
        // Diagnostics for the tray notification: a configured path that was invalid
        // must not disappear silently.
        internal bool WorkingDirFallback;
        internal bool NodePathInvalid;

        // ---- runtime paths ----
        internal string BaseDir;
        internal string DataDir;
        internal string OutputDir;
        internal string IniPath;
        internal string LogFile;
        internal string InstallLogFile;
        internal string UpdateLogFile;
        internal string DiagFile;

        internal const string InstallerPackage = "@agegr/pi-web";
        internal const string PiCliPackage = "@earendil-works/pi-coding-agent";

        internal static Config Load(bool writeDefaults)
        {
            Config config = new Config();
            config.BaseDir = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
            config.DataDir = TrayState.Folder;

            // In a system directory such as Program Files a normal user cannot write, so the
            // writable config and logs always go to %LOCALAPPDATA%\pi-web-tray.
            // The check is path based, not a write probe, so elevated and normal runs behave the same.
            //
            // Instances started with PI_WEB_TRAY_INSTANCE (dev / parallel testing) are isolated too:
            // otherwise they would read the main instance's ini (port included) and could stop its service.
            bool isolated = Instance.Suffix.Length > 0;
            bool systemInstall = IsSystemDirectory(config.BaseDir);
            bool portable = IsTruthy(Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORTABLE"), false);
            string homeOverride = Environment.GetEnvironmentVariable("PI_WEB_TRAY_HOME");
            // HOME is an explicit data-directory override and takes precedence over
            // portable mode; otherwise ini/logs and state/update staging would end up
            // in different roots when both variables are set.
            bool useDataDir = !string.IsNullOrEmpty((homeOverride ?? "").Trim())
                || ShouldUseDataDir(isolated, systemInstall, portable);
            config.OutputDir = useDataDir ? config.DataDir : config.BaseDir;
            config.LogFile = Path.Combine(config.OutputDir, "server.log");
            config.InstallLogFile = Path.Combine(config.OutputDir, "install.log");
            config.UpdateLogFile = Path.Combine(config.OutputDir, "update.log");
            config.DiagFile = Path.Combine(config.OutputDir, "diag.txt");

            string localIni = Path.Combine(config.BaseDir, "pi-web-tray.ini");
            string dataIni = Path.Combine(config.DataDir, "pi-web-tray.ini");
            if (!useDataDir)
            {
                config.IniPath = localIni;
            }
            else
            {
                // Copy the template ini into the data directory once, then read and write only that copy
                // Read-only commands (--help/--version) must not touch the disk either
                if (writeDefaults)
                {
                    try
                    {
                        Directory.CreateDirectory(config.DataDir);
                        if (!PathSafety.ContainsReparsePoint(localIni)
                            && !PathSafety.ContainsReparsePoint(dataIni)
                            && !File.Exists(dataIni) && File.Exists(localIni))
                        {
                            File.Copy(localIni, dataIni);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                config.IniPath = dataIni;
            }

            // Resolve the language before writing the default ini, so the template comments match it.
            // Read-only commands run with writeDefaults=false and therefore do not copy the
            // template ini into the data directory. Fall back to the program-directory ini for
            // the language value, without writing anything, so --help/--lang-check honour it
            // on the first run.
            string languageIni = config.IniPath;
            if (!writeDefaults && useDataDir && !File.Exists(config.IniPath)
                && File.Exists(localIni))
                languageIni = localIni;
            string language = Environment.GetEnvironmentVariable("PI_WEB_TRAY_LANG");
            if (string.IsNullOrEmpty(language)) language = ReadIniValue(languageIni, "language");
            if (string.IsNullOrEmpty(language)) language = L.Auto;
            config.Language = language;
            L.Use(language);

            if (writeDefaults && !File.Exists(config.IniPath)) config.WriteDefaultIni();

            try
            {
                // Never parse a symlink planted at the known ini name. Reading it could
                // copy protected file contents into configuration values and logs.
                if (!PathSafety.ContainsReparsePoint(config.IniPath))
                {
                    foreach (string rawLine in File.ReadAllLines(config.IniPath))
                    {
                        string key;
                        string value;
                        if (!TryParseLine(rawLine, out key, out value)) continue;
                        key = key.ToLowerInvariant();
                        string rawValue = value;
                        value = Environment.ExpandEnvironmentVariables(value);

                        switch (key)
                        {
                            case "port":
                                int parsed;
                                if (int.TryParse(value, out parsed) && parsed > 0 && parsed <= 65535) config.Port = parsed;
                                break;
                            case "workingdir":
                                if (value.Length > 0)
                                {
                                    config.WorkingDir = ResolvePath(config.BaseDir, value);
                                    config.ConfiguredWorkingDir = rawValue;
                                }
                                break;
                            case "nodepath":
                                if (value.Length > 0)
                                {
                                    config.NodePath = ResolvePath(config.BaseDir, value);
                                    config.ConfiguredNodePath = rawValue;
                                }
                                break;
                            case "autoinstall":
                                config.AutoInstall = IsTruthy(value, true);
                                break;
                            case "installpi":
                                config.InstallPi = IsTruthy(value, true);
                                break;
                            case "autorestart":
                                config.AutoRestart = IsTruthy(value, false);
                                break;
                            case "autocheckupdates":
                                config.AutoCheckUpdates = IsTruthy(value, false);
                                break;
                            case "installtag":
                                if (value.Length > 0)
                                {
                                    config.InstallTag = value;
                                    config.ConfiguredInstallTag = rawValue;
                                }
                                break;
                            case "npmregistry":
                                if (value.Length > 0)
                                {
                                    config.NpmRegistry = value;
                                    config.ConfiguredNpmRegistry = rawValue;
                                }
                                break;
                            case "proxy":
                                if (value.Length > 0)
                                {
                                    config.Proxy = value;
                                    config.ConfiguredProxy = rawValue;
                                }
                                break;
                            case "language":
                                if (value.Length > 0)
                                {
                                    config.Language = value;
                                    config.ConfiguredLanguage = rawValue;
                                }
                                break;
                            case "autostartadmin":
                                config.AutostartAdmin = IsTruthy(value, false);
                                break;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            // Resolve the language again: env var > ini value > system default
            string envLanguage = Environment.GetEnvironmentVariable("PI_WEB_TRAY_LANG");
            L.Use(string.IsNullOrEmpty(envLanguage) ? config.Language : envLanguage);

            // ConfiguredLanguage is the value Settings will write back. A temporary
            // PI_WEB_TRAY_LANG override must not be persisted just because the ini
            // has no Language key; an absent key means the documented default, auto.
            if (string.IsNullOrEmpty(config.ConfiguredLanguage))
                config.ConfiguredLanguage = L.Auto;

            if (!string.IsNullOrEmpty(config.NodePath) && !File.Exists(config.NodePath))
                config.NodePathInvalid = true;

            if (config.WorkingDir == null || !Directory.Exists(config.WorkingDir))
            {
                config.WorkingDirFallback = !string.IsNullOrEmpty(config.ConfiguredWorkingDir);
                config.WorkingDir = Shell.Home();
            }

            // Manual Config objects (tests/extensions) and old callers may set only
            // the effective fields. Fill the raw display slots with those defaults
            // without overwriting a real ini value.
            if (string.IsNullOrEmpty(config.ConfiguredNodePath))
                config.ConfiguredNodePath = config.NodePath ?? "";
            if (string.IsNullOrEmpty(config.ConfiguredInstallTag))
                config.ConfiguredInstallTag = config.InstallTag ?? "latest";
            if (string.IsNullOrEmpty(config.ConfiguredNpmRegistry))
                config.ConfiguredNpmRegistry = config.NpmRegistry ?? "";
            if (string.IsNullOrEmpty(config.ConfiguredProxy))
                config.ConfiguredProxy = config.Proxy ?? "";
            if (string.IsNullOrEmpty(config.ConfiguredLanguage))
                config.ConfiguredLanguage = config.Language ?? L.Auto;

            config.ConfiguredPort = config.Port;

            // The port can be overridden by an env var, so test instances need no ini change.
            // Keep the ini value separately: Settings must not persist this temporary value.
            int overridePort;
            if (int.TryParse(Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORT"), out overridePort)
                && overridePort > 0 && overridePort <= 65535)
            {
                config.Port = overridePort;
                config.PortFromEnvironment = true;
            }

            return config;
        }

        // Relative config paths are resolved against the executable directory, not
        // the shell's current directory. The service later starts with WorkingDir,
        // so letting these two use different roots could execute a different file.
        internal static string ResolvePath(string baseDir, string value)
        {
            try
            {
                if (string.IsNullOrEmpty(value)) return value;
                if (Path.IsPathRooted(value)) return Path.GetFullPath(value);
                string root = string.IsNullOrEmpty(baseDir) ? null : baseDir;
                return string.IsNullOrEmpty(root)
                    ? Path.GetFullPath(value)
                    : Path.GetFullPath(Path.Combine(root, value));
            }
            catch (Exception)
            {
                return value;
            }
        }

        internal static bool IsEnabled(string value)
        {
            return IsTruthy(value, false);
        }

        private static bool IsTruthy(string value, bool fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            if (value == "1") return true;
            if (value == "0") return false;
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        // Parse one "key = value" line. Inline comments start with ';' or '#' only when they
        // follow whitespace, so paths such as C:\work;old keep their semicolon. A value may
        // also be wrapped in single or double quotes, which is the safest way to keep both.
        internal static bool TryParseLine(string rawLine, out string key, out string value)
        {
            key = null;
            value = null;
            if (rawLine == null) return false;

            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') return false;

            int eq = line.IndexOf('=');
            if (eq <= 0) return false;

            key = line.Substring(0, eq).Trim();
            if (key.Length == 0) return false;

            value = line.Substring(eq + 1).Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\''))
            {
                char quote = value[0];
                StringBuilder quoted = new StringBuilder();
                bool closed = false;
                for (int i = 1; i < value.Length; i++)
                {
                    char c = value[i];
                    if (c == quote)
                    {
                        // A doubled quote represents one literal quote. This lets
                        // IniValue write credentials/paths containing quotes without
                        // silently truncating them at the first inner quote.
                        if (i + 1 < value.Length && value[i + 1] == quote)
                        {
                            quoted.Append(quote);
                            i++;
                            continue;
                        }
                        closed = true;
                        break;
                    }
                    quoted.Append(c);
                }
                if (closed) value = quoted.ToString();
                return true;
            }

            int comment = -1;
            for (int i = 0; i < value.Length; i++)
            {
                if ((value[i] == ';' || value[i] == '#') && (i == 0 || char.IsWhiteSpace(value[i - 1])))
                {
                    comment = i;
                    break;
                }
            }
            if (comment >= 0) value = value.Substring(0, comment).TrimEnd();
            return true;
        }

        // Read a single key; used to pick the language before the full parse
        internal static string ReadIniValue(string path, string key)
        {
            try
            {
                if (!File.Exists(path)) return "";
                if (PathSafety.ContainsReparsePoint(path)) return "";
                string wanted = (key ?? "").Trim();
                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string parsedKey;
                    string parsedValue;
                    if (!TryParseLine(rawLine, out parsedKey, out parsedValue)) continue;
                    if (!string.Equals(parsedKey, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    return Environment.ExpandEnvironmentVariables(parsedValue);
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        private void WriteDefaultIni()
        {
            try
            {
                if (PathSafety.ContainsReparsePoint(IniPath)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(IniPath));
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(L.T("ini.title"));
                sb.AppendLine(L.T("ini.subtitle"));
                sb.AppendLine();
                sb.AppendLine(L.T("ini.portC"));
                sb.AppendLine("Port=30141");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.workdirC"));
                sb.AppendLine("WorkingDir=%USERPROFILE%");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.autoInstallC"));
                sb.AppendLine("AutoInstall=1");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.installPiC"));
                sb.AppendLine("InstallPi=1");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.autoRestartC"));
                sb.AppendLine("AutoRestart=0");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.autoCheckC"));
                sb.AppendLine("AutoCheckUpdates=0");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.tagC"));
                sb.AppendLine("InstallTag=latest");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.registryC"));
                sb.AppendLine("NpmRegistry=");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.proxyC"));
                sb.AppendLine("Proxy=");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.lookupC"));
                sb.AppendLine("NodePath=");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.autostartC"));
                sb.AppendLine("AutostartAdmin=0");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.langC"));
                // Do not persist a temporary PI_WEB_TRAY_LANG override into the new file;
                // the env var still wins for the current process.
                sb.AppendLine("Language=" + L.Auto);
                File.WriteAllText(IniPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
        }

        internal bool SetValue(string key, string value)
        {
            return SetValues(new KeyValuePair<string, string>[]
            {
                new KeyValuePair<string, string>(key, value)
            });
        }

        // Quote values that would otherwise be truncated by the inline-comment parser.
        private static string IniValue(string value)
        {
            if (value == null) return "";
            bool quote = value.IndexOf(';') >= 0 || value.IndexOf('#') >= 0
                || value.Length != value.Trim().Length
                || value.IndexOf('"') >= 0
                || (value.Length > 0 && value[0] == '\'');
            if (!quote) return value;
            // The parser recognizes a doubled quote inside a quoted value. This keeps
            // values such as proxy passwords and registry URLs containing quotes
            // intact instead of truncating them on the next read.
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        // Write several keys in one pass so a settings dialog cannot leave the file
        // half-updated. Existing comments and ordering are preserved.
        internal bool SetValues(IEnumerable<KeyValuePair<string, string>> values)
        {
            try
            {
                // A symlink at the known ini name must not redirect an elevated settings
                // save (or read a protected file) into an arbitrary target.
                if (PathSafety.ContainsReparsePoint(IniPath)) return false;
                List<string> lines = File.Exists(IniPath)
                    ? new List<string>(File.ReadAllLines(IniPath))
                    : new List<string>();

                foreach (KeyValuePair<string, string> pair in values)
                {
                    string prefix = pair.Key + "=";
                    bool replaced = false;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string parsedKey;
                        string parsedValue;
                        if (!TryParseLine(lines[i], out parsedKey, out parsedValue)) continue;
                        if (!string.Equals(parsedKey, pair.Key, StringComparison.OrdinalIgnoreCase)) continue;
                        // Replace every duplicate so a later load cannot override
                        // this value with a stale second copy of the same key.
                        lines[i] = prefix + IniValue(pair.Value);
                        replaced = true;
                    }
                    if (!replaced)
                    {
                        lines.Add("");
                        lines.Add(prefix + IniValue(pair.Value));
                    }
                }

                // Write beside the ini and replace atomically; a crash must not leave
                // the user with a truncated configuration file.
                string temp = IniPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                string backup = IniPath + "." + Guid.NewGuid().ToString("N") + ".bak";
                if (PathSafety.ContainsReparsePoint(temp)
                    || PathSafety.ContainsReparsePoint(backup)) return false;
                bool keepBackup = false;
                try
                {
                    File.WriteAllLines(temp, lines.ToArray(), new UTF8Encoding(true));
                    ReplaceIniFile(temp, IniPath, backup);
                    return true;
                }
                catch (Exception)
                {
                    // If a fallback moved the original file aside but could not restore
                    // it, keep the backup instead of deleting the only remaining copy.
                    keepBackup = true;
                    return false;
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                    try { if (!keepBackup && File.Exists(backup)) File.Delete(backup); } catch (Exception) { }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // File.Replace is the best choice on NTFS, but it is not supported on some
        // file systems (notably FAT) and can fail on redirected/locked files. Fall back
        // to a backup + move so portable installations on removable media still work.
        private static void ReplaceIniFile(string temp, string ini, string backup)
        {
            if (!File.Exists(ini))
            {
                File.Move(temp, ini);
                return;
            }

            try
            {
                File.Replace(temp, ini, backup, true);
                return;
            }
            catch (Exception)
            {
                // Fall through to the portable replacement path.
            }

            bool movedOriginal = false;
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(ini, backup);
                movedOriginal = true;
                File.Move(temp, ini);
            }
            catch (Exception)
            {
                // If ReplaceFile moved the old file aside but did not restore it, put
                // it back. This also covers the case where the temporary file could
                // not be moved into place.
                if (movedOriginal || (!File.Exists(ini) && File.Exists(backup)))
                {
                    try
                    {
                        if (File.Exists(ini))
                        {
                            File.SetAttributes(ini, FileAttributes.Normal);
                            File.Delete(ini);
                        }
                    }
                    catch (Exception)
                    {
                    }
                    try { File.Move(backup, ini); } catch (Exception) { }
                }
                throw;
            }
        }

        // Per-user data is the safe default: the ini may hold registry credentials and
        // the logs may hold prompts or file contents. Portable mode is an explicit opt-in.
        internal static bool ShouldUseDataDir(bool isolated, bool systemInstall, bool portable)
        {
            if (isolated || systemInstall) return true;
            return !portable;
        }

        internal static bool IsSystemDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return true;
            string[] roots = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string trimmedRoot = root.TrimEnd('\\');
                string prefix = trimmedRoot + "\\";
                if (string.Equals(dir.TrimEnd('\\'), trimmedRoot,
                        StringComparison.OrdinalIgnoreCase)
                    || dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // A drive root (C:\) is never a sensible place for a user-writable ini.
            try
            {
                string pathRoot = Path.GetPathRoot(dir);
                if (!string.IsNullOrEmpty(pathRoot)
                    && string.Equals(pathRoot.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }
    }
}
