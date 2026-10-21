# Project status

_Last updated: 2026-10-21_

## Current milestone

**M4 – Concept model from survey (v0.6).** v0.6 = v0.5 plus surroundings from LiDAR (ground surface and neighbouring blocks on a "Context" workset). v0.5 = v0.4 plus doors, named rooms, everything on the Existing phase, windows that cut through linings, and fixes for the failed Basement and Mezzanine floors. Build from Survey now opens a window (floors, levels, ceiling heights, what to build) and creates walls, floor slabs, ceilings and generic models for unclear areas, on top of v0.2's coordinates, levels and links.
Code compiles and the engine is tested on the real Swallow Place survey. v0.3 was run in Revit by Mihai and stopped with "The input curve is not bound": the link check read end points of circles in the DWG (circles have none). Fixed, and every element is now created on its own so one bad element is skipped and logged instead of cancelling the run. v0.4 was run by Mihai: walls "much better"; windows did not cut openings; Basement floor failed ("wrong EndCap condition"), Mezzanine floor and ceiling failed (outline crossed itself). **v0.5 and v0.6 have not been run in Revit yet.**

Acceptance (to check in Revit 2025):
1. *Barr Gazetas → Survey to Revit → Build from Survey*, select all Swallow Place DWGs: the window lists 10 floors; Basement, Ground, Mezzanine and Seventh are highlighted.
2. After *Build*: walls follow the linked plans (the link is now checked against the walls and moved onto them if off); slabs and ceilings on every floor; "Survey - check" generic models where the drawing was unclear.
3. Running it again replaces only the generated elements on those floors.

v0.6 adds: surroundings. Select Environment Agency LiDAR tiles (DSM and DTM GeoTIFF or .asc, free, Open Government Licence) with the DWGs. Ground = toposolid from the DTM (5 m grid) within the chosen radius (default 150 m). Neighbours = blocks: with a footprint shapefile (e.g. OS MasterMap Topography buildings, if the office licenses it) its exact outlines at the LiDAR median roof height, split by the LiDAR where the roof steps more than 3 m; without one, outlines traced from the LiDAR (1 m grid, about ±0.5 m) where DSM − DTM ≥ 2.5 m on a smooth roof (trees drop out). The surveyed building and 3 m round it are never touched; its LiDAR roof height is only reported. Footprints with no LiDAR height are left out, not guessed. Everything goes on a "Context" workset (made if missing; the add-in never turns worksharing on), Existing phase, Mass category. OS OpenMap Local was considered and rejected: generalised for 1:10,000, several metres out.

v0.5 adds: doors from the door swings on the door layer (a leaf line hinged at a wall gives the opening width and side; two leaves meeting = double door; 2100 mm high), as a wall-hosted single door family (types "Concept Door WxH"; plain openings if none); rooms named from the survey's room-name layer (Revit's enclosed spaces holding a label become rooms; unlabelled spaces get none); every element on the "Existing" phase (else the first phase); window/door families restricted to wall-hosted ones, windows hosted in the thickest nearby wall, and lining walls in front cut through; slab type made by copying a floor type's own layer set (fixes the Basement); floor outlines cleaned so they never cross themselves (fixes the Mezzanine).

v0.4 adds: windows from the window layer (one per cluster of window lines, width along its wall, sill/head from "S 0.94"/"H 2.58" texts, otherwise 900 mm sill and 1500 mm high), hosted in the generated walls as a fixed window family (from the project, else Revit's library; types "Concept Window WxH" in 50 mm steps; plain openings if no family exists); walls joined across gaps up to 1500 mm and hanging ends run on to the next wall; ribbon icon.

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
- Walls (v0.4): 1,344 walls, about 3.4 km, in 8 types (the reader before v0.3 found about 1.0 km in 14 types on the same drawings). Floor outlines on all 10 floors; 31 unclear areas → generic models; 258 windows, 178 with surveyed sill and head.
- v0.5: 262 windows, 176 doors, 181 room labels; all 10 floor outlines are simple (non-crossing) polygons.

## Build and test status

- Whole solution builds (Release) on Linux, .NET SDK 8.0.132, 0 errors.
- `dotnet test`: 60 passed, 0 failed. The LiDAR reader is tested on synthetic GeoTIFFs in every compression the EA uses (made with tifffile; not survey data). No real LiDAR has been run yet: the cloud build machine cannot reach the EA servers.

## Known issues / limitations

- Façade runs merge only where pieces line up; bays, splays and pilastered corners can still give several short pieces.
- Single-line walls get 100 mm; some lines that are not walls (bay outlines, counters) may become walls. Marked Low confidence in Comments.
- Slabs and ceilings follow each floor's outer outline (no voids for stairs, lifts or light wells yet). Ceiling heights are not readable from the survey (placeholders "X.XX"); default 2700 mm, editable in the window.
- Doors: placed in the wall the leaf is hinged on; swing direction is not set (Revit default). Curved walls ignored.
- Rooms only where the walls close a space; a label in an open-plan area with gaps gets no room (counted in the log).
- Windows: a few may land on a thin wall next to the façade rather than the façade itself; widths are measured from the window linework.
- The ground floor is split level (26.47 / 27.05); one level is used, flagged.

## Next single action

Mihai: install v0.5 and run *Build from Survey* on a copy of the project; send screenshots of a typical floor (plan and 3D) and the log.

Then: façades of the surveyed building from the survey elevation drawings (currently skipped); voids in slabs at stairs and lifts.

## Open cost / dependency decisions

None. New dependency: ACadSharp 3.8.0 (MIT).
