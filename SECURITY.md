# Security Policy

## Supported versions

| Version | Supported           |
| ------- | ------------------- |
| 0.1.x   | Yes                 |
| < 0.1   | No (please upgrade) |

## Reporting a vulnerability

Please **do not** open a public GitHub issue for security problems.

Use the repository's **Security** tab and create a private security advisory
("Report a vulnerability"), or contact the maintainers through the address listed
on the repository profile. Include:

- affected version (`pi-web-tray.exe --version`),
- Windows version and .NET Framework version,
- a minimal reproduction or proof of concept,
- impact assessment and any suggested fix.

You will receive an acknowledgement as soon as possible. Please allow time for a
fix and a coordinated release before public disclosure.

## Scope and threat model

Pi Web Tray is a local desktop launcher. It:

- runs with the normal user's token by default (`asInvoker` in the manifest);
- runs elevated only when the user explicitly chooses "Run as administrator";
- starts `node` / `npm` as the current user and never requests credentials;
- reads `pi-web-tray.ini` and environment variables controlled by the current user;
- creates an `HKCU\...\Run` value or an on-logon scheduled task only when the user
  turns on start-at-login;
- checks GitHub for updates only when the user selects **Check for updates**, downloads
  the release archive over HTTPS, verifies its SHA-256 checksum, and starts an elevated
  helper only when the installation directory needs elevation and the user accepts the
  UAC prompt (or the tray is already elevated);
- does not contain telemetry or network listeners.

The tray intentionally trusts the configured `NodePath`, `ServerScript`,
`WorkingDir`, `NpmRegistry` and `InstallTag` values. Treat a writable configuration
file as equivalent to code execution for the current user. Do not point these
settings at untrusted paths or registries.

Updates are staged in the user's data directory before an elevated helper replaces
files in a protected installation directory. Only accept the UAC prompt when you
started the update from the tray, and do not let untrusted programs write to the update
staging directory while a prompt is pending. The helper refuses a target that is not a
Pi Web Tray installation and re-checks a fingerprint of the staged files after the UAC
prompt; these checks reduce the risk but do not replace code signing.

Third-party components (Node.js, npm, pi-web, pi) are not part of this project and
should be reported to their respective maintainers. Vulnerabilities in how this
launcher invokes them are in scope.

## Windows 7 note

Windows 7 SP1 is out of Microsoft support. The tray is built against .NET Framework
4.8 and avoids post-Windows-7 APIs, but security fixes for the operating system and
the runtime are the user's responsibility. Prefer Windows 10 1809 or newer.
