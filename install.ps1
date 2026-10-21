# Installs the Survey to Revit add-in for Revit 2025, for the current Windows user only.
# No admin rights needed: everything goes under %AppData%. Easiest: double-click install.cmd.
# - In the release zip: copies the ready-built files next to this script.
# - In the source folder: builds first (needs the .NET 8 SDK).
$ErrorActionPreference = "Stop"
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2025"
$target = Join-Path $addins "SurveyToRevit"

# Revit 2025 keeps the add-in's files open while it runs, so they cannot be replaced.
# Other Revit versions (e.g. 2024) do not load these files and can stay open.
function Get-Revit2025 { Get-Process -Name "Revit" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*2025*" } }
while (Get-Revit2025) {
    Write-Host "Revit 2025 is running (it may be hidden, check Task Manager). Save your work, close it completely, then press Enter (or close this window to stop)." -ForegroundColor Yellow
    Read-Host | Out-Null
}

$packaged = Join-Path $PSScriptRoot "SurveyToRevit"
if (Test-Path (Join-Path $packaged "SurveyToRevit.Revit.dll")) {
    $source = $packaged
    $manifest = Join-Path $PSScriptRoot "SurveyToRevit.addin"
} else {
    dotnet build (Join-Path $PSScriptRoot "src\SurveyToRevit.Revit\SurveyToRevit.Revit.csproj") -c Release
    $source = Join-Path $PSScriptRoot "src\SurveyToRevit.Revit\bin\Release\net8.0-windows"
    $manifest = Join-Path $source "SurveyToRevit.addin"
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $source "*.dll"), (Join-Path $source "*.pdb"), (Join-Path $source "*.json") $target -Force
Copy-Item $manifest $addins -Force
# Files from a downloaded zip carry an "internet" mark that can stop Revit loading them.
Get-ChildItem $target | Unblock-File
Unblock-File (Join-Path $addins "SurveyToRevit.addin")
Write-Host "Installed to $target. Restart Revit 2025; look for the 'Survey to Revit' panel on the Barr Gazetas tab."
