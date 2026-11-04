# Security Policy

## Supported versions

| Version | Supported           |
| ------- | ------------------- |
| 0.2.x   | Yes                 |
| < 0.2   | No (please upgrade) |

## Reporting a vulnerability

Please **do not** open a public GitHub issue for security problems.

Use the repository's **Security** tab and create a private security advisory ("Report a vulnerability"), or contact the maintainers through the address listed on the repository profile. Include:

- affected version (`.\pi-web-tray.exe --version`),
- Windows version and .NET Framework version,
- a minimal reproduction or proof of concept,
- impact assessment and any suggested fix.

You will receive an acknowledgement as soon as possible. Please allow time for a fix and a coordinated release before public disclosure.

## Scope and threat model

Pi Web Tray is a local desktop launcher. It:

- runs with the normal user's token by default (`asInvoker` in the manifest);
- runs elevated only when the user explicitly chooses "Run as administrator";
- starts `node` / `npm` as the current user and never requests credentials;
- reads `pi-web-tray.ini` and environment variables controlled by the current user;
- creates an `HKCU\...\Run` value or an on-logon scheduled task only when the user turns on start-at-login; the administrator task requires the current account to be an administrator and its name includes the user SID, so users do not overwrite each other's task;
- identifies the process that owns the configured port and reports a port conflict instead of treating or killing an unrelated listener as Pi Web;
- checks GitHub for updates only when the user selects **Update Pi Web Tray** (or opts into automatic checks), downloads the release archive over HTTPS, verifies its SHA-256 checksum, and starts an elevated helper only when the installation directory needs elevation and the user accepts the UAC prompt (or the tray is already elevated);
- stores its configuration and logs in the current user's `%LOCALAPPDATA%\pi-web-tray` by default; `PI_WEB_TRAY_PORTABLE=1` is an explicit opt-in that is not suitable for a shared directory;
- redacts URL user info and token query values from its logs, and rotates `server.log`, `install.log` and `update.log` at 4 MB; the last-resort `1>>` service-output fallback (used only when the log-host child process cannot start) writes unredacted output without rotation;
- does not contain telemetry or network listeners.

The tray intentionally trusts the configured `NodePath`, `ServerScript`, `WorkingDir`, `NpmRegistry`, `Proxy` and `InstallTag` values. Treat a writable configuration file as equivalent to code execution for the current user. Do not point these settings at untrusted paths or registries. Avoid embedding credentials in `NpmRegistry` or `Proxy`: the ini is plain text, and although the tray redacts common URL and query-token forms in its logs, it cannot guarantee that every third-party tool uses a redactable format. Prefer npm's own authentication configuration and a credential-free proxy URL where possible. The `Proxy` setting is used only for Pi Web Tray update checks and downloads; it is not exported to Node, npm or Pi Web child processes. Temporary SOCKS tunnels are bound to loopback and accepted only from the current Pi Web Tray process; they are closed when the proxy setting changes or the process exits.

Updates are staged in the user's data directory before an elevated helper replaces files in a protected installation directory. Only accept the UAC prompt when you started the update from the tray, and do not let untrusted programs write to the update staging directory while a prompt is pending. The helper refuses a target that is not a Pi Web Tray installation and re-checks a fingerprint of the staged files after the UAC prompt; these checks reduce the risk but do not replace code signing. The release file manifest is used only to remove files that were managed by the previous release; it is not an integrity check and does not replace the SHA-256 checksum.

Third-party components (Node.js, npm, Pi Web, Pi) are not part of this project and should be reported to their respective maintainers. Vulnerabilities in how this launcher invokes them are in scope.

## Windows 7 note

Windows 7 SP1 is out of Microsoft support. The tray is built against .NET Framework 4.8 and avoids post-Windows-7 APIs, but security fixes for the operating system and the runtime are the user's responsibility. Prefer Windows 10 1809 or newer.

Windows 7 and Windows 8 have no official Node.js 22 build. On those systems the **Download Node.js** menu item asks for confirmation and then opens the community build at <https://github.com/DeftKing/nodejs-win7>. That binary is not produced, signed or verified by this project; validate it yourself before use.
