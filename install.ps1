# Installs the Survey to Revit add-in for Revit 2025, for the current Windows user only.
# No admin rights needed: everything goes under %AppData%. Easiest: double-click install.cmd.
# - In the release zip: copies the ready-built files next to this script.
# - In the source folder: builds first (needs the .NET 8 SDK).
# Each install goes into its own folder, so it works even if an old copy is still
# held open by Windows or a Revit session; older copies are removed when possible.
$ErrorActionPreference = "Stop"
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2025"
$root = Join-Path $addins "SurveyToRevit"

$packaged = Join-Path $PSScriptRoot "SurveyToRevit"
if (Test-Path (Join-Path $packaged "SurveyToRevit.Revit.dll")) {
    $source = $packaged
} else {
    dotnet build (Join-Path $PSScriptRoot "src\SurveyToRevit.Revit\SurveyToRevit.Revit.csproj") -c Release
    $source = Join-Path $PSScriptRoot "src\SurveyToRevit.Revit\bin\Release\net8.0-windows"
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$target = Join-Path $root $stamp
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $source "*.dll"), (Join-Path $source "*.pdb"), (Join-Path $source "*.json") $target -Force
# Files from a downloaded zip carry an "internet" mark that can stop Revit loading them.
Get-ChildItem $target | Unblock-File

# Manifest pointing at this install.
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Survey to Revit</Name>
    <Assembly>SurveyToRevit\$stamp\SurveyToRevit.Revit.dll</Assembly>
    <AddInId>01d8ac57-f4f6-4331-9bb8-767badc5ce76</AddInId>
    <FullClassName>SurveyToRevit.Revit.App</FullClassName>
    <VendorId>SURVEYTOREVIT</VendorId>
    <VendorDescription>DWG survey to Revit concept model</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -Path (Join-Path $addins "SurveyToRevit.addin") -Value $manifest -Encoding UTF8

# Tidy up earlier installs; anything still in use is left for next time.
Get-ChildItem $root -File -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
Get-ChildItem $root -Directory | Where-Object { $_.Name -ne $stamp } | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "Installed to $target. Start Revit 2025; look for the 'Survey to Revit' panel on the Barr Gazetas tab." -ForegroundColor Green
