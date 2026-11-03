# Contributing to Pi Web Tray

Thanks for taking the time to contribute. This project is a Windows tray application with
no third-party runtime dependencies; the goal is to keep it easy to build, easy to audit
and compatible with **Windows 7 SP1, 8.1, 10 and 11** (with .NET Framework 4.8 installed).

## Before you start

- Read [README.md](README.md) and the source comments; the hardware/OS constraints are
  listed there.
- For a bug, open an issue with the output of `pi-web-tray.exe --diag` (redact user
  names, paths and tokens) and the relevant tail of `server.log` / `install.log`.
- For a security issue, **do not open a public issue**; follow [SECURITY.md](SECURITY.md).

## Development environment

| Tool           | Requirement                                                                         |
| -------------- | ----------------------------------------------------------------------------------- |
| Windows        | 10 or 11 for development (the product targets Windows 7 SP1+)                       |
| Visual Studio  | 2022 or 2026, **.NET desktop development** workload                                 |
| .NET Framework | 4.8 targeting pack                                                                  |
| .NET SDK       | Optional alternative to VS; 8.0 or newer plus the .NET Framework 4.8 targeting pack |

Open `PiWebTray.sln` in Visual Studio, or build from a terminal:

```bat
build.cmd
```

Close any running `pi-web-tray.exe` before building; the script does not terminate processes.

The output is copied to `artifacts\pi-web-tray.exe`, `artifacts\pi-web-tray.exe.config`
and the generated culture folders (for example `artifacts\zh-Hans` and
`artifacts\zh-Hant`). Build with `build.cmd --debug` for a Debug build.

## Tests

Tests in `tests/PiWebTray.Tests` cover the pure logic and the construction of the two-table
environment-check window; the CI smoke script exercises the built CLI:

```powershell
.\scripts\verify.ps1 -ExePath artifacts\pi-web-tray.exe
```

Useful individual checks:

```powershell
dotnet test tests\PiWebTray.Tests\PiWebTray.Tests.csproj -c Release
artifacts\pi-web-tray.exe --lang-check
artifacts\pi-web-tray.exe --check
artifacts\pi-web-tray.exe --install --dry-run
```

Before a release, manually check:

- tray icon states: checking / needs Node / installing / starting / running / stopping / failed;
- install start, finish and failure notifications;
- start-at-login in both normal and admin mode;
- elevated takeover;
- high-DPI scaling;
- Windows 7 SP1 + .NET Framework 4.8 when changing OS-facing code.

## Formatting

- C# and project files: `dotnet format PiWebTray.sln --no-restore`
- Documentation (Markdown, YAML, JSON):
  `prettier --write "**/*.{md,yml,yaml,json}"`

Run both before committing. `.prettierignore` excludes C# sources, build output, binary
assets and configuration files; `dotnet format` uses `.editorconfig` and
`Directory.Build.props`.

## Coding guidelines

- Keep the application dependency-free: **no NuGet packages in `src`** and no third-party
  runtime DLLs. Test-only packages under `tests/` are allowed.
- Target `.NET Framework 4.8` only. Do not use APIs that are unavailable on
  Windows 7 SP1 or .NET Framework 4.8 (for example `System.Text.Json`,
  `TaskDialog`, or WinRT APIs).
- Do not add a manifest DPI mode that Windows 7 cannot parse. The current
  `dpiAware=true` setting is deliberate.
- All user-visible text lives in `src/Resources/Strings.resx` (English neutral),
  `src/Resources/Strings.zh-Hans.resx` (Simplified Chinese) and
  `src/Resources/Strings.zh-Hant.resx` (Traditional Chinese). Add all keys and run
  `--lang-check` afterwards.
- UI work belongs on the WinForms thread. Slow operations (probe, npm, `schtasks`,
  HTTP checks) stay on background threads and report through locked fields.
- Catch exceptions at the boundary (UI action, worker callback), not around every
  line. Never silently swallow an exception that leaves the UI in a lying state.
- Keep comments in English and write them for the next maintainer.
- Prefer small, focused commits with Conventional Commit prefixes
  (`fix:`, `feat:`, `docs:`, `build:`, `chore:`).

## Pull requests

1. Fork the repository and create a topic branch.
2. Make the change and run `build.cmd --no-pause` plus `scripts\verify.ps1`.
3. If you touched the UI, test the affected tray states manually.
4. Describe user-visible changes in the pull request / GitHub release notes.
5. Open a pull request; fill in the template and mention the issue it closes.

## Versioning and releases

- Version lives in `src/PiWebTray.csproj` (`Version`, `AssemblyVersion`,
  `FileVersion`, `InformationalVersion`) and in the application identity in
  `src/app.manifest`.
- Before tagging, run
  `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\set-version.ps1 <version>`
  to update those files. The script accepts `major.minor.patch` (optionally prefixed
  with `v`) and does not run any git commands.
- Tags use `v<major>.<minor>.<patch>`; the release workflow builds, tests and
  publishes a zip with the exe, its `.config`, satellite resource folders (for example
  `zh-Hans` / `zh-Hant`), README files, LICENSE and the sample ini.
- Review `SECURITY.md` supported versions and the release notes before tagging.
- Code signing is optional and is not configured in the repository.

## License

By contributing, you agree that your contribution is licensed under the MIT
License in [LICENSE](LICENSE).
