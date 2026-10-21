# Removes the Survey to Revit add-in for Revit 2025 (current Windows user).
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2025"
Remove-Item (Join-Path $addins "SurveyToRevit.addin") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $addins "SurveyToRevit") -Recurse -ErrorAction SilentlyContinue
Write-Host "Removed. Restart Revit."
