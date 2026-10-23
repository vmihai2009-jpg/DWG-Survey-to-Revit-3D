# DWG Survey to Revit

Revit 2025 add-in that turns 2D DWG building surveys into an editable concept model (walls, slabs, levels, windows, doors, rooms). Early stage: see [PROJECT_STATUS.md](PROJECT_STATUS.md) for what works today.

## Install (Windows, Revit 2025)

**From the release zip** (no tools needed):
1. Unzip anywhere.
2. Double-click `install.cmd`. No admin password is needed: it installs for your Windows user only.
3. Restart Revit 2025. Revit may ask whether to load the unsigned add-in: choose *Always Load*.

**From source:** install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then double-click `install.cmd` in the repository folder; it builds and installs.

Files go to `%AppData%\Autodesk\Revit\Addins\2025\`. Remove with `uninstall.cmd`.

**Updating (from v0.13):** save the new release zip into Downloads (or the folder set in **Settings**; no need to unzip), click **Updater** on the Survey to Revit panel and choose *Install*. It works straight away, with no Revit restart: the next click on *Build from Survey* runs the new version. When the panel shows **Update ready**, a newer zip is waiting.

**Coming from v0.12.x:** v0.13 changes how the add-in is installed, so do this once: put the v0.13 zip in Downloads, click the old **Update** button, then restart Revit. After that, updates no longer need a restart.

**Settings** (on the panel): where the Updater looks for zips, whether to show *Update ready*, which installed version runs (to go back to an earlier one), how many older versions to keep, the default slab thickness and LiDAR radius, and where reports are saved. Saved per user in `%AppData%\SurveyToRevit\settings.json`.

## Use

1. Open a Revit project (try a copy first).
2. *Barr Gazetas* tab → *Survey to Revit* panel → **Build from Survey**.
3. The start window shows the version and the three steps. Add the survey DWGs (by file, a whole folder, or drag and drop; all of them is fine: elevations, sections and duplicate plans are skipped), and LiDAR tiles if wanted, then *Next*.
4. A window lists each floor found: tick the floors to build, correct a level or ceiling height if needed (highlighted floors need checking), and choose what to build:
   - link each plan at its level (shared coordinates),
   - walls ("Concept Wall 160mm" etc., in 20 mm steps), joined across door gaps; façades as thick as the masonry drawn; no two walls in the same place; glazed screens as curtain walls,
   - windows in the walls (sill and head from the survey texts where given, e.g. "S 0.94"/"H 2.58" or "fs 0.92"/"sh 1.88"),
   - doors, read from each door symbol (leaf and swing give the opening's width and direction; two swings meeting = double door; sliding and folding doors drawn as a thin panel across an opening are doors too); a door between two wall ends gets a wall made across the opening to hold it; height from "dh 2.03" texts where given, else 2100 mm,
   - rooms named from the survey's room labels (and "Room" for unlabelled spaces the drawing encloses); no room separation lines are drawn,
   - floor slabs ("Concept Slab 300mm") from each floor's outline,
   - ceilings (one per floor, at the height in the table: the survey's "u/s"/"CH" ceiling heights where given),
   - stairs from the stair layers, with an opening in the floor slab above each: Revit stairs from the drawn treads (curved flights stay curved), up to the next level; a stair Revit refuses becomes stepped blocks ("Survey - stair"),
   - odd columns and chimneys as generic models ("Survey - column"), and masonry too odd for a wall (round columns, curved corner piers, half-round pilasters) as generic models traced to its outline ("Survey - pier"); everything else is walls,
   - roofs: flat concept roofs ("Concept Roof 300mm"), only where a roof plan or a roof layer shows them, at the middle of the spot heights on each roof,
   - section views ("Survey Section A" …) along the cut lines of a survey section sheet (placed from the key plan on the sheet); the heights on the sections set the ceilings and check the levels,
   - optional (off by default): generic models (named "Survey - check") where the drawing could not be read.
   - surroundings, if LiDAR tiles were selected too (see below).
   *Layers:* each layer is read by its name (Uniclass, AIA or plain English). *Open report* lists every layer and what it was read as. If your survey uses other names, put a `layers.txt` in the DWG folder, one line per layer: `A-GLAZ-CURT = CurtainWall` or `MY_STAIRS* = Stair` (* matches anything). Roles: Wall, CurtainWall, Window, Door, LevelText, RoomName, HeightText, Stair, Ramp, Lift, Column, Roof, Rooflight, Chimney, Balcony, Railing, Fitting, Ceiling, Area, Overhead, Annotation, Other.
5. **Build** sets project coordinates and north from the survey, creates the levels, then builds what was ticked. *Open report* shows the full analysis without changing anything.
6. Anything missed: open the floor plan with the linked survey and use **Wall from Lines** (same panel). Pick the two face lines of the wall (straight, curved, or a curve and a straight line); repeat; Esc to finish.
7. A log is saved in `%TEMP%\SurveyToRevit`.

### Surroundings (optional)

Easiest: in the build window click **Get from the Environment Agency…**. A map of the area opens with the survey outline in red and the area to download as a blue square (drag it to move it, set its size with the radius). **Download and use** fetches the Environment Agency LIDAR Composite DSM and DTM 1 m for that square (free, Open Government Licence) and ticks *Surroundings from LiDAR*. Needs internet access to environment.data.gov.uk.

Or by hand:

1. Download the free LiDAR for the site from the [Defra Survey Data Download](https://environment.data.gov.uk/DefraDataDownload/?Mode=survey): draw a box round the site and download the **LIDAR Composite DSM 1m** and **LIDAR Composite DTM 1m** tiles. Unzip them. (The zips can be given as they are.)
2. In *Build from Survey*, select the DWGs and the `.tif` tiles together (keep "DSM"/"DTM" in the file names).
3. Optionally add a building footprint shapefile (`.shp`, British National Grid), e.g. OS MasterMap Topography buildings, for exact outlines.
4. Tick *Surroundings from LiDAR* and choose the radius. The ground and neighbouring blocks go on a "Context" workset (workshared projects) in the Existing phase. The surveyed building is never changed by this.

Running it again replaces only the elements it created before on those floors. Undo reverses the whole run.

### Office Test-Fit

Open the floor plan of a floor and click **Office Test-Fit**. The workplace test-fit opens in a window over Revit, drawn on that floor: the outline of its floor slabs (or its rooms, or the box round its walls, when there is no slab), its walls, columns and rooms. Stairs, lifts and WCs (by room name) start as core spaces; the rest of the floor starts as one workspace. Set the density (m² NIA per workstation), add meeting rooms, focus rooms, breakout and reception, and read the headcount, NIA and checks. Desks stay inside the outline, off the façade by the setback, and clear of walls and columns. Nothing in the model is changed yet (placing the furniture and partitions in Revit is the next step). Needs the Microsoft Edge WebView2 Runtime, which Windows 10/11 already has.

## Developers

```
dotnet build -c Release     # whole solution, works on Windows or Linux
dotnet test                 # geometry tests, no Revit needed
```

- `src/SurveyToRevit.Core` – Revit-free geometry engine (mm).
- `src/SurveyToRevit.Launcher` – what Revit loads at start-up: the ribbon panel, Updater and Settings. Each tool click loads the newest installed engine (`src/SurveyToRevit.Revit` + Core) into its own load context from memory, so updates need no restart. Keep it small; its version only changes when it does.
- `src/SurveyToRevit.Revit` – the engine's Revit side (commands, dialogs, model builder).
- `src/Shared` – settings file shared by both.
- `tests/SurveyToRevit.Core.Tests` – synthetic-plan tests.
- `tools/SurveyLab` – `dotnet run --project tools/SurveyLab -- <folder of DWGs> --out <dir>` runs the engine without Revit and writes a report, SVG wall previews, the wall-material map per floor and a loose-ends check of the linework.

Third-party components: see [THIRD_PARTY.md](THIRD_PARTY.md).
