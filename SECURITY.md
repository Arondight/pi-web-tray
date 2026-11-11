# Security Policy

## Supported versions

| Version | Supported           |
| ------- | ------------------- |
| 0.4.x   | Yes                 |
| < 0.4   | No (please upgrade) |

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
- runs elevated only when the user explicitly chooses "Run as administrator" or enables administrator start-at-login;
- starts `node` / `npm` with the current process token and never requests credentials; elevated node/npm children have user-controlled Node/npm/.NET injection variables removed; before a UAC launch the tray also removes .NET profiler/startup-hook variables (`COR_*`, `CORECLR_*`, `COMPlus_*`, `DOTNET_*`) from its process environment on a best-effort basis, but other inherited environment variables remain; npm dependency commands run from the user's home directory, so npm's normal user-level `.npmrc` is used and a project-local `.npmrc` in a separate `WorkingDir` is not read; the Pi Web install step explicitly enables required lifecycle scripts, while the Pi CLI step uses `--ignore-scripts`;
- reads `pi-web-tray.ini` and environment variables controlled by the current user;
- creates an `HKCU\...\Run` value or an on-logon scheduled task only when the user turns on start-at-login; the administrator task is created only after UAC approval, only when the installation path and all of its ancestors cannot be replaced by standard users, and only when the current Windows account belongs to the Administrators group (standard-user over-the-shoulder UAC is refused); its name includes the user SID and named-instance suffix, so users and instances do not overwrite each other's task; its logon trigger is scoped to the current user rather than every logging-on account, and the task is allowed to start and continue on battery power; the legacy `PiWebTray` task is audited and removed when autostart is next changed;
- identifies the process that owns the configured port and reports a port conflict instead of treating or killing an unrelated listener as Pi Web;
- delegates URL/folder opening to Explorer when the tray is elevated, so a user-controlled protocol or file-association handler is not resolved with the tray's elevated token when possible, and refuses desktop-shortcut creation in elevated mode to avoid loading a user-controlled COM registration;
- checks GitHub for updates only when the user selects **Check for updates** or **Update available** (or opts into automatic checks), downloads the release archive over HTTPS, verifies its SHA-256 checksum, and starts an elevated helper only when the installation directory needs elevation and the user accepts the UAC prompt (or the tray is already elevated); the helper copies the verified archive into a target snapshot, re-hashes that copy against the published checksum, extracts it there, and replaces files from the extracted snapshot. The user-writable staging tree is not trusted. The legacy staging-hash path remains only for compatibility with older helpers. The snapshot is protected when the installation directory is protected; in a portable/user-writable installation it has the same write permissions as the installation; the checksum is not an authenticity signature;
- stores its configuration and logs in the current user's `%LOCALAPPDATA%\pi-web-tray` by default; `PI_WEB_TRAY_PORTABLE=1` is an explicit opt-in that is not suitable for a shared directory;
- redacts URL user info and token query values from its logs, and rotates `server.log`, `install.log` and `update.log` at 4 MB; the last-resort `1>>` service-output fallback (used only when the log-host child process cannot start) writes unredacted output without rotation;
- does not expose a control service or telemetry; when a SOCKS5 proxy is configured, a temporary random loopback listener is opened only for update traffic and is restricted to the current process.

The tray refuses to create an administrator logon task when its own installation directory, any folder in its path, or any file the elevated process loads is writable or replaceable by standard users, or when the current account is not an administrator, and warns once about a pre-existing task in such a directory. Moving the program to a protected location such as Program Files is required when a parent folder is user-writable; the ACL repair refuses such a path and refuses to run recursively through a child junction. Manual elevation, elevated dependency installs, and elevated self-updates are also refused for a user-writable installation. These checks are deliberately limited to the tray installation: the elevated task also runs user-configurable Node.js/npm/Pi/Pi Web paths, configuration, working directories and inherited environment variables, so those remain part of the administrator attack surface. Admin start-at-login is a convenience feature, not a security boundary; do not enable it on a machine where the same account's non-elevated processes are untrusted.

The tray intentionally trusts the configured `NodePath`, `WorkingDir`, `NpmRegistry`, `Proxy` and `InstallTag` values. Treat a writable configuration file as equivalent to code execution for the current user, and, when the tray is elevated, for the administrator token as well. Protecting the tray installation directory does not protect `%LOCALAPPDATA%` configuration or the user-installed Node/npm/Pi/Pi Web packages. Do not point these settings at untrusted paths or registries. Avoid embedding credentials in `NpmRegistry` or `Proxy`: the ini is plain text, and although the tray redacts common URL and query-token forms in its logs, it cannot guarantee that every third-party tool uses a redactable format. Prefer npm's own authentication configuration and a credential-free proxy URL where possible. The `Proxy` setting is used only for Pi Web Tray update checks and downloads; an empty value uses the system proxy. It is not exported to Node, npm or Pi Web child processes. Temporary SOCKS tunnels are bound to loopback and accepted only from the current Pi Web Tray process; they are closed when the proxy setting changes or the process exits.

Updates are downloaded and verified in the user's data directory before an elevated helper replaces files in a protected installation directory. Only accept the UAC prompt when you started the update from the tray. The helper refuses a target that is not the helper's own Pi Web Tray installation, accepts only a fixed `update.log` name under the data or installation directory and the data-directory update layout, copies the verified archive into a protected target snapshot, re-hashes that copy against the published checksum, and extracts it there before replacing files. A user-writable staging tree or zip is therefore not trusted; the legacy staging-hash argument is retained only for compatibility with older helpers. Download size is bounded, redirects must end at a trusted GitHub host, checksum filenames must match the selected archive, and extraction limits the number and expanded size of entries. A configured log or state path that is a reparse point is not followed by the tray's writers. These checks close the download-to-apply tamper window, but they do not authenticate the caller or the archive's publisher; because releases are unsigned, a local process that can obtain UAC approval can still ask the helper to install an arbitrary staged build. The release file manifest is used only to remove files that were managed by the previous release; it is not an integrity check and does not replace the SHA-256 checksum.

Third-party components (Node.js, npm, Pi Web, Pi) are not part of this project and should be reported to their respective maintainers. Vulnerabilities in how this launcher invokes them are in scope.

## Windows 7 note

Windows 7 SP1 is out of Microsoft support. The tray is built against .NET Framework 4.8 and avoids post-Windows-7 APIs, but security fixes for the operating system and the runtime are the user's responsibility. Prefer Windows 10 1809 or newer.

Windows 7 and Windows 8 have no official Node.js 22 build. On those systems the **Download Node.js** menu item asks for confirmation and then opens the community build at <https://github.com/DeftKing/nodejs-win7>. That binary is not produced, signed or verified by this project; validate it yourself before use.
