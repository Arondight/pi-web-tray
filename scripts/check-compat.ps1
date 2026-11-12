# Verify that the build still targets .NET Framework 4.8 and produces an x64
# IL-only executable for Windows 7 to 11.
# Usage: pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-compat.ps1
# PowerShell 7+ is required for the PE/CLR header check.
param(
    [string]$ArtifactDir = "artifacts",
    [string]$ProjectPath = "src/PiWebTray.csproj",
    [string]$ManifestPath = "src/app.manifest"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Assert-Compatibility {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) { throw $Message }
}

$project = [xml](Get-Content $ProjectPath -Raw)
function Get-ProjectProperty {
    param([string]$Name)

    foreach ($group in @($project.Project.PropertyGroup)) {
        $value = $group.$Name
        if ($null -ne $value -and "$value" -ne "") { return "$value" }
    }
    return $null
}

$targetFramework = Get-ProjectProperty "TargetFramework"
$platformTarget = Get-ProjectProperty "PlatformTarget"
$prefer32Bit = Get-ProjectProperty "Prefer32Bit"

Assert-Compatibility ($targetFramework -eq "net48") `
    "PiWebTray.csproj must target net48 (found '$targetFramework')"
Assert-Compatibility ($platformTarget -eq "x64") `
    "PiWebTray.csproj must use PlatformTarget x64 (found '$platformTarget')"
Assert-Compatibility ($prefer32Bit -eq "false") `
    "PiWebTray.csproj must set Prefer32Bit=false (found '$prefer32Bit')"

$manifest = Get-Content $ManifestPath -Raw
$supportedOs = @{
    "Windows 7"     = "35138b9a-5d96-4fbd-8e2d-a2440225f93a"
    "Windows 8.1"   = "1f676c76-80e1-4239-95bb-83d0f6d0da78"
    "Windows 10/11" = "8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a"
}
foreach ($entry in $supportedOs.GetEnumerator()) {
    Assert-Compatibility ($manifest -match [regex]::Escape($entry.Value)) `
        "app.manifest is missing the $($entry.Key) supportedOS entry"
}
Assert-Compatibility ($manifest -match 'level="asInvoker"') `
    "app.manifest must request asInvoker"
Assert-Compatibility ($manifest -match 'dpiAware[^>]*>true') `
    "app.manifest must request system DPI awareness"
Assert-Compatibility ($manifest -match 'Microsoft\.Windows\.Common-Controls') `
    "app.manifest must reference Common Controls 6.0"

$exe = Join-Path $ArtifactDir "pi-web-tray.exe"
Assert-Compatibility (Test-Path $exe) "Missing $exe. Run .\build.cmd first."

$configPath = Join-Path $ArtifactDir "pi-web-tray.exe.config"
Assert-Compatibility (Test-Path $configPath) "Missing $configPath"
$config = Get-Content $configPath -Raw
Assert-Compatibility ($config -match 'sku="\.NETFramework,Version=v4\.8"') `
    "pi-web-tray.exe.config does not select .NET Framework 4.8"

foreach ($culture in @("zh-Hans", "zh-Hant")) {
    $resource = Join-Path $ArtifactDir "$culture\pi-web-tray.resources.dll"
    Assert-Compatibility (Test-Path $resource) "Missing satellite resource: $resource"
}

# Inspect the CLR header. An x64 IL-only image has ILONLY set and neither
# 32BITREQ nor 32BITPREF. The PE machine is AMD64 for x64 images.
if ($PSVersionTable.PSEdition -eq "Core") {
    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::OpenRead((Resolve-Path $exe).Path)
    try {
        $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $headers = $reader.PEHeaders
            Assert-Compatibility ($headers.CoffHeader.Machine -eq `
                [System.Reflection.PortableExecutable.Machine]::Amd64) `
                "pi-web-tray.exe is not a PE32+/x64 image"

            $corHeader = $headers.CorHeader
            Assert-Compatibility ($null -ne $corHeader) `
                "pi-web-tray.exe has no CLR header"

            $flags = [int]$corHeader.Flags
            Assert-Compatibility (($flags -band 0x1) -ne 0) `
                "pi-web-tray.exe is not IL-only"
            Assert-Compatibility (($flags -band 0x2) -eq 0) `
                "pi-web-tray.exe requests 32-bit execution (32BITREQ)"
            Assert-Compatibility (($flags -band 0x20000) -eq 0) `
                "pi-web-tray.exe prefers 32-bit execution (32BITPREF)"
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}
else {
    throw "PowerShell 7 or later is required to verify the PE/CLR header. Run this script with pwsh."
}

Write-Host "[ok] target framework net48"
Write-Host "[ok] x64 IL-only executable"
Write-Host "[ok] Windows 7 to 11 manifest"
Write-Host "[ok] .NET Framework 4.8 runtime configuration"
Write-Host "[ok] zh-Hans and zh-Hant satellite resources"
