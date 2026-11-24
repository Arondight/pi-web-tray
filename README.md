# Pi Web Tray

**English** | [简体中文](README.zh-CN.md)

Pi Web Tray is a Windows 7 to 11 Harness solution based on [Pi Web](https://github.com/agegr/pi-web). It provides runtime detection, dependency installation and updates, service hosting and self-update.

- **One click** — installs and manages Pi Web; start or stop the service with one click.
- **Visible state** — color-coded tray icon, notifications and live logs.
- **Start at login** — uses the standard Run key or an administrator scheduled task.
- **One-click update** — checks for updates, updates the app and removes files from older releases.
- **Portable** — no installer and no bundled runtime.

## Runtime requirements

| Component              | Requirement                                             |
| ---------------------- | ------------------------------------------------------- |
| 64-bit Windows         | 7 SP1, 8.1, 10 or 11                                    |
| .NET Framework Runtime | 4.8; 4.8.1 is also supported on Windows 10 20H2+ and 11 |
| Node.js                | 22.19 or later, with npm                                |

Windows does not include Bash. If Bash is not installed separately, open **Settings → General → Shell tool** in Pi Web and enable **Use PowerShell instead of Bash**.

### Older Windows

Windows 7 SP1 and Windows 8.1 need [.NET Framework 4.8](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) rather than 4.8.1. If checking for updates on Windows 7 SP1 cannot reach GitHub, install the [TLS 1.2 update](https://support.microsoft.com/help/3140245).

Windows 7 SP1 has no official Node.js 22 build. **Download Node.js** asks for confirmation and then opens an unsigned community build; it is not produced or verified by this project. Windows 8.1 uses the official Node.js 22 build (Experimental). On Windows 7 SP1 and 8.1, Pi Web's terminal is unavailable: its PTY layer requires ConPTY (Windows 10 1809+). Next.js may also be unable to load its native SWC addon there and fall back to the WebAssembly build, so startup is noticeably slower.

On Windows 7 SP1 and 8.1, the recommended browser is [Supermium](https://github.com/win32ss/supermium/releases).

## Recommended software

Pi Web Tray does not require Python 3, PowerShell, or Git Bash to run, but installing them extends what Pi Web can do.

### Python 3

| System        | Recommended version                                                      |
| ------------- | ------------------------------------------------------------------------ |
| Windows 10/11 | [Latest Python 3 release](https://www.python.org/downloads/)             |
| Windows 8.1   | [Python 3.13.17](https://www.python.org/downloads/release/python-31317/) |
| Windows 7 SP1 | [Python 3.8.10](https://www.python.org/downloads/release/python-3810/)   |

Enable **Add python.exe to PATH** during installation.

### PowerShell

| System        | Recommended version                                                                                |
| ------------- | -------------------------------------------------------------------------------------------------- |
| Windows 10/11 | [Latest PowerShell release](https://github.com/PowerShell/PowerShell/releases)                     |
| Windows 8.1   | [PowerShell 7.2.24](https://github.com/PowerShell/PowerShell/releases/tag/v7.2.24)                 |
| Windows 7 SP1 | [Windows Management Framework 5.1](https://www.microsoft.com/en-us/download/details.aspx?id=54616) |

### Git Bash

| System        | Recommended version                                                                                       |
| ------------- | --------------------------------------------------------------------------------------------------------- |
| Windows 10/11 | [Latest Git for Windows release](https://git-scm.com/install/windows)                                     |
| Windows 8.1   | [Git for Windows 2.55.0.windows.5](https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.5) |
| Windows 7 SP1 | [Git for Windows 2.46.1.windows.1](https://github.com/git-for-windows/git/releases/tag/v2.46.1.windows.1) |

## Quick start

1. Download and extract the release archive.
2. Double-click `pi-web-tray.exe`.

On first start the tray checks the environment, installs missing dependencies, starts the service and opens the browser. With the default `AutoInstall=1`, missing dependencies are installed automatically during a normal start. If Node.js is missing, it explains the required version and offers the download page; the tray stays running and rechecks when Node.js appears. If Node.js was installed after that prompt, update the service from the tray menu.

Configuration and logs live in `%LOCALAPPDATA%\pi-web-tray` by default. Set `PI_WEB_TRAY_PORTABLE=1` to keep the ini and logs beside the executable. Portable mode is ignored when the executable is in a protected system directory such as `C:\Program Files`. The `pi-web-tray.files.txt` manifest is used by future updates to remove files that are no longer shipped; do not delete it.

## Tray menu

The icon colour shows the state at a glance:

| Colour    | State                                                                        |
| --------- | ---------------------------------------------------------------------------- |
| 🟢 Green  | Service running                                                              |
| ⚪ Grey   | Service stopped                                                              |
| 🟠 Orange | Tray busy (checking, installing, starting/stopping, unverified, or applying) |
| 🔵 Blue   | Node.js or service components missing                                        |
| 🔴 Red    | Failed (for example, port conflict)                                          |

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
Settings
View logs...
  View service runtime log
  View service install/update log
  View software update log
Open data folder
Open program folder
Open configuration file
Copy page link
Create desktop shortcut
---------------------------------------
[ ] Allow connections from the local network
[x] Start the program at login
[ ] Use administrator rights when starting at login
---------------------------------------
Restart in standard-user mode
Check for updates
About
Exit and stop service
```

- **Update service** stops the service first, reinstalls or updates Pi Web (and Pi when `InstallPi=1`). When service components are missing, the same item is shown as **Install/update service**.
- If the service stops unexpectedly, the tray reports it. With `AutoRestart=1` it restarts automatically up to three consecutive times.

## Configuration

Normal use does not require editing the configuration file. The **Settings** window in the tray menu exposes the supported options; the ini documentation below is for advanced deployment, automation, and troubleshooting.

### Configuration file

`pi-web-tray.ini` lives in `%LOCALAPPDATA%\pi-web-tray` by default, so separate Windows users do not share settings. Set `PI_WEB_TRAY_PORTABLE=1` to keep the ini and logs next to the executable (ignored for protected system directories). A commented sample is in [`config/pi-web-tray.ini`](config/pi-web-tray.ini).

| Key                | Default  | Description                                                                                                  |
| ------------------ | -------- | ------------------------------------------------------------------------------------------------------------ |
| `Port`             | `30141`  | Service listening port.                                                                                      |
| `AutoInstall`      | `1`      | Install missing dependencies at startup.                                                                     |
| `InstallPi`        | `1`      | Also install/update Pi alongside Pi Web; set `0` to skip.                                                    |
| `AutoRestart`      | `0`      | Restart the service after an unexpected stop.                                                                |
| `AutoCheckUpdates` | `0`      | Check for a Pi Web Tray update after startup.                                                                |
| `InstallTag`       | `latest` | Pi Web npm distribution tag or exact version; an exact version is enforced against the installed version.    |
| `NpmRegistry`      | empty    | Custom registry, e.g. `https://registry.npmmirror.com`.                                                      |
| `Proxy`            | empty    | Optional HTTP or SOCKS5 proxy for Pi Web Tray update checks/downloads; empty uses the system proxy.          |
| `NetworkAccess`    | `0`      | Allow other devices on the network to access the service; also opens the Windows Firewall service-port rule. |
| `NodePath`         | empty    | Explicit `node.exe`; empty enables auto-detection.                                                           |
| `Language`         | `auto`   | `auto`, `en`, `zh` or `zh-Hant`.                                                                             |
| `AutostartAdmin`   | `0`      | Admin rights for start at login (menu-managed).                                                              |

Values support `%ENV%` expansion. Quote values containing `;` or `#`; comments need whitespace before the marker. Relative `NodePath` values are resolved against the executable directory.

When the Pi Web Tray installation directory, any folder in its path, or any file loaded by the process in administrator mode can be modified or renamed by standard users, enabling administrator start-at-login is refused without changing the current setting. The security warning offers a **Fix** button that repairs the folder ACL through UAC when the rest of the path is protected. For a portable or user-profile installation whose parent is user-writable, the Fix action reports that the program must be moved to a protected location such as `C:\Program Files`; a user-writable parent cannot be repaired safely. Portable mode that keeps the ini and logs beside the executable is also refused by the Fix action, because the repaired ACL would make those runtime files read-only for the standard user. Runtime logs remain user-owned and do not make a protected installation fail this check. If the administrator preference is unavailable and the user enables start-at-login from the main menu, the tray clears that preference and uses standard-user start-at-login instead.

The data directory is per user. The tray redacts URL user info and token query values from its logs. When `Proxy` is set, only the tray update checks and downloads use it. An empty value uses the system proxy. The setting is not passed to Node, npm or Pi Web child processes. The **Test** button in Settings only tests proxy connectivity and does not save.

### Environment variables

| Variable               | Description                                                                                                                                                                         |
| ---------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PI_WEB_TRAY_INSTANCE` | Isolated instance: separate mutex, signals, config, data and start-at-login entries. With `PI_WEB_TRAY_HOME`, data goes under `HOME\pi-web-tray.<name>`.                            |
| `PI_WEB_TRAY_PORT`     | Overrides the configured port.                                                                                                                                                      |
| `PI_WEB_TRAY_LANG`     | Overrides the UI language.                                                                                                                                                          |
| `PI_WEB_TRAY_HOME`     | Overrides the data directory root and takes precedence over portable mode. Must be writable by the user; the administrator-mode update helper rejects protected system directories. |
| `PI_WEB_TRAY_PORTABLE` | `1` keeps the ini and logs beside the executable.                                                                                                                                   |
| `PI_WEB_TRAY_NO_NODE`  | Test hook: simulate a missing Node.js installation.                                                                                                                                 |
| `PI_WEB_TRAY_NO_PIWEB` | Test hook: simulate a missing Pi Web package.                                                                                                                                       |
| `PI_WEB_TRAY_NO_PICLI` | Test hook: simulate a missing Pi package.                                                                                                                                           |

Named instances use separate names for their legacy start-at-login entries, but start-at-login cannot be enabled for a named instance; use the default instance for start-at-login. A Run value or scheduled task does not preserve the launching environment.

## Command line

```text
.\pi-web-tray.exe                   Start the service and open the browser.
.\pi-web-tray.exe --autostart       Start the tray and the service without opening a browser.
.\pi-web-tray.exe --tray-only       Start the tray only, without the service.
.\pi-web-tray.exe --start-no-open   Start the service without a browser.
.\pi-web-tray.exe --stop            Stop the service; keep the tray running if it is present.
.\pi-web-tray.exe --quit            Stop the service and exit.
.\pi-web-tray.exe --check           Check the environment (0 = ready, 1 = missing).
.\pi-web-tray.exe --install         Install dependencies without starting the tray.
.\pi-web-tray.exe --dry-run         With --install, print the npm commands without installing.
.\pi-web-tray.exe --diag            Write a diagnostic snapshot to diag.txt.
.\pi-web-tray.exe --lang-check      Verify that every UI string is complete for all supported languages.
.\pi-web-tray.exe --make-ico        Generate the application icon (build tool).
.\pi-web-tray.exe --version         Print the version.
.\pi-web-tray.exe --help            Show command-line help.
```

Unknown arguments return exit code 2.

`--install` stops a running service before npm runs and restarts it only when a tray is running. Without a tray the service remains stopped. If the tray is in administrator mode, run `--install` from an administrator-mode console as well.

## Build from source

Choose one build tool:

- **.NET SDK 8.0 or later** (recommended for command-line builds), or
- **Build Tools for Visual Studio 2022 or later** (MSBuild only).

Either tool requires the **.NET Framework 4.8 Targeting Pack**. The standalone **.NET Framework 4.8 Developer Pack** includes it.

The build host must be Windows 10/11 (or the Windows Server CI runner). The compatibility check script also requires PowerShell 7.

**Visual Studio 2022 or later** with the **.NET desktop development** workload is an optional IDE, not a separate requirement. The application targets **.NET Framework 4.8**, not .NET 8. End users only need the **.NET Framework 4.8 Runtime** (or 4.8.1 where supported).

```bat
git clone https://github.com/Arondight/pi-web-tray.git
cd .\pi-web-tray
.\build.cmd
```

## Security and limitations

- Runs with the current user token; administrator mode is opt-in. No telemetry. The service listens on loopback unless network access is enabled.
- Administrator mode and administrator start-at-login are refused when the installation path or loaded files can be modified by standard users; move the program to a protected location such as `C:\Program Files`, or use the offered **Fix** action. Administrator start-at-login requires an Administrators account and creates a task for the current user only. These checks do not protect user settings, Node/npm, or Pi Web/Pi packages; do not enable administrator mode where same-account standard processes are untrusted.
- Updates are downloaded from GitHub over HTTPS and verified against the published SHA-256. Releases are unsigned, so SmartScreen may warn on first run.
- Logs rotate at 4 MB. In administrator mode, use an administrator console for `--stop` / `--quit`; the tray menu still works. If the log host is unavailable, a standard-user tray may write unredacted logs without rotation, while an administrator tray discards service output.
- **Allow connections from the local network** binds the service to all interfaces and opens the firewall port. There is no separate login, so anyone who can reach the PC can control Pi and read project files. Only enable it on a trusted network.

## Related projects

Pi Web Tray installs and launches [Pi Web](https://github.com/agegr/pi-web); when `InstallPi=1`, it also installs or updates [Pi](https://github.com/earendil-works/pi). It does not bundle or modify either project; report their issues to their own repositories.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Report vulnerabilities according to [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
