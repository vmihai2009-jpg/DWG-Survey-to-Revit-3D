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
| Flat roof below the mansard's top | A flat roof inside a section-shown mansard is built at the kerb when the roof plan's spot heights reach it. | `RoofTopMm` |

## Revit API traps

- Linked DWGs come in pinned: unpin to move or delete.
- `NewFootPrintRoof` wants an existing `ModelCurveArray`; null throws.
- A queued MCP build runs in whatever project is in front when Revit is idle. Pass `project` to `survey_build`.
- A full SP build takes about 13 minutes and Revit looks frozen. `sheets_only` remakes only the views and sheets, in about 40 s.

## Self-learning

Corrections that cannot be generic rules go in `survey-decisions.json` next to the DWGs: walls, not-walls, facade tops, levels. Every later build of that survey applies them, and the reading reports them. A correction that is generic belongs in the code, with a line in the tables above.
