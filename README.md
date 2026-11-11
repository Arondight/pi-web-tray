# Pi Web Tray

**English** | [简体中文](README.zh-CN.md)

Pi Web Tray is a universal Windows 7 to 11 harness for Pi Web. It provides runtime detection, dependency installation and updates, service hosting and self-update.

- **One click** — installs and manages Pi Web; start or stop the service with one click.
- **Visible state** — color-coded tray icon, notifications and live logs.
- **Start at login** — uses the standard Run key or an administrator scheduled task.
- **Port safe** — native process APIs identify the listener; unrelated programs are never killed.
- **Self-update** — checks for updates, updates the app and removes files from older releases.
- **Portable** — no installer, no third-party runtime dependencies.

## Requirements

| Component      | Requirement                                                        |
| -------------- | ------------------------------------------------------------------ |
| 64-bit Windows | 7 SP1, 8, 8.1, 10 or 11                                            |
| .NET Framework | 4.8 — Windows 7 SP1/8/8.1 require 4.8; Windows 10/11 can use 4.8.1 |
| Node.js        | 22.19 or later, with npm                                           |

Windows 7 and Windows 8 have no official Node.js 22 build. **Download Node.js** asks for confirmation and then opens an unsigned community build; it is not produced or verified by this project. Pi Web's terminal needs ConPTY (Windows 10 1809+), so Pi Web can run on legacy systems, but terminal functionality may be impaired.

### Older Windows runtime

