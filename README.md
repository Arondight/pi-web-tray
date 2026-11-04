# Pi Web Tray

**English** | [简体中文](README.zh-CN.md)

A universal Windows 7 to 11 harness for pi and pi-web. One tray app provides runtime detection, dependency installation and updates, service hosting and self-update.

- **One click** — installs and manages pi and pi-web; start or stop them with one click.
- **Visible state** — color-coded tray icon, notifications and live logs.
- **Start at login** — uses the standard Run key or an administrator scheduled task.
- **Port safe** — native process APIs identify the listener; unrelated programs are never killed.
- **Self-update** — checks for updates, updates the app and removes files from older releases.
- **Portable** — no installer, no third-party runtime dependencies.

## Requirements

| Component      | Requirement                                  |
| -------------- | -------------------------------------------- |
| Windows        | 7 SP1, 8, 8.1, 10 or 11 (64-bit recommended) |
| .NET Framework | 4.8 — see the Windows 7 note below           |
| Node.js        | 22.19 or later, with npm                     |

Windows 7 and Windows 8 have no official Node.js 22 build. **Download Node.js** asks for confirmation and then opens an unsigned community build; it is not produced or verified by this project. pi-web's terminal needs ConPTY (Windows 10 1809+), so the tray may run on legacy systems while the service or terminal is unavailable.

### Windows 7 runtime

Windows 7 SP1 needs **.NET Framework 4.8**; install it from:

<https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48>

Keep `pi-web-tray.exe.config` next to the executable.

Tray self-update also needs TLS 1.2; if GitHub cannot be reached, install the Windows 7 TLS 1.2 update (KB3140245):

<https://support.microsoft.com/help/3140245>

## Recommended software

Pi Web Tray does not require Python 3 or PowerShell, but installing them extends what pi can do.

### Python 3

Windows 10 and 11: use the official download page and pick the installer for your architecture (64-bit unless the system is 32-bit). Tick **Add python.exe to PATH**:

<https://www.python.org/downloads/>

Older Windows releases no longer receive current Python installers. The newest releases that still ship one:

| System      | Newest release with an installer | Official download page                                 |
| ----------- | -------------------------------- | ------------------------------------------------------ |
| Windows 7   | Python 3.8.10                    | https://www.python.org/downloads/release/python-3810/  |
| Windows 8   | Python 3.8.10                    | https://www.python.org/downloads/release/python-3810/  |
| Windows 8.1 | Python 3.13.17                   | https://www.python.org/downloads/release/python-31317/ |

### PowerShell

Windows 8.1, 10 and 11 can install the latest PowerShell from the official releases page:

<https://github.com/PowerShell/PowerShell/releases>

Windows 7 SP1 needs Windows Management Framework 5.1 to get Windows PowerShell 5.1:

<https://www.microsoft.com/en-us/download/details.aspx?id=54616>

Windows 8 cannot update PowerShell; only the built-in 3.0 is available.

## Quick start

1. Download and extract the release archive.
2. Run `.\pi-web-tray.exe`.

On first start the app checks the environment, installs missing dependencies, starts pi-web and opens the browser. If Node.js is missing, it explains the required version, offers the download page and exits; install Node.js and run it again.

Configuration and logs live next to the executable, or in `%LOCALAPPDATA%\pi-web-tray` for protected directories. The `pi-web-tray.files.txt` manifest is used by future updates to remove files that are no longer shipped; do not delete it.

## Tray menu

The icon color shows the state at a glance: green = running, grey = stopped, orange = busy, blue = prerequisites missing, red = error. The menu follows the service state:

```text
● Running · 127.0.0.1:30141 · administrator mode
---------------------------------------
Open Pi Web
Start service
Stop service
Restart service
---------------------------------------
Update pi-web and pi
Update this app
Download Node.js
---------------------------------------
Environment check
Settings...
Logs >
  View service log
  View install log
  View update log
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
About
Exit and stop pi-web
```

- **Update pi-web and pi** stops pi-web first, updates both packages when `InstallPiCli=1`, and restarts the service only if it was running. The progress window supports cancellation.
- A listener that is not pi-web is reported as a **port conflict**: the tray neither treats it as running nor stops or kills it; change `Port` in **Settings**.
- If pi-web stops unexpectedly, the tray reports it and restarts it when `AutoRestart=1`; `AutoCheckUpdates=1` checks for a new release after startup.

