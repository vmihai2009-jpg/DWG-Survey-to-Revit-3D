#!/usr/bin/env bash
# Builds, tests and zips a release: scripts/package.sh <output folder>
# Refuses to zip if the built engine does not carry the version in its csproj.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:?output folder}"
version=$(grep -oP '(?<=<Version>)[^<]+' "$root/src/SurveyToRevit.Revit/SurveyToRevit.Revit.csproj")
dotnet build "$root" -c Release -v q -nologo | grep -E " error |Build succ" | sort -u
dotnet test "$root" -c Release --no-build -nologo | tail -1
engine="$root/src/SurveyToRevit.Revit/bin/Release/net8.0-windows"
launcher=$(ls -d "$root"/src/SurveyToRevit.Launcher/bin/Release/*/)
built=$(strings -el "$engine/SurveyToRevit.Revit.dll" | grep -m1 -E "^$version\.0$" || true)
[ -n "$built" ] || { echo "Engine dll is not version $version: rebuild first." >&2; exit 1; }
stage=$(mktemp -d)
pkg="$stage/SurveyToRevit-$version"
mkdir -p "$pkg/SurveyToRevit"
cp "$root"/install.cmd "$root"/install.ps1 "$root"/uninstall.cmd "$root"/uninstall.ps1 "$root"/README.md "$root"/src/SurveyToRevit.Launcher/SurveyToRevit.addin "$pkg/"
for f in SurveyToRevit.Revit.dll SurveyToRevit.Revit.pdb SurveyToRevit.Revit.deps.json SurveyToRevit.Core.dll SurveyToRevit.Core.pdb ACadSharp.dll Microsoft.Web.WebView2.Core.dll Microsoft.Web.WebView2.Wpf.dll; do cp "$engine/$f" "$pkg/SurveyToRevit/"; done
cp "$launcher"/SurveyToRevit.Launcher.dll "$launcher"/SurveyToRevit.Launcher.pdb "$launcher"/SurveyToRevit.Launcher.deps.json "$pkg/SurveyToRevit/"
mkdir -p "$out"
rm -f "$out/SurveyToRevit-$version.zip"
(cd "$stage" && zip -qr "$out/SurveyToRevit-$version.zip" "SurveyToRevit-$version")
rm -r "$stage"
echo "$out/SurveyToRevit-$version.zip"
