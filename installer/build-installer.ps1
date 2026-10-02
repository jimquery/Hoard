<#
.SYNOPSIS
    Builds Hoard-<version>-x64.msi: the desktop app, the sync service, the setup worker and a
    private copy of MySQL Community Server, all in one self-contained installer.

.DESCRIPTION
    Steps:
      1. Publishes Hoard.Desktop, Hoard.Service and Hoard.Setup self-contained for win-x64, so the
         target machine needs no .NET runtime.
      2. Fetches the MySQL Community Server ZIP for Windows (cached in .cache) and prunes it down to
         what a server actually needs to run - the full archive is ~250 MB, mostly the test suite and
         debug symbols.
      3. Runs the WiX v5 toolset over Package.wxs.

.PARAMETER Version
    Product version for the MSI. Defaults to <Version> in ..\Directory.Build.props.

.PARAMETER MySqlVersion
    Exact MySQL Community Server version to bundle, e.g. 8.4.3. If omitted, the newest of a handful
    of known 8.4 LTS releases that is still downloadable is used.

.PARAMETER MySqlZip
    Path to an already-downloaded mysql-<version>-winx64.zip, for building without internet access
    (or when Oracle has moved the download again).

.PARAMETER SkipPublish
    Reuse whatever is already in .staging instead of re-running dotnet publish.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -MySqlZip C:\Downloads\mysql-8.4.3-winx64.zip
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$MySqlVersion,
    [string]$MySqlZip,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root      = Split-Path -Parent $PSScriptRoot
$here      = $PSScriptRoot
$staging   = Join-Path $here ".staging"
$cache     = Join-Path $here ".cache"
$outputDir = Join-Path $root "publish"

function Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

# ---------------------------------------------------------------- version ----
if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
    $Version = $props.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Version '$Version' is not a Windows Installer version (x.y.z[.b])."
}
Step "Building Hoard $Version"

# ------------------------------------------------------------- toolchain ----
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
}
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Step "Installing the WiX v5 toolset as a global dotnet tool"
    dotnet tool install --global wix --version "5.*"
    if ($LASTEXITCODE -ne 0) { throw "Could not install the wix dotnet tool." }
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}
# The UI extension must match the wix tool's own major.minor.patch: `wix extension add` without a version
# pulls the newest package on NuGet, which is a different major version now that WiX 6 exists, and a
# mismatched extension is reported as "could not be found". Read the tool's version and pin to it.
$wixVersionText = (& wix --version 2>&1 | Select-Object -First 1).ToString().Trim()
if ($wixVersionText -notmatch '^(\d+\.\d+\.\d+)') { throw "Could not read the wix tool version from '$wixVersionText'." }
$wixVersion = $Matches[1]
if ($wixVersion -notlike '5.*') {
    throw "wix $wixVersion is installed but Package.wxs targets WiX v5. Run: dotnet tool update --global wix --version `"5.*`""
}
Step "Making sure WixToolset.UI.wixext $wixVersion is available"
# Idempotent: adding an extension that is already present is a no-op. Not silenced, so a failure is visible.
& wix extension add -g "WixToolset.UI.wixext/$wixVersion"
if ($LASTEXITCODE -ne 0) { throw "wix extension add failed - check the message above (NuGet access is needed the first time)." }
$installed = (& wix extension list -g 2>&1 | Out-String)
if ($installed -notmatch 'WixToolset\.UI\.wixext') {
    throw "WixToolset.UI.wixext is still not listed by 'wix extension list -g':`n$installed"
}

