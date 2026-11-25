# Survey to Revit: lessons learned

What reviewers caught by eye on real surveys, and the generic rule S2R follows since. Read this before changing the reading or the build. Add to it whenever a review finds something new: a lesson is the symptom, the cause, the rule, and where the rule lives.

Surveys used: 26-28 Mount Row (MR, 7 floors, mews) and 7 Swallow Place / 251-259 Regent Street (SP, 10 floors, mansard, curved corner). Check every change on both with SurveyLab (`tools/SurveyLab`) before release.

## Project set-up (BG office standard)

| Symptom | Rule | Where |
|---|---|---|
| Built in a default Autodesk project | The project comes from the whole office template (`UserSettings.ProjectTemplate`, default BG 2025 .rte). Build from Survey offers a new one; MCP `survey_new_project`. | `OfficeProject.cs`, `BuildFromSurveyCommand` |
| Template levels left beside the survey's | A template level "00_Ground Floor" is the survey's Ground Floor, moved to its height. New levels are named the same way: B1_, 03_, 00M_ for a mezzanine. | `ModelBuilder.SetUpLevels`, `OfficeLevelCode` |
| Plans not the office's existing plans | Each level's plan is the template's `01_` + level name, made like the template's own when missing. It takes the view template `*BGA_01_FP_Existing` and the Existing phase. | `PlanLike`, `ExistingTemplate` |
| Drawings not on sheets | Every plan, elevation and section gets a sheet made like the template's "Existing (View Name) Plan/Elevation/Section": same title block and parameters. | `ModelBuilder.Sheets.cs` |
| Wrong numbers | 10.200 + floor; basements count down (B1 = 10.199); mezzanine 10.200M; sections 10.250+; elevations 10.270+. All in settings. | `BuildSheets` |
| Browser folders | The sheet parameter "View Type" is set to "10.200 Existing Plans", "10.270 Existing Elevations", "10.250 Existing Sections". | `OnSheet` |
| Plans jump from sheet to sheet | One crop for all plans; each viewport is centred on the building's centre through `View.GetModelToProjectionTransforms()` and then `Viewport.GetProjectionToSheetTransform()`. The second alone throws viewports off the sheet. The drawing area allows for the BG title strip (130 mm). | `OnSheet` |
| Scope box asked for | The Revit API cannot create or resize scope boxes. Identical crops give the same result. | — |
| No survey on the sheets | Plans: each DWG is linked into its 01_ plan. Elevations: the survey elevation's own lines are drawn as detail lines (style "Survey (S2R)") by the elevation index's fit. A DWG linked into an elevation view is not drawn where `ImportInstance.GetTotalTransform` says. | `LinkPlans`, `DrawElevation` |
| Neighbouring buildings in front of the facade (SP) | Context blocks only when asked (build option `context`). | `RunSteps` |

## Reading and building

