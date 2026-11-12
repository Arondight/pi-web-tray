# Build a release zip from an already compiled artifact directory.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-release.ps1
param(
    [string]$ArtifactDir = "artifacts"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$exe = Join-Path $ArtifactDir "pi-web-tray.exe"
if (-not (Test-Path $exe)) { throw "Missing $exe. Run .\build.cmd first." }

$version = (Get-Item $exe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($version)) { $version = "dev" }

# Refuse to package a stale artifact after a version-only source bump.
$projectText = Get-Content "src\PiWebTray.csproj" -Raw
$projectVersion = [regex]::Match($projectText, '<Version>([^<]+)</Version>').Groups[1].Value.Trim()
if ([string]::IsNullOrWhiteSpace($projectVersion)) {
    throw "Could not read <Version> from src\PiWebTray.csproj."
}
if ($version -ne $projectVersion) {
    throw "Artifact version '$version' does not match source version '$projectVersion'. Run .uild.cmd first."
}

foreach ($culture in @("zh-Hans", "zh-Hant")) {
    $resource = Join-Path $ArtifactDir "$culture\pi-web-tray.resources.dll"
    if (-not (Test-Path $resource)) { throw "Missing culture resource: $resource" }
}

$zip = Join-Path $ArtifactDir "pi-web-tray-$version-win.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

$files = @(
    $exe,
    (Join-Path $ArtifactDir "pi-web-tray.exe.config"),
    "README.md",
    "README.zh-CN.md",
    "CONTRIBUTING.md",
    "CODE_OF_CONDUCT.md",
    "SECURITY.md",
    "LICENSE",
    "config"
) | Where-Object { Test-Path $_ }

# Include every generated culture folder (for example zh-Hans).
$resourceDirs = @(
    Get-ChildItem -Path $ArtifactDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "pi-web-tray.resources.dll") }
)
$files = @($files) + @($resourceDirs | Select-Object -ExpandProperty FullName)

# Record the files owned by the release. The updater uses this list to remove files
# that disappear in a later release; user settings and logs are never listed.
$manifestName = "pi-web-tray.files.txt"
$manifestPath = Join-Path $ArtifactDir $manifestName
$manifestEntries = New-Object System.Collections.Generic.List[string]
foreach ($relative in @(
    "pi-web-tray.exe",
    "pi-web-tray.exe.config",
    "README.md",
    "README.zh-CN.md",
    "CONTRIBUTING.md",
    "CODE_OF_CONDUCT.md",
    "SECURITY.md",
    "LICENSE",
    "config/pi-web-tray.ini"
)) {
    $candidate = Join-Path $ArtifactDir $relative
    if (-not (Test-Path $candidate)) { $candidate = Join-Path $root $relative }
    if (Test-Path $candidate) { $manifestEntries.Add($relative) }
}
foreach ($directory in $resourceDirs) {
    $manifestEntries.Add($directory.Name + "/pi-web-tray.resources.dll")
}
[IO.File]::WriteAllText($manifestPath, (($manifestEntries | Sort-Object) -join "`n") + "`n",
    (New-Object System.Text.UTF8Encoding($false)))
$files = @($files) + $manifestPath

Compress-Archive -Path $files -DestinationPath $zip -Force

$hashFile = "$zip.sha256"
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $bytes = [System.IO.File]::ReadAllBytes($zip)
    $hash = ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) -join ""
}
finally {
    $sha.Dispose()
}
Set-Content -Path $hashFile -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ASCII
Write-Host "[ok] $zip"
Write-Host "[ok] $hashFile"
