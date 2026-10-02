<#
.SYNOPSIS
    Updates an installed Hoard in place with the current source, copying only the files that changed.

.DESCRIPTION
    For changes that need no new schema migration, no installer change and no service re-registration — a new
    report, a UI fix — the MSI is the wrong tool: a new version triggers a major upgrade that removes and re-lays
    every file, MySQL included. This script instead:
      1. publishes Hoard.Desktop and Hoard.Service exactly as build-installer.ps1 does (Release, win-x64,
         self-contained), into installer\.patch;
      2. compares every published file with the installed copy by hash;
      3. stops the Hoard sync service if it is running, copies the files that differ into
         C:\Program Files\Hoard\Desktop and \Service, and starts the service again.
    The MSI's own record (Add/Remove Programs) keeps the version it installed; Settings › About shows the
    assembly version actually running. The next full MSI upgrade replaces everything anyway.

    Run from an elevated PowerShell (Program Files is protected) with the Hoard desktop app closed.

.PARAMETER InstallRoot
    Where Hoard is installed. Defaults to C:\Program Files\Hoard.

.PARAMETER WhatIf
    List what would be copied without copying anything.

.EXAMPLE
    .\installer\patch-installed.ps1
    .\installer\patch-installed.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramFiles "Hoard")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root    = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $PSScriptRoot ".patch"

function Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $WhatIfPreference) { throw "Run this from an elevated PowerShell: $InstallRoot is protected." }
if (-not (Test-Path (Join-Path $InstallRoot "Desktop\Hoard.exe"))) { throw "Hoard is not installed at $InstallRoot (no Desktop\Hoard.exe)." }
if (Get-Process -Name Hoard -ErrorAction SilentlyContinue) { throw "Close the Hoard desktop app first; its files are in use." }

$props   = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = $props.Project.PropertyGroup.Version
Step "Publishing Hoard $version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
foreach ($pair in @(@{ Project = "src\Hoard.Desktop\Hoard.Desktop.csproj"; Target = "Desktop" },
                    @{ Project = "src\Hoard.Service\Hoard.Service.csproj"; Target = "Service" })) {
    dotnet publish (Join-Path $root $pair.Project) -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -o (Join-Path $staging $pair.Target) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($pair.Project)." }
}

Step "Comparing with $InstallRoot"
$changes = @()
foreach ($target in @("Desktop", "Service")) {
    $src = Join-Path $staging $target
    $dst = Join-Path $InstallRoot $target
    foreach ($file in Get-ChildItem $src -Recurse -File) {
        $relative = $file.FullName.Substring($src.Length).TrimStart('\')
        $installed = Join-Path $dst $relative
        $reason = if (-not (Test-Path $installed)) { "new" }
                  elseif ((Get-FileHash $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash $installed -Algorithm SHA256).Hash) { "changed" }
                  else { $null }
        if ($reason) { $changes += [pscustomobject]@{ Target = $target; File = $relative; Reason = $reason; Source = $file.FullName; Destination = $installed } }
    }
}

if ($changes.Count -eq 0) { Write-Host "Nothing to do: the installed files already match this build." -ForegroundColor Green; return }
$changes | Sort-Object Target, File | Format-Table Target, File, Reason -AutoSize | Out-Host

$serviceTouched = $changes | Where-Object Target -eq "Service"
$service = Get-Service -Name Hoard -ErrorAction SilentlyContinue
$wasRunning = $service -and $service.Status -eq "Running"

if ($PSCmdlet.ShouldProcess("$InstallRoot", "Copy $($changes.Count) file(s)")) {
    if ($serviceTouched -and $wasRunning) {
        Step "Stopping the Hoard sync service"
        Stop-Service -Name Hoard
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
    }
    Step "Copying"
    foreach ($c in $changes) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $c.Destination) | Out-Null
        Copy-Item $c.Source $c.Destination -Force
    }
    if ($serviceTouched -and $wasRunning) {
        Step "Starting the Hoard sync service"
        Start-Service -Name Hoard
    }
    Write-Host ""
    Write-Host ("Patched {0} file(s). Hoard {1} is now installed at {2}." -f $changes.Count, $version, $InstallRoot) -ForegroundColor Green
    Write-Host "Add/Remove Programs still shows the MSI's version; Settings > About shows the running one."
}