| Symptom | Rule | Where |
|---|---|---|
| Terrace roof missing a wedge (MR 1F) | A floor's slab is cut back to its own walls and the floor below's: it is that floor's roof. | `SurveyAnalyzer` (slab over the roof of the floor below) |
| Ceiling showing through slabs or on the facade | A ceiling stops 100 mm inside the full-height walls' outer faces. | `SurveyAnalyzer` (ceiling kept inside the floor's walls) |
| Floating stair outside the building (MR 1F) | Numbered flights chain by continuing numbers (gap 0-6, nearest top to foot). Numbering that restarts is another stair, so steps up to a door are not the stair inside. | `StairFinder.ByTreadNumbers` |
| Mansard built as upright walls (SP) | The top storey is the highest floor covering 40% of the one below (not a roof overrun). The kerb is the top of the steep face, not the roof's highest point. A section-shown mansard carries round to thin, windowless, set-back stretches. | `FacadeTops` |
| Mansard missing | Native footprint-roof strips, eaves to kerb, only the eaves edge sloping, 100 mm thick (300 mm stands up like a board at 70°). A stretch's inside is found by a point just off it lying in the floor. | `ModelBuilder.Mansard.cs` |
| Only 3 of 9 elevations read (SP, 2026-11-25): a classical front's windows do not stack like the plan's, so 35-52% of window columns lined up, under the 70% rule, though 9-64 columns matched on the right facade | A looser second pass for drawings still unmatched: at least 30% AND 6 columns lined up AND the best facade's count at most 75% of the next facade's; the note says "a looser match" so it is checked. And `survey-decisions.json` "elevations" with a `facadeLine` (model mm) assigns a drawing to a facade for every later build of that survey (the hub's `elevation_index` on the built model is the source: copy its result). `docs/examples/swallow-place-survey-decisions.json`. | `ElevationIndex.Build`, `ElevationIndex.ApplyDecisions` |
| OPEN (SP, 2026-11-25): S2R reads 4 drawings where the elevation sheet has 9; with 0.33.64 and the decisions file elevations 4, 5, 7 come out "no drawing at that x range" and Regent Street's drawing is labelled "Elevation 1" | `SurveyLab` on the real DWGs (offline, 37 s) is how to see it. TESTED AND REJECTED: the key plan's layer (`Zz_20_80_40-Location`) is already role Annotation, and ignoring the sheet-frame layer (`Zz_10_20-Frame`, role Other) in `ElevationIndex.Split` changed nothing (still 4 drawings), so it was reverted. Next: dump the islands `Split` forms (box, line count, title) for that sheet before changing anything; the hub's `elevation_index` splits the same sheet into 7 drawings by groups of window lines, so copy its grouping rule | `ElevationIndex.Split` |
| Project Information left as the template's placeholders after a build (title blocks read 00000-BGA-..., "Project Name", client "Owner") | Set by hand for the demo (Project Name, Project Number 2574 from the survey folder name "2574 7 Swallow Place & ..."). S2R should read the number and name from the survey path when it matches `<4 digits> <name>` and write them, leaving the client for a person | OPEN: `ModelBuilder.Sheets.cs` |
| GA set missing after a demo build | The demo build was run with `sheets: false`. `survey_build options {sheets_only: true, project}` makes the whole Existing set (10 plans, A/B sections, N/E/S/W elevations, site plan) on the BG template in under a minute, RIBA Stage 1, "For Information" | `ModelBuilder.Sheets.cs` |
| Facade detail built on a plain brick rear wall looks like random bars and blocks (SP rear returns, 2026-11-25) | Stone detail is only for a classical front: the hub's `facade_detail_pass` scores each facade from its elevation (6 or more moulded architraves, pediments, keystones, hoods, consoles) and skips relief on plain ones. Every facade-detail mistake, cause and rule is in the hub's `docs/FACADE-LESSONS.md`: read it before changing anything that builds facade detail. | hub `SurveyFacadePass.cs` |
| Classical detail (surrounds, pilasters, window frames, bands) missing after a build | S2R builds walls, piers and plain windows only. The detail comes from the hub's `facade_detail_pass` (windows as BG Window types, bands from sections as wall sweeps, pilasters from the plan, one family per repeated piece, a surround family per window design, mansard), run after `survey_autofix` and the elevation/section/facade indexes. | hub 1.38.0 `SurveyFacadePass.cs` |
| Flat roof below the mansard's top | A flat roof inside a section-shown mansard is built at the kerb when the roof plan's spot heights reach it. | `RoofTopMm` |

## Revit API traps

- Linked DWGs come in pinned: unpin to move or delete.
- `NewFootPrintRoof` wants an existing `ModelCurveArray`; null throws.
- A queued MCP build runs in whatever project is in front when Revit is idle. Pass `project` to `survey_build`.
- A full SP build takes about 13 minutes and Revit looks frozen. `sheets_only` remakes only the views and sheets, in about 40 s.

## Self-learning

There are three layers:
- **Per survey:** `survey-decisions.json` next to the DWGs (walls, not-walls, facade tops, levels). Every later build of that survey applies it.
- **Office-wide, learned from data:** corrections captured on every project, surveyor profiles, wall ranking and tuning. They are gated by a benchmark and adopted by a person: see `docs/LEARNING.md`.
- **Generic rules:** in the code, with a line in the tables above.