# --------------------------------------------------------------- publish ----
if (-not $SkipPublish) {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $staging, $cache, $outputDir | Out-Null

function Publish($project, $target) {
    Step "Publishing $project"
    dotnet publish (Join-Path $root $project) `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none `
        -o (Join-Path $staging $target)
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project." }
}

if (-not $SkipPublish) {
    Publish "src\Hoard.Desktop\Hoard.Desktop.csproj" "Desktop"
    Publish "src\Hoard.Service\Hoard.Service.csproj" "Service"
    Publish "src\Hoard.Setup\Hoard.Setup.csproj"     "Setup"
}

foreach ($required in @("Desktop\Hoard.exe", "Service\HoardService.exe", "Setup\HoardSetup.exe")) {
    if (-not (Test-Path (Join-Path $staging $required))) {
        throw "Expected $required in $staging but it is not there."
    }
}

# ----------------------------------------------------------------- MySQL ----
$mysqlStage = Join-Path $staging "mysql"
if (-not (Test-Path (Join-Path $mysqlStage "bin\mysqld.exe"))) {

    if (-not $MySqlZip) {
        # Oracle retires point releases from the download server, so try newest-first and take
        # whichever is still there. Override with -MySqlVersion or -MySqlZip at any time.
        $candidates = if ($MySqlVersion) { @($MySqlVersion) } else {
            @("8.4.11", "8.4.5", "8.4.4", "8.4.3", "8.4.2", "8.4.1", "8.4.0")
        }
        foreach ($v in $candidates) {
            $series = ($v -split '\.')[0..1] -join '.'
            $name   = "mysql-$v-winx64.zip"
            $target = Join-Path $cache $name
            if (Test-Path $target) { $MySqlZip = $target; break }

            $url = "https://dev.mysql.com/get/Downloads/MySQL-$series/$name"
            Step "Downloading $name (about 250 MB; cached in .cache for next time)"
            try {
                Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
                $MySqlZip = $target
                break
            } catch {
                Write-Host "    $v is not available at $url" -ForegroundColor DarkYellow
                if (Test-Path $target) { Remove-Item $target -Force }
            }
        }
    }

    if (-not $MySqlZip -or -not (Test-Path $MySqlZip)) {
        throw @"
Could not obtain the MySQL Community Server ZIP.
Download 'Windows (x86, 64-bit), ZIP Archive' from https://dev.mysql.com/downloads/mysql/
and re-run:  .\build-installer.ps1 -MySqlZip <path to mysql-x.y.z-winx64.zip>
"@
    }

    Step "Extracting $(Split-Path -Leaf $MySqlZip)"
    $extract = Join-Path $cache "extract"
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-Archive -Path $MySqlZip -DestinationPath $extract -Force

    $inner = Get-ChildItem $extract -Directory | Select-Object -First 1
    if (-not $inner) { throw "The MySQL archive did not contain the expected top-level folder." }
    if (-not (Test-Path (Join-Path $inner.FullName "bin\mysqld.exe"))) {
        throw "bin\mysqld.exe was not found in $($inner.FullName)."
    }

    Step "Pruning the MySQL tree (the full archive is mostly the test suite and debug symbols)"
    $before = (Get-ChildItem $inner.FullName -Recurse -File | Measure-Object Length -Sum).Sum

    # Whole trees a running server never touches.
    foreach ($dir in @("mysql-test", "include", "docs")) {
        $p = Join-Path $inner.FullName $dir
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
    # Debug symbols, import libraries and the debug builds of the plugins.
    Get-ChildItem $inner.FullName -Recurse -File |
        Where-Object { $_.Extension -in @(".pdb", ".lib", ".exp") } | Remove-Item -Force
    Get-ChildItem $inner.FullName -Recurse -Directory |
        Where-Object { $_.Name -eq "debug" } | Remove-Item -Recurse -Force
    # Client programs Hoard does not use. Every .dll stays - mysqld links several of them.
    $keepExe = @("mysqld.exe", "mysql.exe", "mysqladmin.exe", "mysqldump.exe", "my_print_defaults.exe")
    Get-ChildItem (Join-Path $inner.FullName "bin") -File -Filter *.exe |
        Where-Object { $keepExe -notcontains $_.Name } | Remove-Item -Force
    # Error messages: English is the only language the server is configured for.
    $share = Join-Path $inner.FullName "share"
    if (Test-Path $share) {
        Get-ChildItem $share -Directory |
            Where-Object { $_.Name -notin @("english", "charsets") } | Remove-Item -Recurse -Force
    }

    $after = (Get-ChildItem $inner.FullName -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("    {0:N0} MB -> {1:N0} MB" -f ($before / 1MB), ($after / 1MB))

    if (Test-Path $mysqlStage) { Remove-Item $mysqlStage -Recurse -Force }
    Move-Item $inner.FullName $mysqlStage
    Remove-Item $extract -Recurse -Force
} else {
    Step "Reusing the MySQL tree already staged in .staging\mysql"
}

# ------------------------------------------------------------------- MSI ----
$msi = Join-Path $outputDir "Hoard-$Version-x64.msi"
Step "Building $msi (compression takes a few minutes)"

# Output is echoed and also kept in publish\wix-build.log so a failure can be read after the fact.
$wixLog = Join-Path $outputDir "wix-build.log"
wix build `
    -arch x64 `
    -d Version=$Version `
    -d PublishRoot=$staging `
    -bindpath $here `
    -ext WixToolset.UI.wixext `
    -o $msi `
    (Join-Path $here "Package.wxs") 2>&1 | Tee-Object -FilePath $wixLog

if ($LASTEXITCODE -ne 0) { throw "wix build failed - see the messages above (also in $wixLog)." }

$size = (Get-Item $msi).Length / 1MB
Write-Host ""
Write-Host ("Done. {0} ({1:N0} MB)" -f $msi, $size) -ForegroundColor Green
Write-Host "Install with:  msiexec /i `"$msi`" /l*v hoard-install.log"
Write-Host "Uninstall keeps your database; to remove it too, run from an elevated prompt:"
Write-Host "  `"C:\Program Files\Hoard\Setup\HoardSetup.exe`" uninstall --purge"
