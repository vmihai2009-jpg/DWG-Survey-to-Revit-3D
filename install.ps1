# Installs the Survey to Revit add-in for Revit 2025, for the current Windows user only.
# No admin rights needed: everything goes under %AppData%. Easiest: double-click install.cmd.
# - In the release zip: copies the ready-built files next to this script.
# - In the source folder: builds first (needs the .NET 8 SDK).
# Two parts, each in its own versioned folder so nothing in use is ever overwritten:
#   launcher\  what Revit loads at start-up (ribbon, Updater, Settings)
#   engine\    the tools; the launcher runs the newest engine on each click, so later
#              updates (Updater button) work without restarting Revit.
$ErrorActionPreference = "Stop"
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2025"
$root = Join-Path $addins "SurveyToRevit"

$packaged = Join-Path $PSScriptRoot "SurveyToRevit"
if (Test-Path (Join-Path $packaged "SurveyToRevit.Revit.dll")) {
    $engineSource = $packaged
    $launcherSource = $packaged
} else {
    dotnet build (Join-Path $PSScriptRoot "src\SurveyToRevit.Revit\SurveyToRevit.Revit.csproj") -c Release
    dotnet build (Join-Path $PSScriptRoot "src\SurveyToRevit.Launcher\SurveyToRevit.Launcher.csproj") -c Release
    $engineSource = Join-Path $PSScriptRoot "src\SurveyToRevit.Revit\bin\Release\net8.0-windows"
    $launcherSource = Join-Path $PSScriptRoot "src\SurveyToRevit.Launcher\bin\Release\net8.0-windows"
}

function Version-Of($dll) {
    $v = [System.Reflection.AssemblyName]::GetAssemblyName($dll).Version
    "{0}.{1}.{2}" -f $v.Major, $v.Minor, [Math]::Max(0, $v.Build)
}

# Same folder names as the in-Revit Updater: <when installed>_v<version>
function Install-Part($source, $part, $files) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmssfff"
    $main = Get-ChildItem $source -Filter $files[0] | Select-Object -First 1
    $target = Join-Path $root "$part\$($stamp)_v$(Version-Of $main.FullName)"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Get-ChildItem $source -File | Where-Object { $n = $_.Name; ($files | Where-Object { $n -like $_ }) -and $_.Extension -in ".dll", ".pdb", ".json" } |
        Copy-Item -Destination $target -Force
    # Files from a downloaded zip carry an "internet" mark that can stop Revit loading them.
    Get-ChildItem $target | Unblock-File
    $target
}

$engine = Install-Part $engineSource "engine" @("SurveyToRevit.Revit.dll", "SurveyToRevit.Core.*", "SurveyToRevit.Revit.*", "ACadSharp*", "CSUtilities*")
$launcher = Install-Part $launcherSource "launcher" @("SurveyToRevit.Launcher.dll", "SurveyToRevit.Launcher.*")

# Manifest pointing at the launcher.
$relative = $launcher.Substring($addins.Length + 1)
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Survey to Revit</Name>
    <Assembly>$relative\SurveyToRevit.Launcher.dll</Assembly>
    <AddInId>01d8ac57-f4f6-4331-9bb8-767badc5ce76</AddInId>
    <FullClassName>SurveyToRevit.Launcher.App</FullClassName>
    <VendorId>SURVEYTOREVIT</VendorId>
    <VendorDescription>DWG survey to Revit concept model</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -Path (Join-Path $addins "SurveyToRevit.addin") -Value $manifest -Encoding UTF8

# Tidy up: loose files and v0.12-style folders (anything still in use is left; Revit tidies it later),
# and older launchers.
Get-ChildItem $root -File -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
Get-ChildItem $root -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}(\.old)?$' } | ForEach-Object {
    $old = if ($_.Name.EndsWith(".old")) { $_.FullName } else { $_.FullName + ".old" }
    try { if ($old -ne $_.FullName) { Rename-Item $_.FullName $old -ErrorAction Stop }; Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
Get-ChildItem (Join-Path $root "launcher") -Directory | Where-Object { $_.FullName -ne $launcher } | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Installed to $root. Start Revit 2025 (or restart it once); look for the 'Survey to Revit' panel on the Barr Gazetas tab." -ForegroundColor Green
Write-Host "Later releases install from inside Revit with the Updater button, with no restart." -ForegroundColor Green
