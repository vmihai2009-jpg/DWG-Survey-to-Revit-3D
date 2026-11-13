# Survey to Revit: audit from a full MCP run, and the tools to build next

Written 2026-11-13 by the org-side Claude (Mihai's work account) after taking **7 Swallow Place & 251-259 Regent
Street** (Plowman Craven survey, 35 DWGs) from `survey_build` towards a 1:1 model with the BGMCP hub (MCP-Rev
1.9 -> 1.36.4). It lists, step by step, where the work after Survey to Revit's build was slow, what Survey to Revit
could have done itself, and exactly how, so that next time the engine does the heavy lifting and Claude only checks.

**For the Claude that implements this:** this file is a list of instructions, not code. Work through it in the
order of section 3 (P0 first). For each item: read the cited code, write the Revit-free part in
`SurveyToRevit.Core` with a synthetic test in `tests/SurveyToRevit.Core.Tests`, keep the Revit side thin, bump the
version, add a line to PROJECT_STATUS.md and an entry in COORDINATION.md (projects side) naming the item letter.

---

## 0. Who does what (Mihai, 2026-11-13)

> "mcp should analyse against drawings and pictures and take the responsibility to check against survey to revit"

**Survey to Revit is used on its own as often as with the MCP side.** Nothing below may make it do less when Claude
isn't there. A person clicking *Build from Survey* must get the best model the engine can make: every item is
implemented in full inside the plugin, with its own best call and its own checks. The MCP side is an extra layer
on top, not a part the plugin relies on.

| | **Survey to Revit (this repo), always** | **MCP side (BGMCP hub + Claude), when present** |
|---|---|---|
| Job | Read every drawing it can (plans, sections, elevations, roof plan) and build the most complete model it can, deterministically | Analyse the build against the drawings **and** the site photos; check it; overrule where it knows better |
| Judgement | Makes its own best call every time, by the rules in each item (for example classes the top of each facade and builds that). Never skips a thing so that someone else can do it later | Re-decides the calls the log marks uncertain, using photos and cross-checks the engine can't make |
| Photos | Doesn't read them | Uses them alongside the DWGs for facade and roof decisions |
| Checks | Full self-checks of the built model (item Q), in the report | Its own checks on top: `clash_check`, `facade_check`, `opening_clear_check`, `survey_fidelity`, `survey_section` |
| Log | **A structured log of every call it made** (item V): what, where, the evidence, how sure, the alternative it rejected and what would settle it, so a person or Claude knows exactly where to act | Reads the log first and acts where it points, instead of re-reading DWGs |
| Overrides | Reads `survey-decisions.json` if present and obeys it, as it obeys `layers.txt`; without the file, it builds exactly as it would anyway | Writes decisions there, so they survive rebuilds |

So every item below makes Survey to Revit **do the work, show its working, and accept an override**. The decisions
file is an override, not a dependency.

**`survey-decisions.json`** (in the survey folder, next to `layers.txt`; written by the MCP side, read here; a
person can edit it too). Every entry has `by` (tool or person), `evidence` (text: drawings, photo names) and `date`:

```json
{
  "version": 1,
  "facadeTop":   [ { "facade": "F29", "from": 0, "to": 38400, "class": "Mansard", "evidence": "Section-A slope 66°; photo regent-st-front" },
                   { "facade": "F78", "class": "SetBackStorey", "evidence": "photo swallow-place; section vertical" } ],
  "notWalls":    [ { "level": "06", "line": [x1, y1, x2, y2], "reason": "mansard skin cut by the plan" } ],
  "walls":       [ { "level": "04", "line": [x1, y1, x2, y2], "thickness": 215, "reason": "missed: face lines on Hidden layer" } ],
  "elevations":  [ { "sheet": "02000", "xRange": [a, b], "facade": "F29", "mirrored": false } ],
  "levels":      [ { "level": "00", "aod": 26470, "source": "elevation", "reason": "plan spots split level" } ],
  "windows":     [ { "facade": "F29", "u": 12345, "sill": 1450, "head": 3600, "source": "elevation" } ],
  "keep":        [ 326461 ]
}
```

Survey to Revit applies these after reading and before building, says in the preview which decisions it applied and
which no longer match anything (a stale decision is reported, never silently dropped), and tags the elements built
from a decision `SurveyToRevit|<level>|Decision`.

## 1. Rules every item follows

1. **Deterministic, no AI.** Everything here is geometry, text parsing, clustering and fitting, as the engine works
   today (WallDetector, WindowFinder, SectionReader). No model calls, no image recognition. Every threshold is a
   named constant in `Tolerances` or an argument, with the default that worked on Swallow Place given below.
2. **Transferable.** Nothing building-specific: no layer names, family names or numbers from this job in code.
   Layer roles come from `LayerRules` + `layers.txt`; sizes are arguments.
3. **Editable output.** What a person would draw: walls, floors, native roofs with slopes, loadable families with
   parameters, types for repeated sizes, one curve = one curved element. **No DirectShapes in a model to hand over**
   (today: piers, columns, stair blocks, roof fallback, unclear areas: see item M).
4. **Existing phase for everything**, including elements Revit makes by itself (stair railings: item B).
5. **Say how sure you are.** Keep the `SurveyToRevit|<level>|<what>` tags; add the source (drawing, layer) and the
   reason for Low/Medium (item R).
6. **Write what you read as JSON, in a shared place**, so the hub (and Claude) never re-reads a DWG to find
   something the engine already found: see section 4 for the files and their schema.
7. **Report, then build.** Every new step has a preview (`survey_build` without `confirm` already does this):
   counts, what it would make, CHECK lines.

---

## 2. Audit: where the work after the build was slow on Swallow Place

Each row: what happened, what it cost after the build (hub tools that had to be written, hours of Claude + Revit
time), the cause in this repo, and the item in section 3 that removes it.

| # | Step | What happened | Cost after the build | Cause here | Item |
|---|---|---|---|---|---|
| 1 | Roofs | Every roof came out as a flat 300 mm **floor slab**, not a roof; PROJECT_STATUS: "NewFootPrintRoof fails on every survey so far, cause unknown" | hub `roof_from_survey`, `roof_solver`, `mansard_roof`; a day of roof work | `ModelBuilder.cs:987` passes `out ModelCurveArray footprint` uncreated: Revit 2025 throws "Value cannot be null". Found and fixed the same bug in the hub's `create_roof` today | A |
| 2 | Phases | 118 stair railings (and anything Revit adds by itself) in "New Construction"; every phased view wrong until `phase_fix` | hub `PhaseKeeper` + `phase_fix` | `Mark()` (MB:1075) sets the phase only on elements it tags; railings made by `StairsEditScope` are never tagged | B |
| 3 | Top storey | Three top conditions on one building (mansard on the street fronts, set-back attic at the rear, parapets); the plan cut the mansard's sloping skin ~1 m above the Sixth Floor and it was built as **13 full-height thin walls** ("Concept Wall 100mm/40mm", Low); the real mansard roof clipped through all of them | `clash_check`, photos to tell mansard from set-back, 13 deletions approved by Mihai | Nothing classifies the top of a facade (PROJECT_STATUS:13, no code); any line on the top storey becomes a wall; walls run to the next level and are never trimmed by a roof (MB:468-473) | C, D |
| 4 | Elevations | Elevations skipped entirely. Window sills/heads came from plan S/H labels (internal, wrong for the facade), 52 windows didn't match their elevation; **101 windows drawn in the elevations had no model window** | hub `elevation_index`, `dwg_elevation`, `survey_autofix` (elevation heights), `window_look`, `elevation_fit_check`, `elevation_overlay`: most of two days | SA:186 skips any sheet without a level; only `survey_overlay` links them as 2D at the DWG origin | E, F |
| 5 | One sheet, many drawings | The elevations DWG holds several elevations side by side (one, "Elevation 4", was left out); each part has its own height fit; level marks on one part paired with another part's lines | hub `elevation_fit_check` (fit per part along the sheet) | No sheet splitting for elevations/sections (SheetSplitter only does plans) | E, N |
| 6 | Window types | One type per 50 mm size: 91 window types for one building; a person changing a design edits dozens | hub `window_family` (one parametric "BG Window"), `window_look`, `opening_clusters` | `MB:1208/1428` duplicates "Concept Window WxH" per size; `ConceptFamilies.Window` is width/height only | G |
| 7 | Sections | Section-A's fit to the facade was out by 440 mm at the wall face; level marks paired with the wrong line (4 of 41) | hub `section_index`, `facade_profile` (own fit + wall-face shift) | `SectionReader` fits the cut to the plan but doesn't publish the fit, the wall-face offset or the profile | H |
| 8 | Stepped floors | One level per floor; Ground floor slab off by +620 mm in part; split levels flagged but not built | hub `survey_floor_steps` (spot heights per room -> slab pieces) | One level per floor (PROJECT_STATUS:94); slabs = one outline | I |
| 9 | Curves | The curved corner came out as a staircase of short walls/slab edges; facade tools skipped curved walls; the mansard needed its corner as one arc | hub `curve_check`, `curve_walls`, arc fitting in `mansard_roof` (6 m chords) | `ArcChains` exists now but long chords (survey drew the corner in 4 m chords) are outside its limits; slab outlines aren't arc-fitted | J, K |
| 10 | Slab outlines | Slab sketches Revit rejected; outlines with every 100 mm jog of the plan (138 edges at the Sixth Floor), which broke any roof or offset built from them | hub outline fallbacks, `Simplified()` in `mansard_roof` | Slab = the floor's raw outer outline (`OutlineRepair` only on failure) | K |
| 11 | Detail as DirectShapes | "Survey - pier/column", "Detail: Wall" (leftover masonry), stair blocks, roof fallback: not editable; flagged by `editability_audit` | hub conversion work; Mihai: "none of these elements are editable" | `BuildDetails` (MB:876), MB:569/685/704/1012/1069 | M |
| 12 | Facade registry | Every facade tool needed "which walls make facade F29, its line, its elevation": built by hub `facade_list` from walls, curved walls skipped | hub `facade_list`, `facades.json` | The engine knows its facade walls (`FacadeWalls.Consolidate`, `FacadeStack`) but doesn't publish them | O |
| 13 | Heights disagree | Plan spot heights vs elevation level marks vs section marks disagreed by >30 mm on some floors, silently | hub `survey_conflicts` | Level = commonest spot height; sections only produce a CHECK note | P |
| 14 | Self-check | Clashes (roof through walls, relief through windows, duplicate walls) found only by eye in Mihai's screenshots | hub `clash_check`, `opening_clear_check`, `detail_support_check` | `ModelChecks.Run` checks the plan reading, not the built model | Q |
| 15 | Layer roles | Elevation layers (windows on `Ss_25_30_95-Window`, stonework on `EF_25-Wall` and `Pr_20_93_52-DecDetail`) and the sections' `Ss_25-Profile` had to be learnt by Claude each time | hub `layer_roles_check`, notes in docs | `LayerRules` is written for plans | S |
| 16 | DWG prep | 35 DWGs: duplicates (fire/area plans), several drawings per sheet, a title block, a key plan on the section sheet, 19,780 lines on one elevation; nothing tells the next tool which drawing is which | Claude reading sheets by hand with `dwg_image`, `cad_lines` | No manifest of what each DWG holds (analysis keeps it in memory only) | N |
| 17 | Decisions lost | Site photos (Street View) settled what the DWGs couldn't (mansard on the street fronts only, set-back attic at the rear); the MCP side's decisions (delete 13 skin walls, which elevation is which facade) live only in the model and are undone by any rebuild | repeating the analysis after each rebuild | No channel from the checker back to the engine (only `layers.txt`, for layers) | T |
| 18 | Rebuilds | Rebuilding chosen levels turns off links, stairs, roofs and sections (MT:342); every rebuild undid hub work on those floors | re-running hub steps after each rebuild | Rebuild replaces by tag per floor, with no record of what other tools hung on those elements | U |

---

## 3. Instructions, in order

### P0: bugs and defaults that cost the most

**A. Roofs that are roofs (`ModelBuilder.cs:965-1024`).**
- Create the mapping array first: `ModelCurveArray footprint = new ModelCurveArray(); _doc.Create.NewFootPrintRoof(curves, level, type, out footprint);`.
  This alone should turn the "slab fallback" into real roofs. Grep the repo for any other `out` arrays handed to
  Revit and do the same.
- After creating, `doc.Regenerate()` and check the roof has a solid (sum of `Solid.Volume` > 0) inside a
  `SubTransaction`. "Can't make footprint roof" otherwise only appears at commit and takes the whole floor's
  transaction with it. Roll back that sub-transaction and fall back as today, logging the reason.
- Simplify the outline before the roof (item K) and set `DefinesSlope = false` on every edge for flat roofs.
- Test in Revit on a 10 x 6 m rectangle first (it makes a roof at 66° on all edges: 36.9 m3).

**B. Everything in the survey phase, including what Revit adds (`Mark()`, MB:1075).**
- After each floor's transaction, collect every element created in it, not only the tagged ones. Use
  `doc.GetAddedElementIds()` via an `IUpdater`, or the before/after id sets of `FilteredElementCollector`.
  Railings from `StairsEditScope`, stair runs and landings, and openings must all get `_phase`.
- Test: a build with stairs leaves 0 model elements in a later phase. Count by `CreatedPhaseId` in the build report.

**C. What the top of each facade is: classify from evidence, never assume (new, Core + Revit).**
Most buildings have no mansard. Swallow Place has three top conditions on one building:
- a mansard on the street fronts;
- a flat set-back attic with real walls at the rear;
- flat roofs behind parapets.

So don't look for "a mansard". For each facade stretch, classify the top of the building from the evidence the
survey gives, and build each class its own way.

- **Classes:**

  | Class | What the evidence shows | Build |
  |---|---|---|
  | `FlatParapet` | wall runs up past the roof plan's flat roof by a parapet | walls to the parapet top; flat roof from the roof plan |
  | `SetBackStorey` | top storey's face set back from the facade below, **vertical** in the section (or no section and thick walls ≥ 200 mm, or windows in them) | **real walls, as today**; flat roof at the set-back's edge |
  | `Mansard` | section profile above the wall top slopes steeply (≥ 45°, rise ≥ 1.5 m) | sloped roof band (below); the plan's cut through it is not walls |
  | `Pitched` | section or roof plan shows a slope from the wall top (eaves at the wall head), with ridges, hips or verges in the roof plan | pitched footprint roof with slopes per edge (the hub's `roof_solver` reads the faces from the roof plan) |
  | `Unknown` | no section crosses it, the roof plan says nothing there | **build as today** and flag it in the report for a person/photo |
- **Evidence, strongest first:**
  1. a section crossing the stretch (item H profile);
  2. the roof plan (flat faces and their edges, slopes, ridges, hips: `roof_solver`'s edge classes);
  3. the elevation's outline above the wall top (sloped line vs vertical: item E);
  4. the plan alone (set-back and thickness).

  Classify `Mansard` or `Pitched` only from 1-3. The plan alone may raise a CHECK, never remove a wall.
- **Build its own call, every time.** Survey to Revit classifies and **builds** each stretch by the rules above:
  the mansard roof and its skin lines left out where a section or roof plan shows the slope, walls where it shows
  vertical. With the plan alone it builds walls, as today. Nothing is held back for someone else to finish.
- **Show its working.** Each stretch's class goes to `roof-edges.json` and the log (item V) with the evidence, the
  confidence (High from a section, Medium from a roof plan or elevation, Low from the plan alone), the class it
  rejected and what would settle it ("no section crosses F78; a photo or a section would confirm SetBackStorey").
- **Overridable.** An entry in `survey-decisions.json` (`facadeTop`, `notWalls`) wins over the engine's call on the
  next build.
- **Mansard only:** mark the plan's thin lines on the cut through the slope as `RoofSkin` in the analysis and leave
  them out of the walls. Keep any wall on that line that hosts a window: that is a dormer front (tag it `Dormer`).
  On Swallow Place these were 13 "Concept Wall 100mm/40mm", Low, no inserts.
- **Publish** `roof-edges.json` (section 4): for each facade stretch, the class, the evidence used, and for sloped
  classes the eaves (out from the facade line, z), the kerb or ridge, the slope, and the section or roof face used.
- **Build a mansard (Revit)** as the hand-made reference model does it; Claude read it with the hub's `roof_detail`:
  - one Basic Roof footprint, every edge sloped at one angle, a curve as one arc edge, cut off at the kerb;
  - **only along the stretches classed `Mansard`.** A whole-building footprint with every edge sloped made no solid
    at any angle on Swallow Place: the rear limbs broke Revit's hip solver;
  - the robust form that worked: a band, the chain of mansard edges closed by the same chain set in by the slope's
    run (the kerb line), with the chain edges sloped and the kerb and the two ends flat;
  - the flat roof behind is the roof plan's top face.
- **Defaults that worked on Swallow Place:** slope from the section (66.1°, eaves 23,749 to kerb 26,749 mm, run
  1.36 m). The hub's `mansard_roof` (MCP-Rev `SurveyRoofTools.cs`) is the reference implementation.
- **Test with synthetic plans for all five classes.** A building with no section and no roof plan must build
  exactly as today.

**D. Walls under roofs stop under them (`MB:468-473`, `BuildRoofs` MB:965).**
- Build roofs before the top storey's walls are finalised. Then, for each wall whose solid intersects a roof
  (`ElementIntersectsElementFilter`):
  - if it is an inner partition: `wall.AddAttachment(roof.Id, AttachmentLocation.Top)` (Revit 2024+);
  - if it is a parapet line that the roof plan shows as an upstand: keep it and set its top to the upstand height;
  - report the rest as clashes (item Q).

### P1: read the elevations and sections, publish what they say

**E. Elevations: split, identify, place, fit (new; the hub's `elevation_index`, `elevation_fit_check` and
`elevation_overlay` are the reference behaviour).**
1. **Split each elevation sheet into drawings.** Cluster the linework into islands with a gap ≥ 2 m. Each island
   with a title text matching `ELEVATION|ELEV\.|FACADE` (plus the `layers.txt` `label` mechanism for other words)
   is one drawing; title blocks and key plans are dropped as in `SheetSplitter`.
2. **Find each drawing's facade.** For each facade run of the built plan (item O), compare the horizontal pattern of
   window openings: the model's window centres along the facade against the drawing's window clusters
   (`Ss_..Window` or the Window role) along x. Use a 1D cross-correlation over shift and ±2% scale; the best match
   ≥ 0.7 wins. Mirror the drawing when it is seen from the other side. Use street-name text on the plan near the
   facade as a tie-break.
3. **Fit heights per part.** Read level marks (`+26.470`, `FFL 32.36`, `▽ 36.78`) with the line they sit on. Fit
   z = a·y + b for each stretch of the sheet, not one fit per sheet: on Swallow Place the right-hand part of an
   elevation was a different block with its own fit. Reject pairs off by more than 50 mm, and say which part.
4. **Place** a 2D overlay per facade: an elevation view "Survey <F>" with the drawing behind the facade, cropped
   to it. This is what the hub's `elevation_overlay` does today.
5. **Publish** `elevation-index.json` (section 4).

**F. Windows from the elevations (`WindowFinder`, SA:331).**
- Sill and head heights come from the elevation's window outline when an elevation covers that facade; the plan's
  S/H labels are internal heights. Width: the plan's reveals where they agree within 50 mm, else the elevation's.
- **Place the windows the elevation draws and the plan doesn't** (101 on Swallow Place): where a wall of that floor
  stands over the window's whole height, put the window in from the elevation alone and tag it
  `ElevationOnly` (Low).
- **Read the panes** (innermost closed loops inside each window outline): panes across, meeting rail height, frame
  and bar widths, arched head. Write them on the type (item G). The hub's `WindowPattern.Read` is the reference
  implementation.

**G. One parametric window family, types by design (`ConceptFamilies.Window`, MB:1208/1428).**
- Generate one family from Metric Window.rft with reference planes and locked parameters: Width, Height, Frame
  Width, Bottom Rail, Frame Depth, Inset, Meeting Rail Height/Width, Panes Across (1-6), Panes High
  Bottom/Top, Glazing Bar Width, materials. The hub's `WindowFamilyBuilder.cs` (MCP-Rev) does exactly this:
  25 planes, 15 boxes, 60 locks.
- Cluster windows into types by their pane settings with sizes within 25 mm (the hub's `opening_clusters`), and
  name types by design: "BG Window 2x2 sash 1200x1800", not one per 50 mm step.
- Arched heads get their own family only when the arch is really different.

**H. Sections: publish the fit and the profile (`SectionReader.cs`).**
- Publish per section: the cut line, its fit to the plan (scale, shift, score), the level marks used and rejected,
  and the offset of the section's wall face from the model's facade line at the cut. On F29 that offset was
  -440 mm, and every profile taken without it was out by that much.
- Read the outer profile at 10 mm steps of height (the outermost line at each height, made monotonic). Classify
  its parts: wall face, bands/cornices (projection ≥ 50 mm, height ≤ 1 m), balustrade, parapet, and above the wall
  top: vertical set-back, steep slope (mansard), shallow slope (pitched) or flat (feeds item C).
- Publish everything in `section-index.json` (section 4). The hub's `facade_profile` reads it to sweep cornices and
  build the mansard instead of re-deriving it.

**I. Floors that step (new; the hub's `survey_floor_steps` and `Survey/SlabSteps.cs` are the reference).**
- For each room on a floor, take the spot heights inside it. When ≥ 75% of the floor's spots fall in rooms, give a
  room with ≥ 3 spots (≥ 5 for a step over 300 mm) whose median is off the level by more than 30 mm its own slab
  piece at that height.
- Split-level floors (Ground 26.47 / 27.05 here) get two slab heights instead of one flagged level.
- Report spots within tolerance before and after. On Swallow Place: Basement 42 -> 62 of 105, Ground 12 -> 29 of 56.

**J. Curves that are drawn as chords (`ArcChains.cs`).**
- Allow long chords. The curved corner was drawn in ~4 m chords, so use max piece 6 m, turn ≤ 35° at each
  joint, every vertex within 25-60 mm of the fitted circle, and ≥ 3 pieces.
- Run it on slab outlines and facade runs as well as wall lines.
- One fitted arc becomes one curved wall, one arc slab edge and one arc in any roof sketch.

**K. Clean outlines for slabs and roofs.**
- Before any slab or roof sketch:
  1. fit arcs (item J);
  2. drop line-line corners within 350 mm of the line through their neighbours, corners that turn < 12°, and
     spikes (turn > 160°);
  3. remove edges shorter than Revit's short-curve tolerance (about 0.8 mm; use 2 mm).
- Offset (eaves set-backs, kerb lines) only **after** simplifying. Offsetting the raw 138-edge slab edge made a
  spike that doubled back.
- The hub's `Simplified()` and `ArcsFitted()` in MCP-Rev `SurveyRoofTools.cs` are the reference.

### P2: editable detail, a shared registry, and checks

**M. No DirectShapes (MB:569, 685, 704, 876-900, 1012, 1069).**
- **Piers and columns:** a loadable Generic Model (or Column) family generated per distinct outline: a plan-profile
  extrusion with Base/Top parameters, the outline as a sketch. Identical outlines share one family; place
  instances. The hub's `DetailFamilyBuilder.BuildVertical` is the reference.
- **Round columns:** a revolve family.
- **"Detail: Wall" leftover masonry:** if it is a rectangle within 20 mm, make a wall; otherwise use the pier family
  above.
- **Stairs Revit refuses:** keep the stepped blocks, but as an in-family extrusion per flight, not a DirectShape.
- **Roof fallback:** a sloped or flat slab with the slope arrow. Never a DirectShape.
- **"Unclear" areas:** keep as now, off by default, and never in a model to hand over.

**N. Survey prep: a manifest of what every DWG holds (new tool `survey_prep`, report-only).**
Read every DWG in the folder once and write `survey-manifest.json`. The engine has most of this in memory today
and drops it.
- **Per file:** units (header, else inferred), extents, layer list with entity counts and roles, block and xref
  names, duplicate lines (same layer and ends within 1 mm) and zero-length entities, title text.
- **Per drawing on a sheet** (islands, as in item E1): kind (plan / elevation / section / roof plan / key plan /
  title block / unknown), level code, title, extents, the transform to the model (for plans: the SheetSplitter
  shift; for elevations and sections: the item E/H fit), and for elevations the facade id.
- **Duplicates:** fire and area plans of a level that is already read, with the reason kept or skipped (SA:194 does
  this silently today).
- **Never edit the surveyor's DWGs.** If a cleaned copy helps (de-duplicated lines, blocks exploded on the roles
  used, xrefs bound), write it to `<folder>\_prep\` and record the source in the manifest.

**O. Publish the facades (`FacadeWalls`, `FacadeStack`).**
Write `facades.json` from the build: per facade run, an id, a name from the nearest street text, the walls in
order (including curved ones), the outer face line (with arcs), the outward bearing, base/top per stretch,
window ids, and the elevation drawing that covers it (item E2). Keep the ids stable between builds: number them
clockwise from grid north and match runs by line within 300 mm. The hub's `facade_list` uses the same schema
today; once this exists, it only reads it.

**P. Heights that disagree (new; the hub's `survey_conflicts` is the reference).**
For each floor, compare the plan's spot-height level, the elevations' level marks (item E3) and the sections'
marks (item H). Where they differ by more than 30 mm, pick one by a declared order (plan, elevation, section;
changeable) and log the choice in the report and `levels.json`. Never average silently.

**Q. Check the built model (extend `ModelChecks`, Revit side).**
These are full checks, for every user, with or without Claude. They should catch what Mihai found by eye on Swallow
Place: floors through the roof, roofs through walls, detail floating in front of nothing. After the build, add to
the report and the log (item V):
- each floor and slab's intersections with roofs: a slab edge pushing through a sloped roof, or a roof-plan slab
  overlapping the roof band;
- detail elements (piers, columns, details) whose back face reaches no wall, floor or roof within 50 mm (floating),
  or whose front is behind the wall face (buried);
- each roof's intersections with walls (item D);
- windows whose glass is crossed by another element: a ray through the opening;
- duplicate walls (same line within 20 mm, overlapping by ≥ 50%);
- walls whose base or top is more than 50 mm off the levels the plan says;
- elements in a wrong phase (item B).

Give element ids and the fix, as the hub's `clash_check`, `opening_clear_check` and `detail_support_check` do.

**R. Provenance on every element.**
Keep the Comments tag, and add (in extensible storage or a second parameter) the source drawing, layer, entity
handle(s) and the reason for the confidence. For example: "Low: single line, 100 mm assumed", "Low: bridge across a
1.2 m gap", "Medium: facade join". Then the hub's `review_queue` and a person can go straight from an element to
the DWG lines it came from.

**S. Layer roles for elevations and sections (`LayerRules.cs:125-154`).**
Add roles and default patterns for elevation/section linework:
- `ElevWindow`: window layers on an elevation (`Ss_25_30_95*`, `*WIN*`, `*GLAZ*`);
- `ElevStone`: wall and stonework on an elevation (`EF_25*`, `*DECDETAIL*`, `*ORNAMENT*`);
- `SectionProfile`: `Ss_25-Profile`, `*PROFILE*`, `*SECT*CUT*`;
- `LevelMark`;
- `ElevText`.

The same `layers.txt` lines override them. Check each role from geometry too (the hub's `layer_roles_check`: a
window layer is mostly small closed rectangles, a profile layer is long open polylines).

**V. A log that says where to act (new; replaces the free-text CHECK lines as the main output, which stay too).**
Write `build-log.json` (and keep the readable report) on every analyse and build. One entry per call the engine
made that wasn't certain, and per check it failed:
- `kind`: wall-from-single-line, bridge, mansard-skin, facade-top, level, window-height-source, elevation-match,
  roof-fallback, clash, floating, and so on;
- `where`: level, element ids, model xy/z, drawing + layer + entity handles;
- `confidence`: High / Medium / Low, and **why** in one line;
- `evidence`: what it used; `alternative`: what it rejected and why;
- `settle`: what would decide it ("a section through x = 12.4 m", "the elevation of F78", "a site photo of the
  roofline"), and the decisions-file entry that would override it;
- `severity`: how much of the model it affects (m of wall, m² of slab, number of windows).

Sort by severity × (1 - confidence). The first lines are where a person or Claude should act first. The
standalone user sees the same list in the review list (`ReviewListCommand`); Claude reads the JSON
(`survey_review` returns it).

**T. Read and obey `survey-decisions.json` (new, Core).**
Photos stay on the MCP side: Survey to Revit doesn't read them. The decisions file lets the MCP side's decisions,
made from the drawings and the photos, survive a rebuild. Without the file, the build is exactly what the engine
would do on its own.
- Load `survey-decisions.json` (section 0) with `layers.txt`, and include it in the analysis cache key (SA:148).
- Apply each kind at its step:
  - `elevations`: before item E2's matching;
  - `levels`: at the level estimate;
  - `walls` and `notWalls`: after `WallDetector`, matching lines within 50 mm and 2°;
  - `facadeTop`: item C;
  - `windows`: item F;
  - `keep`: element ids to leave alone on a rebuild (item U).
- In the preview, list applied decisions, decisions that match nothing (stale) and decisions that contradict a
  stronger reading (for example `notWalls` on a line that hosts a window). Never drop one silently.
- Test: a synthetic plan plus a decisions file builds what the file says, and a rebuild without the file builds
  as before.
- Never commit photos or decisions files from real jobs to this repo (Street View images are Google's copyright;
  jobs are confidential).

**U. Rebuilds that keep the work done since (MT:342, `BuildElements`).**
Before replacing a floor's generated elements, list what other tools hosted on them or tagged near them: windows
retyped by the hub, wall sweeps, families placed against the walls, rooms. Then:
- match walls old to new by line within 20 mm and same thickness, and re-host or keep instead of deleting;
- say in the preview what will be lost;
- add an option `keep_matching: true` (default) so a rebuild after a layers.txt fix doesn't undo a day of facade
  work.

---

## 4. Shared files (the engine writes, the hub and Claude read)

Write them to the reports folder the hub already uses (`<reports>\<model name>\`, set in Settings) and to the survey
folder's `_prep\`. JSON, millimetres in the model's internal frame unless named `aod`. Keep each schema stable,
with a `version` field.

| File | Written by item | Contents (minimum) |
|---|---|---|
| `survey-manifest.json` | N | files, drawings per sheet, kind, level, title, transform, duplicates/skips with reasons, layer roles |
| `levels.json` | P | per floor: plan level, elevation marks, section marks, chosen value and rule |
| `facades.json` | O | per facade: id, name, walls, line (with arcs), bearing, base/top per stretch, windows, elevation id |
| `elevation-index.json` | E | per elevation drawing: sheet, x range, facade id, mirrored, per-part height fit (a, b, residual, marks used) |
| `section-index.json` | H | per section: cut line, plan fit, level marks, wall-face offset, outer profile (u, z), classified parts |
| `roof-edges.json` | C, H | per facade stretch: class (FlatParapet / SetBackStorey / Mansard / Pitched / Unknown), evidence, and for sloped classes eaves (out, z), kerb or ridge, slope, source |
| `build-log.json` | V | every uncertain call and failed check: kind, where, confidence + why, evidence, alternative, what would settle it, severity; sorted worst first |
| `windows.json` | F, G | per window: id, facade, sill/head/width with source (plan/elevation), pane pattern, type |

Today the hub writes `facades.json`, `elevation-index.json` and `section-index.json` itself in the same schema
(MCP-Rev `Survey/*.cs`, `Survey*Tools.cs`). Match it, so that both sides can switch over without changes. The hub's
`mansard.json` (eaves, kerb, line per facade) is the `Mansard` case of `roof-edges.json`; the hub will read the new
file once it exists.

---

## 5. Done when (measure on Swallow Place, then on a second survey)

- `survey_build` makes **native roofs** (0 slab fallbacks), with each facade's top classed from evidence.
  - On Swallow Place: the mansard as a sloped roof on the street fronts, 0 thin full-height walls under it, the
    rear's set-back attic still walls. `clash_check` on the roofs reports only kerb-edge touches.
  - On a survey with no mansard: the same walls as today, and no roof storey invented.
- **0 elements in a later phase** after a build.
- Windows: every elevation window has a model window with sill/head within 30 mm of the elevation, and **≤ 20
  window types** on Swallow Place (it had 91), all of one parametric family.
- The curved corner is **one** curved wall per storey and one arc in each slab and roof sketch.
- **0 DirectShapes** in the build (except "unclear", off by default).
- `facades.json`, `elevation-index.json`, `section-index.json`, `roof-edges.json` and `survey-manifest.json` exist after
  `survey_analyze`, and the hub's facade tools run without re-reading a DWG.
- A rebuild of one floor after a `layers.txt` change keeps the hub's windows, sweeps and families on walls that
  didn't move.
- **Standalone:** with no MCP add-in and no decisions file, *Build from Survey* gives the same model as above
  (roofs, top-of-facade classes, elevation windows, checks), and the review list shows the log's worst-first
  entries.
- Every decision the MCP side wrote to `survey-decisions.json` (the Swallow Place set: F29/F53 mansard, rear
  set-back storey, the 13 skin lines not walls) survives a full rebuild, and the preview lists them as applied.

## 6. Order of work and sizes (guide)

| Order | Items | Why first | Size |
|---|---|---|---|
| 1 | A, B | one-line bug + one collector: removes two whole hub workarounds | small |
| 2 | K, J | every roof/slab after depends on clean outlines with arcs | small-medium |
| 3 | C, D | the top of each facade classed from evidence (mansard, set-back, parapet, pitched): the biggest visible error on Swallow Place | medium |
| 4 | O, N | the registry and manifest everything else hangs on | medium |
| 5 | E, F, S | elevations: the largest time sink (two days of hub work) | large |
| 6 | G | one window family + clustering | medium (port from MCP-Rev) |
| 7 | H, P | sections and height reconciliation | medium |
| 8 | I | stepped floors | medium (port from MCP-Rev) |
| 9 | M, Q, R, U | editable detail, checks, provenance, safe rebuilds | medium each |
| with 3 | V, T | the structured log (where to act) and the decisions file (overrides that survive rebuilds) | small-medium |

Reference implementations to port (same author, same rules) live in MCP-Rev: `SurveyRoofTools.cs` (mansard,
outlines, arcs, clash_check, roof_detail), `WindowFamilyBuilder.cs`, `Survey/WindowPattern.cs`,
`Survey/ElevationFit.cs`, `Survey/SectionProfile.cs`, `Survey/SlabSteps.cs`, `Survey/CurveChains.cs`,
`DetailFamilyBuilder.cs`, `SurveyFacadeCheckTools.cs`. The method and lessons are in MCP-Rev
`docs/SURVEY-TO-1TO1.md`, and the numbers in `docs/SURVEY-RESULTS.md`.
