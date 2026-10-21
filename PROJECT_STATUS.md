# Project status

_Last updated: 2026-10-21_

## Current milestone

**M4 – Concept model from survey (v0.9).** v0.9 = v0.8 plus every layer read for what it holds (with an optional layers.txt), curtain walls, 20 mm wall steps, better-seated windows and closed rooms. v0.8 = v0.7 plus closed façade corners and piers, and surveyed heights for windows, doors, ceilings and levels. v0.7 = v0.6 plus curved walls, bridged façade facets, the *Wall from Lines* tool, room fix, and survey file names in words (second building: Mount Row). v0.6 = v0.5 plus surroundings from LiDAR (ground surface and neighbouring blocks on a "Context" workset). v0.5 = v0.4 plus doors, named rooms, everything on the Existing phase, windows that cut through linings, and fixes for the failed Basement and Mezzanine floors. Build from Survey now opens a window (floors, levels, ceiling heights, what to build) and creates walls, floor slabs, ceilings and generic models for unclear areas, on top of v0.2's coordinates, levels and links.
Code compiles and the engine is tested on the real Swallow Place survey. v0.3 was run in Revit by Mihai and stopped with "The input curve is not bound": the link check read end points of circles in the DWG (circles have none). Fixed, and every element is now created on its own so one bad element is skipped and logged instead of cancelling the run. v0.4 was run by Mihai: walls "much better"; windows did not cut openings; Basement floor failed ("wrong EndCap condition"), Mezzanine floor and ceiling failed (outline crossed itself). v0.6 was run by Mihai: build completed; rooms 0 (fixed in 0.6.1: rooms were matched before Revit regenerated), only a DTM tile was given (no neighbours), project not workshared; curved Regent St corner missing. v0.7 was looked at in Revit by Mihai: curved corner present, but gaps where the curve meets the east façade and missing piers on the east façade (3rd, 4th floors). v0.8 feedback: windows "still look janky"; asked for layer understanding, stairs, 20 mm wall steps, more generic-model detail, roofs, rooms where the drawing shows them. **v0.9 has not been run in Revit yet.** The LiDAR reader was checked on Mihai's real EA tile TQ28se_DTM_1m.tif (LZW, 128 px tiles): ground at the base point 26.63 m AOD vs survey ground floor 26.47 m.

Acceptance (to check in Revit 2025):
1. *Barr Gazetas → Survey to Revit → Build from Survey*, select all Swallow Place DWGs: the window lists 10 floors; Basement, Ground, Mezzanine and Seventh are highlighted.
2. After *Build*: walls follow the linked plans (the link is now checked against the walls and moved onto them if off); slabs and ceilings on every floor; "Survey - check" generic models where the drawing was unclear.
3. Running it again replaces only the generated elements on those floors.

v0.9 adds: every layer is given a role from its name (walls, curtain walls/glazed screens, windows, doors, spot heights, room names, height notes, stairs, ramps, lifts, columns, roofs, rooflights, chimneys, balconies, railings, fittings, ceilings, area outlines, overhead lines, annotation); the report lists each layer and what it was read as; a layers.txt next to the DWGs ("layer name = Role", * wildcard) overrides the built-in rules. Lines on the new feature layers are kept for stairs/roofs/detail (next releases). Glazed partition layers are built as curtain walls ("Concept Curtain Wall"). Wall types every 20 mm (halfway sizes take the thicker). Windows: host must hold the whole drawn window, outer face turned to the outside, windows with no surveyed height take the floor's usual surveyed sill/head. Rooms: room separation lines where a drawn line (not a wall) or a short gap between walls closes a room; unlabelled enclosed spaces that the drawing shows as rooms (2 m² or more) become rooms named "Room".

v0.8 adds: façade corners closed where a curved or straight façade wall stops short of the next one (free ends of façade or thick walls facing each other within an opening's width are extended to their corner; not where a wall already fills the gap, not for thin partitions inside); pier blocks (solid wall material no wall covers, next to a wall, compact and up to 3 m long) filled as walls, kept to the standard wall types; surveyed heights read the same way for any survey: window sill/head ("S 0.94", "H 2.58", "AH"), floor-to-sill + window height ("fs 0.92" + "sh 1.88"), door head ("dh 2.03"), ceiling ("u/s 2.52", "CH"; the floor's ceiling is the median of these); a floor whose most common spot height is under 2 m above the floor below (e.g. a roof plan's terrace) takes its next most common one. The report counts what came from the survey.

v0.7 adds: curved walls (arcs and bulged polylines are read; each curved face is measured inward against the wall-material grid, or paired with a concentric arc; pieces of one curve are fitted to one circle; straight chords along it dropped; windows and doors are placed on the curve); facets of a bend joined across window gaps; ribbon tool *Wall from Lines* (pick two face lines in the linked survey: two lines, two arcs, or an arc and a line; walls kept on re-runs, Comments "SurveyToRevit|PICKED|"); floors named from words in file names (Ground Floor Plan, Basement, Level 3, LG, Roof), so non-ISO surveys work; "BUILDINGS"/"External walls" layers read as walls, but BUILDINGS lines only as pairs (they also hold roof outlines). Second test building: 26-28 Mount Row (old survey 29463B: 8 floors read, levels 21.37–41.11 m from spot heights; Maltby 26045-300 is an elevation sheet and the LOD2 .rvt cannot be opened without Revit, both kept for façade work).

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
- `dotnet test`: 74 passed, 0 failed. The LiDAR reader is tested on synthetic GeoTIFFs in every compression the EA uses (made with tifffile; not survey data). No real LiDAR has been run yet: the cloud build machine cannot reach the EA servers.

## Known issues / limitations

- Façade runs merge only where pieces line up; bays, splays and pilastered corners can still give several short pieces.
- Single-line walls get 100 mm; some lines that are not walls (bay outlines, counters) may become walls. Marked Low confidence in Comments.
- Slabs and ceilings follow each floor's outer outline (no voids for stairs, lifts or light wells yet). One ceiling height per floor: the middle of the surveyed ceiling heights ("u/s 2.52") where given, else a default; rooms with different heights get the floor's value.
- Old surveys (e.g. Mount Row 29463B) mix neighbouring and roof lines into wall layers; some rooflights and stair outlines still become walls. Room numbers (not names) are not read.
- Curved walls need a curved face drawn as arcs or polyline bulges; a curve drawn only as many short straight lines is handled as facets (bridged), not as a true arc.
- Doors: placed in the wall the leaf is hinged on; swing direction is not set (Revit default). Curved walls ignored.
- Rooms only where the walls close a space; a label in an open-plan area with gaps gets no room (counted in the log).
- Windows: a few may land on a thin wall next to the façade rather than the façade itself; widths are measured from the window linework.
- The ground floor is split level (26.47 / 27.05); one level is used, flagged.

## Next single action

Mihai: install v0.8 and run *Build from Survey* on a copy of the project; send screenshots of a typical floor (plan and 3D) and the log.

Then: façades of the surveyed building from the survey elevation drawings (currently skipped); voids in slabs at stairs and lifts.

## Open cost / dependency decisions

None. New dependency: ACadSharp 3.8.0 (MIT).
