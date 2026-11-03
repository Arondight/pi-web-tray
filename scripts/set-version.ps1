# Set the Pi Web Tray product version.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\set-version.ps1 0.2.0
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\set-version.ps1 v0.2.0
#
# The script updates the version fields in src/PiWebTray.csproj and the application
# identity in src/app.manifest. It also updates the bug report version placeholder when
# it contains a plain version. No git commands are run.
#
# Version format: major.minor.patch (each part 0-65535). A leading "v" is accepted.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Version
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$projectPath = Join-Path $root "src\PiWebTray.csproj"
$manifestPath = Join-Path $root "src\app.manifest"
$issuePath = Join-Path $root ".github\ISSUE_TEMPLATE\bug_report.yml"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-TextFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "File not found: $Path"
    }
    return [System.IO.File]::ReadAllText($Path)
}

function Write-TextFileIfChanged {
    param(
        [string]$Path,
        [string]$OldText,
        [string]$NewText
    )

    # Keep the repository rule that every text file ends with a newline. Follow the
    # dominant line ending of the file so CRLF sources stay CRLF and LF sources stay LF.
    if (-not $NewText.EndsWith("`n")) {
        $ending = "`n"
        if ($NewText.Contains("`r`n")) { $ending = "`r`n" }
        $NewText += $ending
    }

    if ($NewText -ceq $OldText) {
        return $false
    }
    [System.IO.File]::WriteAllText($Path, $NewText, $utf8NoBom)
    return $true
}

# Replace exactly one element such as <Version>1.2.3</Version>.
function Set-XmlElementValue {
    param(
        [string]$Text,
        [string]$Element,
        [string]$Value,
        [string]$Path
    )

    $pattern = "<$Element>[^<]*</$Element>"
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) {
        throw "Expected one <$Element> element in $Path, found none."
    }
    if ($match.NextMatch().Success) {
        throw "Expected exactly one <$Element> element in $Path, found more than one."
    }

    $replacement = "<$Element>$Value</$Element>"
    return $Text.Substring(0, $match.Index) + $replacement +
        $Text.Substring($match.Index + $match.Length)
}

# Normalize and validate the requested version.
$requested = $Version.Trim()
if ($requested -match '^[vV]') {
    $requested = $requested.Substring(1)
}
if ($requested -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid version '$Version'. Expected major.minor.patch, for example 0.2.0."
}

$parts = @()
foreach ($part in $requested.Split('.')) {
    $number = 0
    if (-not [int]::TryParse($part, [ref]$number)) {
        throw "Invalid version '$Version': '$part' is not a number."
    }
    if ($number -lt 0 -or $number -gt 65535) {
        throw "Invalid version '$Version': each part must be between 0 and 65535."
    }
    $parts += $number
}

$version3 = $parts -join "."
$version4 = "$version3.0"

# Read everything before writing anything, so a missing field cannot leave a partial bump.
$projectText = Read-TextFile $projectPath
$manifestText = Read-TextFile $manifestPath

$currentMatch = [regex]::Match($projectText, '<Version>([^<]*)</Version>')
if (-not $currentMatch.Success) {
    throw "Could not read the current <Version> from $projectPath."
}
$currentVersion = $currentMatch.Groups[1].Value.Trim()

$newProjectText = $projectText
$newProjectText = Set-XmlElementValue $newProjectText "Version" $version3 $projectPath
$newProjectText = Set-XmlElementValue $newProjectText "AssemblyVersion" $version4 $projectPath
$newProjectText = Set-XmlElementValue $newProjectText "FileVersion" $version4 $projectPath
$newProjectText = Set-XmlElementValue $newProjectText "InformationalVersion" $version3 $projectPath

$manifestPattern = '<assemblyIdentity\b(?=[^>]*\bname="PiWebTray\.app")[^>]*/>'
$manifestMatch = [regex]::Match($manifestText, $manifestPattern)
if (-not $manifestMatch.Success) {
    throw "Could not find the PiWebTray.app assemblyIdentity in $manifestPath."
}
if ($manifestMatch.NextMatch().Success) {
    throw "Found more than one PiWebTray.app assemblyIdentity in $manifestPath."
}
$manifestVersionPattern = '\bversion="[^"]*"'
if ([regex]::Matches($manifestMatch.Value, $manifestVersionPattern).Count -ne 1) {
    throw "Could not find exactly one version attribute in the PiWebTray.app assemblyIdentity."
}
$manifestReplacement = [regex]::Replace(
    $manifestMatch.Value, $manifestVersionPattern, 'version="' + $version4 + '"')
$newManifestText = $manifestText.Substring(0, $manifestMatch.Index) + $manifestReplacement +
    $manifestText.Substring($manifestMatch.Index + $manifestMatch.Length)

# The issue template contains a version hint. Update a plain semver placeholder;
# a custom or non-version placeholder is left alone and reported below.
$issueText = $null
$newIssueText = $null
$issueChanged = $false
$issueMatched = $false
if (Test-Path -LiteralPath $issuePath) {
    $issueText = Read-TextFile $issuePath
    $issuePattern = '(?m)^(\s*placeholder:\s*")[0-9]+\.[0-9]+\.[0-9]+(")\s*$'
    $issueMatch = [regex]::Match($issueText, $issuePattern)
    if ($issueMatch.Success -and -not $issueMatch.NextMatch().Success) {
        $issueMatched = $true
        $newLine = [regex]::Replace($issueMatch.Value, '[0-9]+\.[0-9]+\.[0-9]+', $version3)
        $newIssueText = $issueText.Substring(0, $issueMatch.Index) + $newLine +
            $issueText.Substring($issueMatch.Index + $issueMatch.Length)
        $issueChanged = $newIssueText -cne $issueText
    }
}

$projectChanged = Write-TextFileIfChanged $projectPath $projectText $newProjectText
$manifestChanged = Write-TextFileIfChanged $manifestPath $manifestText $newManifestText
$issueWritten = $false
if ($null -ne $issueText) {
    $issueContent = $issueText
    if ($null -ne $newIssueText) { $issueContent = $newIssueText }
    $issueWritten = Write-TextFileIfChanged $issuePath $issueText $issueContent
}

if ($projectChanged) {
    Write-Host "[ok] src\PiWebTray.csproj : $currentVersion -> $version3 (assembly/file $version4)"
} else {
    Write-Host "[i] src\PiWebTray.csproj already at $version3"
}

if ($manifestChanged) {
    Write-Host "[ok] src\app.manifest : application identity $version4"
} else {
    Write-Host "[i] src\app.manifest already at $version4"
}

if ($issueChanged) {
    Write-Host "[ok] .github\ISSUE_TEMPLATE\bug_report.yml : version placeholder $version3"
} elseif ($issueMatched) {
    Write-Host "[i] .github\ISSUE_TEMPLATE\bug_report.yml already at $version3"
} elseif ($issueWritten) {
    Write-Host "[ok] .github\ISSUE_TEMPLATE\bug_report.yml : added the missing final newline"
} elseif ($null -ne $issueText) {
    Write-Host "[i] .github\ISSUE_TEMPLATE\bug_report.yml placeholder was not changed"
} else {
    Write-Host "[i] .github\ISSUE_TEMPLATE\bug_report.yml not found; skipped"
}

Write-Host ""
Write-Host "Version set to $version3. No git commands were run."
Write-Host "[i] Review SECURITY.md supported versions and the release notes before tagging."
