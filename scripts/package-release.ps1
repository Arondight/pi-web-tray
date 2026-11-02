# Build a release zip from an already compiled artifact directory.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-release.ps1
param(
    [string]$ArtifactDir = "artifacts"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$exe = Join-Path $ArtifactDir "pi-web-tray.exe"
if (-not (Test-Path $exe)) { throw "Missing $exe. Run build.cmd first." }

$version = (Get-Item $exe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($version)) { $version = "dev" }

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
    "config\pi-web-tray.ini"
) | Where-Object { Test-Path $_ }

# Include every generated culture folder (for example zh-Hans).
$resourceDirs = @(
    Get-ChildItem -Path $ArtifactDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "pi-web-tray.resources.dll") } |
        Select-Object -ExpandProperty FullName
)
$files = @($files) + $resourceDirs

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
