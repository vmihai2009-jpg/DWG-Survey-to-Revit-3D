# Project status

_Last updated: 2026-10-21_

## Current milestone

**M4 – Concept model from survey (v0.3).** Build from Survey now opens a window (floors, levels, ceiling heights, what to build) and creates walls, floor slabs, ceilings and generic models for unclear areas, on top of v0.2's coordinates, levels and links.
Code compiles and the engine is tested on the real Swallow Place survey; **nothing of v0.3 has been run inside Revit yet**.

Acceptance (to check in Revit 2025):
1. *Barr Gazetas → Survey to Revit → Build from Survey*, select all Swallow Place DWGs: the window lists 10 floors; Basement, Ground, Mezzanine and Seventh are highlighted.
2. After *Build*: walls follow the linked plans (the link is now checked against the walls and moved onto them if off); slabs and ceilings on every floor; "Survey - check" generic models where the drawing was unclear.
3. Running it again replaces only the generated elements on those floors.

v0.2 feedback addressed: no floors/ceilings (added), poor walls (new reader, below), no interface (window added), link "559 km away" warning (the check compared different coordinate spaces; it now compares the link's actual wall lines and corrects the position), walls placed twice-transformed (fixed).

## Architecture

| Project | Purpose | Needs Revit? |
|---|---|---|
| `src/SurveyToRevit.Core` | Engine (mm): reads DWGs directly with ACadSharp (texts/spot heights included), layer roles, axis snapping, wall detection, levels, coordinate frame, reports | No |
| `src/SurveyToRevit.Revit` | Revit 2025 add-in: Build from Survey command, `SurveyDialog` window, `ModelBuilder` (coordinates, levels, links, walls, slabs, ceilings, generic models) | Yes |
| `tests/SurveyToRevit.Core.Tests` | Synthetic tests (no client data in the repo) | No |
| `tools/SurveyLab` | Runs the engine on a folder of DWGs, writes report + SVG previews | No |

Key decisions:
- DWGs are read directly (ACadSharp, MIT) instead of through Revit's CAD import, because Revit's API does not expose DWG text, so spot heights would be lost.
- Coordinates: internal origin = base point (centre of ground floor walls, rounded to 1 m); project north = main wall direction; grid north = true north; elevations = survey datum (m AOD). Revit's angle convention is verified after setting and flipped if needed; each link's position is checked against the walls (warning if >10 mm off).
- Links: `Placement = Shared`, current view only, pinned, into that level's floor plan.
- Thresholds (all in `Tolerances`): 10 mm precision; 2° angle snap to the building's main directions; wall types snapped to 100/150/200/250/300/350/400/450/500/600/700/800/900/1000; façade pieces sharing one face merged; wall pieces joined across doors/windows (up to 3 m) when opening linework is in the gap; text inside a candidate wall rejects it.
- Wall reading (v0.3), after studying how the survey is drawn: walls are double outlines, rooms carry a name or spot height, door openings leave gaps, and the linework has small gaps that defeat exact face tracing. So: (1) a 25 mm grid of each floor tells outside, rooms (flooded from labels/spot heights, gaps closed) and wall material (thin strips, closed pockets; room parts a 600 mm square cannot reach); (2) walls are the drawn line pairs, closest first, kept only where the grid says wall material lies between them, so thicknesses come from the drawing to the mm; (3) façade pieces that face outside and line up become one wall per run, at the masonry thickness; (4) lone lines inside the building ≥ 1 m (single-line partitions, common on the ground floor) become 100 mm walls marked Low confidence.
- Regeneration: generated walls carry `SurveyToRevit|<level code>|<confidence>` in Comments; re-run deletes only those for the floors being rebuilt. The whole run is one undo step.

## Results on Swallow Place survey (engine, outside Revit)

- 35 DWGs: 10 GA floor plans used; fire/area plans (duplicates), elevations, sections and roof sheet skipped with reasons.
- Units: correctly metres for all plans (most files say "unitless").
- Base point E 528999 N 181190; rotation 12.61°.
- Levels (m AOD): B 22.86, G 26.47*, GM 29.50*, 1st 32.36, 2nd 36.78, 3rd 40.60, 4th 43.95, 5th 47.30, 6th 50.21, 7th 54.25* (*flagged: split/unclear level).
- Walls: 1,400 walls, about 3.2 km, in 8 types (the previous reader found about 1.0 km in 14 types on the same drawings). Floor outlines found on all 10 floors; 62 unclear areas → generic models.

## Build and test status

- Whole solution builds (Release) on Linux, .NET SDK 8.0.132, 0 errors.
- `dotnet test`: 42 passed, 0 failed.

## Known issues / limitations

- Façade runs merge only where pieces line up; bays, splays and pilastered corners can still give several short pieces.
- Single-line walls get 100 mm; some lines that are not walls (bay outlines, counters) may become walls. Marked Low confidence in Comments.
- Slabs and ceilings follow each floor's outer outline (no voids for stairs, lifts or light wells yet). Ceiling heights are not readable from the survey (placeholders "X.XX"); default 2700 mm, editable in the window.
- No windows or doors yet; curved walls ignored.
- The ground floor is split level (26.47 / 27.05); one level is used, flagged.

## Next single action

Mihai: install v0.3 and run *Build from Survey* on a copy of the project; send screenshots of a typical floor (plan and 3D) and the log.

Then: windows and doors from the opening layers and sill/head texts ("S 0.94 H 2.58"); voids in slabs at stairs and lifts.

## Open cost / dependency decisions

None. New dependency: ACadSharp 3.8.0 (MIT).