## Configuration

### Configuration file

`pi-web-tray.ini` is created on first start next to the executable, or in `%LOCALAPPDATA%\pi-web-tray` for protected directories. A commented sample is in [`config/pi-web-tray.ini`](config/pi-web-tray.ini).

| Key                | Default         | Description                                              |
| ------------------ | --------------- | -------------------------------------------------------- |
| `Port`             | `30141`         | pi-web listening port.                                   |
| `WorkingDir`       | `%USERPROFILE%` | Service working directory and pi's default project root. |
| `AutoInstall`      | `1`             | Install pi-web automatically when missing.               |
| `AutoRestart`      | `0`             | Restart pi-web after an unexpected stop.                 |
| `AutoCheckUpdates` | `0`             | Check for a tray update after startup.                   |
| `InstallPiCli`     | `1`             | Install the pi CLI together with pi-web.                 |
| `InstallTag`       | `latest`        | npm distribution tag or exact version.                   |
| `NpmRegistry`      | empty           | Custom registry, e.g. `https://registry.npmmirror.com`.  |
| `NodePath`         | empty           | Explicit `node.exe`; empty enables auto-detection.       |
| `ServerScript`     | empty           | Explicit `pi-web.js`; empty enables auto-detection.      |
| `Language`         | `auto`          | `auto`, `en`, `zh` (Simplified) or `zh-Hant`.            |
| `AutostartAdmin`   | `0`             | Admin rights for start at login (menu-managed).          |

Values support `%ENV%` expansion; quote values containing `;` or `#`; comments need whitespace before the marker.

### Environment variables

| Variable                     | Description                            |
| ---------------------------- | -------------------------------------- |
| `PI_WEB_TRAY_PORT`           | Overrides the configured port.         |
| `PI_WEB_TRAY_LANG`           | Overrides the UI language.             |
| `PI_WEB_TRAY_HOME`           | Overrides the data directory.          |
| `HTTP_PROXY` / `HTTPS_PROXY` | Used by npm and pi-web model requests. |

## Command line

```text
.\pi-web-tray.exe                   Start pi-web and open the browser.
.\pi-web-tray.exe --autostart       Start tray and service without a browser.
.\pi-web-tray.exe --tray-only       Start the tray only.
.\pi-web-tray.exe --start-no-open   Start the service without a browser.
.\pi-web-tray.exe --stop            Stop the service, keep the tray.
.\pi-web-tray.exe --quit            Stop the service and exit.
.\pi-web-tray.exe --check           Check the environment (0 = ready, 1 = missing).
.\pi-web-tray.exe --install         Install dependencies without the tray.
.\pi-web-tray.exe --dry-run         With --install, print the npm commands only.
.\pi-web-tray.exe --diag            Write a diagnostic snapshot to diag.txt.
.\pi-web-tray.exe --version         Print the version.
.\pi-web-tray.exe --help            Show command-line help.
```

Unknown arguments return exit code 2.

## Build from source

Requires Windows 11 and Visual Studio 2026 with the **.NET desktop development** workload (it includes the .NET Framework 4.8 targeting pack).

```bat
git clone https://github.com/Arondight/pi-web-tray.git
cd .\pi-web-tray
.\build.cmd
```

## Security and limitations

- Runs with the current user token (`asInvoker`); elevation only on request. No telemetry and no network listener.
- Updates are downloaded from GitHub over HTTPS and verified against the published SHA-256; the helper also validates the target installation and a staging fingerprint. Releases are unsigned, so SmartScreen may warn on first run.
- Admin start-at-login requires an admin account and is machine-wide. If the tray runs elevated, use an elevated console for `--stop` / `--quit`; the tray menu always works.

## Related projects

Pi Web Tray installs and launches [pi-web](https://github.com/agegr/pi-web) (`@agegr/pi-web`) and [pi](https://github.com/earendil-works/pi) (`@earendil-works/pi-coding-agent`). It does not bundle or modify them; report their issues to their own repositories.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Report vulnerabilities according to [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
