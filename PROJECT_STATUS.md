# Project status

_Last updated: 2026-10-21_

## Current milestone

**M3 – Build from survey (v0.2).** One button: pick survey DWGs → coordinates, true/project north, levels from spot heights, each plan linked to its level, concept walls.
Code compiles and the engine is tested on the real Swallow Place survey; **nothing has been run inside Revit yet**.

Acceptance (to check in Revit 2025):
1. *Barr Gazetas → Survey to Revit → Build from Survey*, select all Swallow Place DWGs.
2. Summary lists 10 floors (Basement … Seventh Floor) with heights.
3. After *Build model*: shared coordinates at E 528999 N 181190, project north 12.61° from true north; levels at surveyed heights; each GA plan linked into its level's plan view and lining up with the walls; walls typed "Concept Wall NNNmm".
4. Running it again replaces only the generated walls (no duplicates).

## Architecture

| Project | Purpose | Needs Revit? |
|---|---|---|
| `src/SurveyToRevit.Core` | Engine (mm): reads DWGs directly with ACadSharp (texts/spot heights included), layer roles, axis snapping, wall detection, levels, coordinate frame, reports | No |
| `src/SurveyToRevit.Revit` | Revit 2025 add-in: Build from Survey command, `ModelBuilder` (coordinates, levels, links, walls) | Yes |
| `tests/SurveyToRevit.Core.Tests` | Synthetic tests (no client data in the repo) | No |
| `tools/SurveyLab` | Runs the engine on a folder of DWGs, writes report + SVG previews | No |

Key decisions:
- DWGs are read directly (ACadSharp, MIT) instead of through Revit's CAD import, because Revit's API does not expose DWG text, so spot heights would be lost.
- Coordinates: internal origin = base point (centre of ground floor walls, rounded to 1 m); project north = main wall direction; grid north = true north; elevations = survey datum (m AOD). Revit's angle convention is verified after setting and flipped if needed; each link's position is checked against the walls (warning if >10 mm off).
- Links: `Placement = Shared`, current view only, pinned, into that level's floor plan.
- Thresholds (all in `Tolerances`): 10 mm precision; 2° angle snap to the building's main directions; wall types snapped to 100/150/200/250/300/350/400/450/500/600/700/800/900/1000; façade pieces sharing one face merged; wall pieces joined across doors/windows (up to 3 m) when opening linework is in the gap; text inside a candidate wall rejects it.
- Regeneration: generated walls carry `SurveyToRevit|<level code>|<confidence>` in Comments; re-run deletes only those for the floors being rebuilt. The whole run is one undo step.

## Results on Swallow Place survey (engine, outside Revit)

- 35 DWGs: 10 GA floor plans used; fire/area plans (duplicates), elevations, sections and roof sheet skipped with reasons.
- Units: correctly metres for all plans (most files say "unitless").
- Base point E 528999 N 181190; rotation 12.61°.
- Levels (m AOD): B 22.86, G 26.47*, GM 29.50*, 1st 32.36, 2nd 36.78, 3rd 40.60, 4th 43.95, 5th 47.30, 6th 50.21, 7th 54.25* (*flagged: split/unclear level).
- Walls: 1,094 in 14 types (first pass gave 1,586 walls in up to 44 types per floor).

## Build and test status

- Whole solution builds (Release) on Linux, .NET SDK 8.0.132, 0 errors.
- `dotnet test`: 38 passed, 0 failed.

## Known issues / limitations

- Many wall lines in this survey are single (one face only, e.g. room outlines without the other side). These are reported as "not explained", not turned into walls; some walls are therefore missing.
- Some pilasters/projections on façades still give short extra wall pieces.
- Walls run floor to floor (to the next level); top floor uses 3000 mm. No slabs, windows or roof yet.
- Ground floor level choice is ambiguous (split level); check in Revit.
- Curved walls (bulged polylines, arcs) ignored.
- ACadSharp is loaded into Revit's process; if another add-in loads a different ACadSharp version there could be a conflict.

## Next single action

Mihai: install v0.2 and run *Build from Survey* on a copy of a project with the Swallow Place DWGs; report what looks wrong (screenshots + the log file).

Then: floor slabs from the building outline (M4), windows from the window layer and sill/head texts ("S 0.98 H 5.10").

## Open cost / dependency decisions

None. New dependency: ACadSharp 3.8.0 (MIT).
