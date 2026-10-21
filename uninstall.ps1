# Removes the Survey to Revit add-in for Revit 2025 (current Windows user).
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2025"
Remove-Item (Join-Path $addins "SurveyToRevit.addin") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $addins "SurveyToRevit") -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $addins "SurveyToRevit")) { Write-Host "Some files are still in use; they will be removed after a restart of the PC or Revit." }
Write-Host "Removed. Restart Revit."
