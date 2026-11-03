# Pi Web Tray

**English** | [简体中文](README.zh-CN.md)

A universal Windows 7–11 harness for [pi](https://github.com/earendil-works/pi) and
[pi-web](https://github.com/agegr/pi-web). One tray app detects the runtime, installs and
updates the npm packages, hosts the service and updates itself.

- **One click** — start pi-web and open the browser, or just open it when it is running.
- **Setup included** — finds Node.js 22.19+, installs pi-web and the pi CLI with npm.
- **Visible state** — color-coded icon, notifications and live service/install/update logs.
- **Start at login** — normal Run key or elevated scheduled task, without a browser.
- **Port safe** — identifies the listener with native APIs and never kills a stranger.
- **Self-update** — GitHub release, SHA-256 verification, stale-file cleanup.
- **Portable** — no installer, no third-party runtime dependencies (.NET Framework 4.8).
- **Localized** — English, Simplified Chinese and Traditional Chinese.

## Requirements

| Component      | Requirement                                                       |
| -------------- | ----------------------------------------------------------------- |
| Windows        | 7 SP1, 8, 8.1, 10 or 11 (64-bit recommended)                      |
| .NET Framework | 4.8 — see the Windows 7 note below                                |
| Node.js        | 22.19 or later, with npm                                          |
| Network        | npm registry (`NpmRegistry` may use a mirror); GitHub for updates |

### Windows 7 runtime

Windows 7 needs **.NET Framework 4.8**, not 4.8.1: 4.8.1 does not support Windows 7,
even though the system dialog may still offer it. Install 4.8 from:

<https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48>

Keep `pi-web-tray.exe.config` next to the executable.

Windows 7 and Windows 8 have no official Node.js 22 build. **Download Node.js** asks for
confirmation and then opens an unsigned community build; it is not produced or verified
by this project. pi-web's terminal needs ConPTY (Windows 10 1809+), so on legacy systems
the tray may run while the service or the terminal is unavailable. Windows 7 and
Windows 8.1 are out of Microsoft support; use them at your own risk.

## Quick start

1. Download and extract the release archive.
2. Keep `pi-web-tray.exe`, `pi-web-tray.exe.config`, `pi-web-tray.files.txt` and the
   `zh-Hans` / `zh-Hant` folders together.
3. Run `pi-web-tray.exe`.

On first start the app checks the environment, installs what is missing, starts pi-web
and opens the browser. Configuration and logs (`server.log`, `install.log`,
`update.log`) live next to the executable, or in `%LOCALAPPDATA%\pi-web-tray` when the
executable is in a protected directory such as `Program Files`. The
`pi-web-tray.files.txt` manifest is used by future updates to remove dropped files; do
not delete it.

Before the first conversation, sign in to a model provider in the **Models** panel or
run `pi auth login`. pi-web and pi share credentials in `~/.pi/agent`.

## Tray menu

```text
Open Pi Web · Start · Stop · Restart
Update pi-web / pi dependencies · Update Pi Web Tray · Download Node.js…
Environment check · Settings · Logs · data/program folder · configuration · URL · shortcut
Start at login · Start at login as admin · Restart as administrator · About · Exit
```

- Left click starts pi-web and opens the browser, or only opens the browser when the
  service is already running. **Start**/**Restart** never open a browser.
- **Update pi-web / pi dependencies** stops pi-web first (npm cannot replace a running
  package), installs both packages when `InstallPiCli=1`, and restarts the service only
  if it was running before. A progress window can cancel the install.
- **Update Pi Web Tray** verifies the release SHA-256, replaces files (UAC when needed),
  preserves the service state and removes files the new release no longer contains.
  Cancel aborts the active download immediately.
- A listener that is not pi-web is reported as a **port conflict**; the tray neither
  treats it as running nor stops it. Change `Port` in Settings.
- If pi-web stops unexpectedly, the tray reports it and can restart it when
  `AutoRestart=1`. `AutoCheckUpdates=1` checks for a new release shortly after startup.
- The environment check shows runtime components (version, path, status) and
  application information (configuration, port, directories, logs).

## Configuration

`pi-web-tray.ini` is generated on first start next to the executable, or in
`%LOCALAPPDATA%\pi-web-tray` for protected directories. A commented sample is in
[`config/pi-web-tray.ini`](config/pi-web-tray.ini).

| Key                | Default         | Description                                              |
| ------------------ | --------------- | -------------------------------------------------------- |
| `Port`             | `30141`         | pi-web listening port.                                   |
| `WorkingDir`       | `%USERPROFILE%` | Service working directory and pi's default project root. |
| `AutoInstall`      | `1`             | Install pi-web automatically when missing.               |
| `AutoRestart`      | `0`             | Restart pi-web after an unexpected stop.                 |
| `AutoCheckUpdates` | `0`             | Check for a tray update shortly after startup.           |
| `InstallPiCli`     | `1`             | Install the pi CLI together with pi-web.                 |
| `InstallTag`       | `latest`        | npm distribution tag or exact version.                   |
| `NpmRegistry`      | empty           | Custom registry, e.g. `https://registry.npmmirror.com`.  |
| `NodePath`         | empty           | Explicit `node.exe`; empty enables auto-detection.       |
| `ServerScript`     | empty           | Explicit `pi-web.js`; empty enables auto-detection.      |
| `Language`         | `auto`          | `auto`, `en`, `zh` (Simplified) or `zh-Hant`.            |
| `AutostartAdmin`   | `0`             | Use admin rights for start at login (menu-managed).      |

`%ENV%` values are expanded; values containing `;` or `#` may be quoted. Comments start
with `;` or `#` after whitespace.

| Variable                     | Description                            |
| ---------------------------- | -------------------------------------- |
| `PI_WEB_TRAY_PORT`           | Overrides the configured port.         |
| `PI_WEB_TRAY_LANG`           | Overrides the UI language.             |
| `PI_WEB_TRAY_HOME`           | Overrides the data directory.          |
| `HTTP_PROXY` / `HTTPS_PROXY` | Used by npm and pi-web model requests. |

## Command line

```text
pi-web-tray.exe                  Start pi-web and open the browser.
pi-web-tray.exe --autostart      Start tray and service without a browser.
pi-web-tray.exe --tray-only      Start the tray only.
pi-web-tray.exe --start-no-open  Start the service without a browser.
pi-web-tray.exe --stop           Stop the service, keep the tray.
pi-web-tray.exe --quit           Stop the service and exit.
pi-web-tray.exe --check          Check the environment (0 = ready, 1 = missing).
pi-web-tray.exe --install        Install dependencies without the tray.
pi-web-tray.exe --dry-run        With --install, print the npm commands only.
pi-web-tray.exe --diag           Write a diagnostic snapshot to diag.txt.
pi-web-tray.exe --version        Print the version.
pi-web-tray.exe --help           Show command-line help.
```

Unknown arguments return exit code 2.

## Build from source

Windows 10/11, Visual Studio 2022/2026 with the .NET desktop workload and the .NET
Framework 4.8 targeting pack (or .NET SDK 8+).

```bat
git clone https://github.com/Arondight/pi-web-tray.git
cd pi-web-tray
build.cmd
```

Output: `artifacts\pi-web-tray.exe`, `pi-web-tray.exe.config` and the `zh-Hans` /
`zh-Hant` resource folders. The application has no NuGet dependencies; the test project
restores MSTest. Close a running `pi-web-tray.exe` before building.
`scripts\package-release.ps1` creates the release archive and its file manifest.

## Troubleshooting

| Symptom                                 | Action                                                                                                   |
| --------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| Blue icon, Node.js required             | Install Node.js 22.19+, then click the icon. Set `NodePath` if detection fails.                          |
| Installation failed                     | Check `install.log`: unwritable npm prefix, invalid `InstallTag`, proxy or network.                      |
| Update failed                           | Check `update.log`: cancelled UAC, proxy or network; retry **Update Pi Web Tray**.                       |
| Red icon, port conflict                 | Another program owns the port; choose a free one in **Settings**. The tray never kills it.               |
| Red icon, service failed                | Check `server.log`: `EADDRINUSE` means port conflict, `Cannot find module` an incomplete install.        |
| `Cannot launch conpty`                  | ConPTY needs Windows 10 1809+. Chat and tools still work where Node.js runs.                             |
| Start at login does not work            | Check the tray menu, then `HKCU\...\Run` or `schtasks /query /tn PiWebTray`.                             |
| Application does not start on Windows 7 | Install .NET Framework 4.8 from the link above and keep `pi-web-tray.exe.config` next to the executable. |
| More diagnostics                        | Run `pi-web-tray.exe --diag`; see `diag.txt`. A crashed tray logs to `crash.log`.                        |

## Security and limitations

- Runs with the current user token (`asInvoker`); elevation only on request. No telemetry
  and no network listener.
- Update downloads come from GitHub over HTTPS and are verified against the published
  SHA-256; the helper also validates the target and a staging fingerprint. Releases are
  unsigned, so SmartScreen may warn on first run.
- `NodePath`, `ServerScript`, `WorkingDir`, `NpmRegistry` and `InstallTag` are trusted
  configuration: treat a writable ini as code execution.
- Node.js is not installed automatically; it must be 22.19+.
- npm 12 blocks install scripts by default. The pi-web step passes a one-off
  `--allow-scripts` allowlist (existing entries plus `@agegr/pi-web` and `node-pty`);
  the pi CLI uses `--ignore-scripts`. npm configuration is never modified.
- A pi CLI update fails while pi is running and holding its files; close pi and retry.
- Admin start-at-login requires an admin account and is machine-wide. If the tray runs
  elevated, use an elevated console for `--stop` / `--quit`; the menu always works.

## Related projects

Pi Web Tray installs and launches [pi-web](https://github.com/agegr/pi-web)
(`@agegr/pi-web`) and [pi](https://github.com/earendil-works/pi)
(`@earendil-works/pi-coding-agent`). It does not bundle or modify them; report their
issues to their own repositories.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
Report vulnerabilities according to [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