Windows 7 SP1, Windows 8 and Windows 8.1 need [.NET Framework 4.8](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) rather than 4.8.1. If checking for updates on Windows 7 cannot reach GitHub, install the [TLS 1.2 update](https://support.microsoft.com/help/3140245).

## Recommended software

Pi Web Tray does not require Python 3 or PowerShell, but installing them extends what Pi can do.

### Python 3

Windows 10 and 11: use the [official download page](https://www.python.org/downloads/). Tick **Add python.exe to PATH**:

Older Windows releases no longer receive current Python installers. The newest releases that still ship one:

| System          | Newest release with an installer                                         |
| --------------- | ------------------------------------------------------------------------ |
| Windows 7 and 8 | [Python 3.8.10](https://www.python.org/downloads/release/python-3810/)   |
| Windows 8.1     | [Python 3.13.17](https://www.python.org/downloads/release/python-31317/) |

### PowerShell

Windows 8.1, 10 and 11 can install the latest PowerShell from the [official releases page](https://github.com/PowerShell/PowerShell/releases). Windows 7 SP1 needs [Windows Management Framework 5.1](https://www.microsoft.com/en-us/download/details.aspx?id=54616) to get Windows PowerShell 5.1. Windows 8 cannot update PowerShell, only the built-in 3.0 is available.

## Quick start

1. Download and extract the release archive.
2. Run `.\pi-web-tray.exe`.

On first start Pi Web Tray checks the environment, installs missing dependencies, starts Pi Web and opens the browser. With the default `AutoInstall=1`, missing dependencies are installed automatically during a normal start. If Node.js is missing, it explains the required version and offers the download page; Pi Web Tray stays running and rechecks when Node.js appears. When Node.js was installed after that prompt, automatic installation stays off: missing dependencies are reported and the menu offers **Install/update service**. If Node.js is removed while Pi Web is running, Pi Web Tray notifies you without stopping the running service and rechecks before the next start. If an install is interrupted, the next attempt reinstalls the relevant packages instead of trusting an incomplete directory.

Configuration and logs live in `%LOCALAPPDATA%\pi-web-tray` by default. Set `PI_WEB_TRAY_PORTABLE=1` to keep the ini and logs beside the executable. Portable mode is ignored when the executable is in a protected system directory such as `C:\Program Files`. Even in portable mode, `state.ini`, `crash.log` and update staging remain in the data directory. The `pi-web-tray.files.txt` manifest is used by future updates to remove files that are no longer shipped; do not delete it.

## Tray menu

The icon colour shows the state at a glance:

| Colour    | State                                                                            |
| --------- | -------------------------------------------------------------------------------- |
| 🟢 Green  | Running                                                                          |
| ⚪ Grey   | Stopped                                                                          |
| 🟠 Orange | Busy: checking, installing, starting/stopping the service, or applying an update |
| 🔵 Blue   | Missing prerequisites (Node.js / Pi / Pi Web)                                    |
| 🔴 Red    | Error or port conflict                                                           |

The menu follows the service state:

```text
● Running · 127.0.0.1:30141 · administrator mode
---------------------------------------
Open browser
Start service
Stop service
Restart service
---------------------------------------
Update service
Download Node.js
---------------------------------------
Environment check
Settings...
Logs >
  View service runtime log
  View service install/update log
  View software update log
Open data folder
Open program folder
Open configuration file
Copy page link
Create desktop shortcut
---------------------------------------
Start at login: Enabled (standard user)
Start at login as administrator: no
---------------------------------------
Restart as administrator
Check for updates
About
Exit and stop service
```

- **Update service** stops Pi Web first, reinstalls or updates Pi Web, and restarts it automatically only if the service was running before the update. When dependencies are missing, the same item is shown as **Install/update service**. The progress window supports cancellation; while the self-update helper is replacing files, the tray exit action is disabled.
- A listener is treated as Pi Web Tray's service only when it belongs to the same Windows session and runs the installed `pi-web.js`. Another session or installation is a **port conflict**, so Pi Web Tray neither opens nor stops it. Change `Port` in **Settings** if needed.
- If Pi Web stops unexpectedly, Pi Web Tray reports it. With `AutoRestart=1` it restarts automatically; with `AutoCheckUpdates=1` it checks for a new release after startup.

## Configuration

### Configuration file

`pi-web-tray.ini` lives in `%LOCALAPPDATA%\pi-web-tray` by default, so separate Windows users do not share settings. Set `PI_WEB_TRAY_PORTABLE=1` to keep the ini and logs next to the executable (ignored for protected system directories). A commented sample is in [`config/pi-web-tray.ini`](config/pi-web-tray.ini).

| Key                | Default         | Description                                                                             |
| ------------------ | --------------- | --------------------------------------------------------------------------------------- |
| `Port`             | `30141`         | Pi Web listening port.                                                                  |
| `WorkingDir`       | `%USERPROFILE%` | Service working directory and Pi's default project root.                                |
| `AutoInstall`      | `1`             | Install missing dependencies at startup.                                                |
| `InstallPi`        | `1`             | Also install/update Pi with Pi Web.                                                     |
| `AutoRestart`      | `0`             | Restart Pi Web after an unexpected stop.                                                |
| `AutoCheckUpdates` | `0`             | Check for a Pi Web Tray update after startup.                                           |
| `InstallTag`       | `latest`        | Pi Web npm distribution tag or exact version. Exact versions/ranges do not apply to Pi. |
| `NpmRegistry`      | empty           | Custom registry, e.g. `https://registry.npmmirror.com`.                                 |
| `Proxy`            | empty           | Optional HTTP or SOCKS5 proxy for update checks/downloads; empty uses the system proxy. |
| `NodePath`         | empty           | Explicit `node.exe`; empty enables auto-detection.                                      |
| `Language`         | `auto`          | `auto`, `en`, `zh` (Simplified) or `zh-Hant`.                                           |
| `AutostartAdmin`   | `0`             | Admin rights for start at login (menu-managed).                                         |

Pi is not a required dependency of Pi Web. When `InstallPi=1`, Pi is installed or updated together with Pi Web; when `0`, an existing Pi installation is left alone.

Values support `%ENV%` expansion. Quote values containing `;` or `#`; comments need whitespace before the marker.

When the Pi Web Tray installation directory can be modified by standard users, enabling administrator start-at-login is refused without changing the current setting. The security warning offers a **Fix** button that repairs the folder ACL through UAC. If the administrator preference is unavailable and the user enables start-at-login from the main menu, Pi Web Tray clears that preference and uses standard-user start-at-login instead.

The data directory is per user. Pi Web Tray redacts URL user info and token query values from its logs. When `Proxy` is set, only Pi Web Tray update checks and downloads use it. An empty value uses the system proxy. The setting is not passed to Node, npm or Pi Web child processes. The **Test** button in Settings only tests proxy connectivity and does not save.

### Environment variables

| Variable               | Description                                                                                                                                              |
| ---------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PI_WEB_TRAY_INSTANCE` | Isolated instance: separate mutex, signals, config, data and start-at-login entries. With `PI_WEB_TRAY_HOME`, data goes under `HOME\pi-web-tray.<name>`. |
| `PI_WEB_TRAY_PORT`     | Overrides the configured port.                                                                                                                           |
| `PI_WEB_TRAY_LANG`     | Overrides the UI language.                                                                                                                               |
| `PI_WEB_TRAY_HOME`     | Overrides the data directory root and takes precedence over portable mode.                                                                               |
| `PI_WEB_TRAY_PORTABLE` | `1` keeps the ini and logs beside the executable.                                                                                                        |
| `PI_WEB_TRAY_NO_NODE`  | Test hook: simulate a missing Node.js installation.                                                                                                      |
| `PI_WEB_TRAY_NO_PIWEB` | Test hook: simulate a missing Pi Web package.                                                                                                            |
| `PI_WEB_TRAY_NO_PICLI` | Test hook: simulate a missing Pi CLI package.                                                                                                            |

Named instances also use separate start-at-login names so they cannot overwrite the default installation's entries. A Run value or scheduled task does not preserve the launching environment, so use start-at-login from the default instance.

## Command line

```text
.\pi-web-tray.exe                   Start the service and open the browser.
.\pi-web-tray.exe --autostart       Start Pi Web Tray and the service without opening a browser.
.\pi-web-tray.exe --tray-only       Start Pi Web Tray only, without the service.
.\pi-web-tray.exe --start-no-open   Start the service without a browser.
.\pi-web-tray.exe --stop            Stop the service; keep Pi Web Tray running if it is present.
.\pi-web-tray.exe --quit            Stop the service and exit.
.\pi-web-tray.exe --check           Check the environment (0 = ready, 1 = missing).
.\pi-web-tray.exe --install         Install dependencies without starting Pi Web Tray.
.\pi-web-tray.exe --dry-run         With --install, print the npm commands only.
.\pi-web-tray.exe --diag            Write a diagnostic snapshot to diag.txt.
.\pi-web-tray.exe --lang-check      Verify that every UI string is complete for all supported languages.
.\pi-web-tray.exe --make-ico        Generate the application icon (build tool).
.\pi-web-tray.exe --version         Print the version.
.\pi-web-tray.exe --help            Show command-line help.
```

Unknown arguments return exit code 2.

## Build from source

Requires Windows 10 or 11 with either Visual Studio 2022/2026 and the **.NET desktop development** workload, or the .NET SDK 8.0+ with the .NET Framework 4.8 targeting pack.

```bat
git clone https://github.com/Arondight/pi-web-tray.git
cd .\pi-web-tray
.\build.cmd
```

## Security and limitations

- Runs with the current user token (`asInvoker`); elevation only on request. No telemetry and no persistent network listener. A SOCKS5 update proxy opens a temporary loopback listener that only the current tray process may use.
- Updates are downloaded from GitHub over HTTPS and verified against the published SHA-256. The helper copies staged files into a target snapshot and re-verifies them before replacing files; the snapshot is protected when the installation directory is protected. Releases are unsigned, so SmartScreen may warn on first run. The helper still accepts a caller-provided staging hash; it is not a substitute for code signing.
- Admin start-at-login requires the current Windows account to be a member of the Administrators group and creates an on-logon task for the current user. Standard-user over-the-shoulder UAC is refused. The task name includes the user SID and named-instance suffix, so another user's or instance's task is not overwritten. A legacy `PiWebTray` task is reported. It is removed when autostart is next changed.
- `server.log`, `install.log` and `update.log` rotate at 4 MB. If the tray runs elevated, use an elevated console for `--stop` / `--quit`. The tray menu always works. If the log-host child cannot be created, service output falls back to the unredacted, non-rotating `1>>` path described in [SECURITY.md](SECURITY.md).
- The administrator start-at-login task is scoped to the current Windows user and keeps running on battery power. It does not inherit the environment of the shell that enabled it.

## Related projects

Pi Web Tray only installs and launches [Pi Web](https://github.com/agegr/pi-web) and [Pi](https://github.com/earendil-works/pi). It does not bundle or modify them. Report their issues to their own repositories.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Report vulnerabilities according to [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
