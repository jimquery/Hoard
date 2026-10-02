<#
.SYNOPSIS
    Tags the current version and publishes it as a GitHub release with the MSI attached.

.DESCRIPTION
    Reads the version from Directory.Build.props, checks that publish\Hoard-<version>-x64.msi exists (build it
    first with build-installer.ps1), extracts that version's section from CHANGELOG.md as the release notes,
    creates an annotated tag v<version>, pushes it, and runs `gh release create` with the MSI as an asset.
    Requires the GitHub CLI (winget install GitHub.cli) and a one-time `gh auth login`.

.EXAMPLE
    .\installer\build-installer.ps1
    .\installer\release.ps1
#>
[CmdletBinding()]
param(
    [switch]$Draft
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "The GitHub CLI is required: winget install GitHub.cli, then gh auth login." }

$version = ([xml](Get-Content "Directory.Build.props")).Project.PropertyGroup.Version
$msi = Join-Path $root "publish\Hoard-$version-x64.msi"
if (-not (Test-Path $msi)) { throw "$msi is missing. Run .\installer\build-installer.ps1 first." }

# Release notes = this version's section of the changelog.
$lines = Get-Content "CHANGELOG.md"
$start = ($lines | Select-String -Pattern "^## $([regex]::Escape($version))\b" | Select-Object -First 1).LineNumber
if (-not $start) { throw "CHANGELOG.md has no '## $version' section." }
$end = ($lines | Select-String -Pattern "^## " | Where-Object LineNumber -gt $start | Select-Object -First 1).LineNumber
$notes = if ($end) { $lines[$start..($end - 2)] } else { $lines[$start..($lines.Count - 1)] }
$notesFile = Join-Path $env:TEMP "hoard-release-$version.md"
$notes | Set-Content $notesFile -Encoding UTF8

if (git status --porcelain) { throw "The working tree has uncommitted changes. Commit them first." }
if (-not (git tag --list "v$version")) {
    git tag -a "v$version" -m "Hoard $version"
    git push origin "v$version"
}

$args = @("release", "create", "v$version", $msi, "--title", "Hoard $version", "--notes-file", $notesFile)
if ($Draft) { $args += "--draft" }
& gh @args
Write-Host "Released Hoard $version." -ForegroundColor Green
