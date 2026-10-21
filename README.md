# DWG Survey to Revit

Revit 2025 add-in that turns 2D DWG building surveys into an editable concept model (walls, slabs, levels, windows). Early stage: see [PROJECT_STATUS.md](PROJECT_STATUS.md) for what works today.

## Install (Windows, Revit 2025)

**From the release zip** (no tools needed):
1. Unzip anywhere.
2. Right-click `install.ps1` → *Run with PowerShell* (or run `powershell -ExecutionPolicy Bypass -File install.ps1`).
3. Restart Revit 2025. Revit may ask whether to load the unsigned add-in: choose *Always Load*.

**From source:** install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then run `install.ps1` from the repository folder; it builds and installs.

Files go to `%AppData%\Autodesk\Revit\Addins\2025\`. Remove with `uninstall.ps1`.

## Use

1. Open a project, link a DWG survey (*Insert → Link CAD*, units set correctly, *Current view only* off).
2. *Survey to Revit* tab → **Analyse DWG**. If several DWGs are linked, select one first or pick it when asked.
3. Read the summary; *Open report* shows the full text report (also saved in `%TEMP%\SurveyToRevit`).

The command is read-only: it never changes the model.

## Developers

```
dotnet build -c Release     # whole solution, works on Windows or Linux
dotnet test                 # geometry tests, no Revit needed
```

- `src/SurveyToRevit.Core` – Revit-free geometry engine (mm).
- `src/SurveyToRevit.Revit` – Revit add-in.
- `tests/SurveyToRevit.Core.Tests` – synthetic-plan tests.

Third-party components: see [THIRD_PARTY.md](THIRD_PARTY.md).
