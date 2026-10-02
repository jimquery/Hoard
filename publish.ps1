# Builds release binaries for the desktop app and the Windows service into .\publish\<version>\.
# Usage (from the repository root, in PowerShell):  .\publish.ps1
# Requires the .NET 8 SDK. Output is framework-dependent (needs the .NET 8 Desktop Runtime on the target machine);
# add -SelfContained to bundle the runtime instead.
param([switch]$SelfContained)

$ErrorActionPreference = "Stop"
$version = ([xml](Get-Content "$PSScriptRoot\Directory.Build.props")).Project.PropertyGroup.Version
$out = Join-Path $PSScriptRoot "publish\$version"
$sc = if ($SelfContained) { "true" } else { "false" }

Write-Host "Publishing Hoard $version to $out"
dotnet publish "$PSScriptRoot\src\Hoard.Desktop\Hoard.Desktop.csproj" -c Release -r win-x64 --self-contained $sc -o "$out\Desktop"
dotnet publish "$PSScriptRoot\src\Hoard.Service\Hoard.Service.csproj" -c Release -r win-x64 --self-contained $sc -o "$out\Service"
Copy-Item "$PSScriptRoot\README.md", "$PSScriptRoot\CHANGELOG.md" $out

Write-Host ""
Write-Host "Done. Desktop: $out\Desktop\Hoard.Desktop.exe"
Write-Host "      Service: $out\Service\HoardService.exe (register with sc.exe as described in README.md)"
