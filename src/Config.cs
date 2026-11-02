// Pi Web Tray — configuration
//
// The config file is pi-web-tray.ini:
//   system install (Program Files etc.): the ini next to the exe is only a template;
//   the real file lives in %LOCALAPPDATA%\pi-web-tray\. Otherwise the ini next to the exe is used.
//
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
        internal bool AutostartAdmin;
        internal bool AutoInstall = true;
        internal string Language = L.Auto;
        internal bool InstallPiCli = true;
        internal string InstallTag = "latest";
        internal string NpmRegistry = "";
        internal string WorkingDir;
        internal string NodePath;
        internal string ServerScript;

        // ---- runtime paths ----
        internal string BaseDir;
        internal string DataDir;
        internal string OutputDir;
        internal string IniPath;
        internal string LogFile;
        internal string InstallLogFile;
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
            bool useDataDir = isolated || systemInstall;
            config.OutputDir = useDataDir ? config.DataDir : config.BaseDir;
            config.LogFile = Path.Combine(config.OutputDir, "server.log");
            config.InstallLogFile = Path.Combine(config.OutputDir, "install.log");
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
                        if (!File.Exists(dataIni) && File.Exists(localIni)) File.Copy(localIni, dataIni);
                    }
                    catch (Exception)
                    {
                    }
                }
                config.IniPath = dataIni;
            }

            // Resolve the language before writing the default ini, so the template comments match it
            string language = Environment.GetEnvironmentVariable("PI_WEB_TRAY_LANG");
            if (string.IsNullOrEmpty(language)) language = ReadIniValue(config.IniPath, "language");
            if (string.IsNullOrEmpty(language)) language = L.Auto;
            config.Language = language;
            L.Use(language);

            if (writeDefaults && !File.Exists(config.IniPath)) config.WriteDefaultIni();

            try
            {
                foreach (string rawLine in File.ReadAllLines(config.IniPath))
                {
                    string key;
                    string value;
                    if (!TryParseLine(rawLine, out key, out value)) continue;
                    key = key.ToLowerInvariant();
                    value = Environment.ExpandEnvironmentVariables(value);

                    switch (key)
                    {
                        case "port":
                            int parsed;
                            if (int.TryParse(value, out parsed) && parsed > 0 && parsed <= 65535) config.Port = parsed;
                            break;
                        case "workingdir":
                            if (value.Length > 0) config.WorkingDir = value;
                            break;
                        case "nodepath":
                            if (value.Length > 0) config.NodePath = value;
                            break;
                        case "serverscript":
                            if (value.Length > 0) config.ServerScript = value;
                            break;
                        case "autoinstall":
                            config.AutoInstall = IsTruthy(value, true);
                            break;
                        case "installpicli":
                            config.InstallPiCli = IsTruthy(value, true);
                            break;
                        case "installtag":
                            if (value.Length > 0) config.InstallTag = value;
                            break;
                        case "npmregistry":
                            if (value.Length > 0) config.NpmRegistry = value;
                            break;
                        case "language":
                            if (value.Length > 0) config.Language = value;
                            break;
                        case "autostartadmin":
                            config.AutostartAdmin = IsTruthy(value, false);
                            break;
                    }
                }
            }
            catch (Exception)
            {
            }

            // Resolve the language again: env var > ini value > system default
            string envLanguage = Environment.GetEnvironmentVariable("PI_WEB_TRAY_LANG");
            L.Use(string.IsNullOrEmpty(envLanguage) ? config.Language : envLanguage);

            if (config.WorkingDir == null || !Directory.Exists(config.WorkingDir))
                config.WorkingDir = Shell.Home();

            // The port can be overridden by an env var, so test instances need no ini change
            int overridePort;
            if (int.TryParse(Environment.GetEnvironmentVariable("PI_WEB_TRAY_PORT"), out overridePort)
                && overridePort > 0 && overridePort <= 65535)
            {
                config.Port = overridePort;
            }

            return config;
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
                int close = value.IndexOf(quote, 1);
                if (close > 0) value = value.Substring(1, close - 1);
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
                sb.AppendLine(L.T("ini.piCliC"));
                sb.AppendLine("InstallPiCli=1");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.tagC"));
                sb.AppendLine("InstallTag=latest");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.registryC"));
                sb.AppendLine("NpmRegistry=");
                sb.AppendLine();
                sb.AppendLine(L.T("ini.lookupC"));
                sb.AppendLine("NodePath=");
                sb.AppendLine("ServerScript=");
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

        internal void SetValue(string key, string value)
        {
            try
            {
                List<string> lines = File.Exists(IniPath)
                    ? new List<string>(File.ReadAllLines(IniPath))
                    : new List<string>();

                string prefix = key + "=";
                bool replaced = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string parsedKey;
                    string parsedValue;
                    if (!TryParseLine(lines[i], out parsedKey, out parsedValue)) continue;
                    if (!string.Equals(parsedKey, key, StringComparison.OrdinalIgnoreCase)) continue;
                    lines[i] = prefix + value;
                    replaced = true;
                    break;
                }
                if (!replaced)
                {
                    lines.Add("");
                    lines.Add(prefix + value);
                }
                File.WriteAllLines(IniPath, lines.ToArray(), new UTF8Encoding(true));
            }
            catch (Exception)
            {
            }
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
                string prefix = root.TrimEnd('\\') + "\\";
                if (dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
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
