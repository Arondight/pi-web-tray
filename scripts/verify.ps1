# Pi Web Tray - smoke tests used by CI and by contributors before a release.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1
#        powershell ... -File scripts\verify.ps1 -ExePath artifacts\pi-web-tray.exe
param(
    [string]$ExePath = "artifacts\pi-web-tray.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath. Run build.cmd first."
}
$exe = (Resolve-Path $ExePath).Path

# The tray is a WinExe, so PowerShell does not wait for it and $LASTEXITCODE stays unset.
# Start-Process -Wait gives us a reliable exit code and captured output.
function Invoke-Exe {
    param([string[]]$Arguments)

    $stdout = [IO.Path]::GetTempFileName()
    $stderr = [IO.Path]::GetTempFileName()
    try {
        $process = Start-Process -FilePath $exe -ArgumentList $Arguments -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr

        $out = ""
        if (Test-Path $stdout) { $out += (Get-Content $stdout -Raw -ErrorAction SilentlyContinue) }
        if (Test-Path $stderr) { $out += (Get-Content $stderr -Raw -ErrorAction SilentlyContinue) }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output   = $out
        }
    }
    finally {
        Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Checked {
    param(
        [string]$Name,
        [string[]]$Arguments,
        [int[]]$AllowedExitCodes = @(0)
    )

    $result = Invoke-Exe -Arguments $Arguments
    if ($AllowedExitCodes -notcontains $result.ExitCode) {
        throw "$Name failed with exit code $($result.ExitCode)`n$($result.Output)"
    }
    Write-Host "[ok] $Name"
    return $result.Output
}

$version = Invoke-Checked "version" @("--version")
if ($version -notmatch "Pi Web Tray") { throw "Unexpected version output: $version" }

$help = Invoke-Checked "help" @("--help")
if ($help -notmatch "--install") {
    throw "Help output is missing expected options"
}

# --check: 0 = ready, 1 = Node/npm/pi-web missing (expected on a clean CI runner)
Invoke-Checked "environment check" @("--check") @(0, 1) | Out-Null
# 0 = command printed, 2 = Node/npm missing (the dry run still reports the environment)
Invoke-Checked "install command assembly" @("--install", "--dry-run") @(0, 2) | Out-Null
Invoke-Checked "UI string completeness" @("--lang-check") | Out-Null
Invoke-Checked "unknown argument rejection" @("--bogus") @(2) | Out-Null
Invoke-Checked "dry-run validation" @("--dry-run") @(2) | Out-Null

$originalLang = $env:PI_WEB_TRAY_LANG
try {
    $env:PI_WEB_TRAY_LANG = "en"
    Invoke-Checked "environment check (en)" @("--check") @(0, 1) | Out-Null
    $env:PI_WEB_TRAY_LANG = "zh"
    Invoke-Checked "environment check (zh)" @("--check") @(0, 1) | Out-Null
    $env:PI_WEB_TRAY_LANG = "zh-Hant"
    Invoke-Checked "environment check (zh-Hant)" @("--check") @(0, 1) | Out-Null
}
finally {
    if ($null -eq $originalLang) { Remove-Item Env:\PI_WEB_TRAY_LANG -ErrorAction SilentlyContinue }
    else { $env:PI_WEB_TRAY_LANG = $originalLang }
}

Write-Host ""
Write-Host "All smoke tests passed."
