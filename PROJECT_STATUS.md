# Project status

_Last updated: 2026-10-21_

## Current milestone

**M1 – add-in loads in Revit 2025** and **M2 – reads a DWG and reports geometry**.
Code for both is written and compiles; neither is yet confirmed inside Revit.

Acceptance:
- M1: a "Survey to Revit" panel with an "Analyse DWG" button appears on the Barr Gazetas tab in Revit 2025.
- M2: with a DWG linked, the button reports segment count, layers, extents and wall candidates, and writes a text report. Model unchanged.

## Architecture (decided)

| Project | Purpose | Needs Revit? |
|---|---|---|
| `src/SurveyToRevit.Core` | Geometry engine in mm: cleaning linework, wall detection, thickness grouping, slab outline validation, report text | No (net8.0) |
| `src/SurveyToRevit.Revit` | Revit 2025 add-in: ribbon, DWG geometry reading, later element creation | Yes (net8.0-windows) |
| `tests/SurveyToRevit.Core.Tests` | Automated geometry tests on synthetic plans | No |

Coordinate rule: Revit gives model coordinates in feet; the reader converts to mm in XY. Core works only in mm. Conversion back to feet happens only when creating Revit elements.

## Working features (with evidence)

| Feature | Compiled | Automated tests | Checked in Revit |
|---|---|---|---|
| Linework cleaning (duplicates, fragments, small gaps) | yes | pass | n/a |
| Wall detection from parallel face pairs, thickness, centrelines | yes | pass | no |
| Corners and T-junctions joined at centrelines | yes | pass | no |
| Thickness grouping into `CONCEPT_<mm>` types | yes | pass | no |
| Slab outline validation (open, self-intersecting, duplicate vertices, units sanity) | yes | pass | n/a |
| Ribbon panel on Barr Gazetas tab + Analyse DWG command (read-only) | yes | none possible | **no** |
| DWG geometry reading (lines, polylines, nested blocks, layer names) | yes | none possible | **no** |

## Build and test status

- Built on Linux with .NET SDK 8.0.132: whole solution, Release, 0 warnings, 0 errors.
- `dotnet test`: 16 passed, 0 failed.
- Revit API references: NuGet `Nice3point.Revit.Api.RevitAPI/RevitAPIUI` 2025.4.60 (compile-time only, not shipped).

## Known issues / limitations

- Arcs (curved walls, door swings) are counted but not used.
- Wall pairing is O(n²); fine for plans, may need a spatial index for very large drawings.
- Two parallel lines up to 800 mm apart with nothing between are read as a wall; narrow ducts/shafts may be misread. They show up in the report for review.
- Door/window gaps in a wall face currently get bridged when joining collinear wall pieces (gap ≤ wall thickness), or leave a split wall when wider. Openings come in a later milestone.
- DWG units rely on the Revit import settings; the report shows extents so wrong units are obvious.

## Decisions

- Native C# add-in, no MCP, no AI API, no paid services (brief §23).
- `OdedCas/mytools-pyrevit` has **no licence**, so no code copied; only the general approach (merge collinear lines → pair parallel faces → centrelines → extend to junctions) was used as a reference, re-implemented from scratch.
- `horizun-revit-mcp` (Apache-2.0) and `hungpixi/revit-mcp` (MIT) are optional later dev tools only (Phase 8). `Trace-BIM` is for raster/scanned plans and has no licence file; not needed while real DWG geometry is available.

## Next single action

Mihai: install on Windows (see README), confirm the tab appears (M1), link a survey DWG and run Analyse DWG (M2). Send back the report text file or any error message.

After that: Milestone 3 – create native walls from the recognised centrelines (with a level/height assumption and tagging for safe regeneration).

## Open cost / dependency decisions

None.
