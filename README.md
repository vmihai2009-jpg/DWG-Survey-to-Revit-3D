# DWG Survey to Revit

Revit 2025 add-in that turns 2D DWG building surveys into an editable concept model (walls, slabs, levels, windows). Early stage: see [PROJECT_STATUS.md](PROJECT_STATUS.md) for what works today.

## Install (Windows, Revit 2025)

**From the release zip** (no tools needed):
1. Unzip anywhere.
2. Double-click `install.cmd`. No admin password is needed: it installs for your Windows user only.
3. Restart Revit 2025. Revit may ask whether to load the unsigned add-in: choose *Always Load*.

**From source:** install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then double-click `install.cmd` in the repository folder; it builds and installs.

Files go to `%AppData%\Autodesk\Revit\Addins\2025\`. Remove with `uninstall.cmd`.

## Use

1. Open a Revit project (try a copy first).
2. *Barr Gazetas* tab → *Survey to Revit* panel → **Build from Survey**.
3. Select the survey DWGs (all of them is fine: elevations, sections and duplicate plans are skipped).
4. Check the summary (floors, heights, wall count), then choose:
   - **Build model**: sets project coordinates and north from the survey, creates levels at the surveyed floor heights, links each plan into its level's floor plan view, and creates concept walls ("Concept Wall 150mm" etc.).
   - **Set up only**: the same without walls.
   - **Open the report only**: changes nothing.
5. A log is saved in `%TEMP%\SurveyToRevit`. Levels marked CHECK in the report had unclear heights.

Running it again replaces only the walls it created before on those floors. Undo reverses the whole run.

## Developers

```
dotnet build -c Release     # whole solution, works on Windows or Linux
dotnet test                 # geometry tests, no Revit needed
```

- `src/SurveyToRevit.Core` – Revit-free geometry engine (mm).
- `src/SurveyToRevit.Revit` – Revit add-in.
- `tests/SurveyToRevit.Core.Tests` – synthetic-plan tests.
- `tools/SurveyLab` – `dotnet run --project tools/SurveyLab -- <folder of DWGs> --out <dir>` runs the engine without Revit and writes a report and SVG wall previews.

Third-party components: see [THIRD_PARTY.md](THIRD_PARTY.md).
