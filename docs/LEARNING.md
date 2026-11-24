# How Survey to Revit learns (office-wide)

S2R learns from what people do to the models it builds, on every project, for every survey company. It never learns silently: what it learns is proposed, checked against a benchmark of real surveys, and adopted by a person. The engine only reads adopted lessons. Code: `src/SurveyToRevit.Core/Learning/Learning.cs`, `src/SurveyToRevit.Revit/LearningCapture.cs` and `tools/SurveyLab/LearnCli.cs`.

The shared folder is set by `LearningFolder` in `%AppData%\SurveyToRevit\settings.json`. It defaults to `I:\BG Technical Library\S2R\learning`, falls back to `Documents\SurveyToRevit\learning` when that can't be written, and an empty setting turns learning off.

```
learning/
  examples/<surveyor>.jsonl     one line per built wall and what happened to it
  proposed/                     what the examples support now (written by --propose)
  adopted/                      what the engine uses (written only by --adopt after the gate)
  history/                      earlier adopted files, kept on every adoption
```

## 1. Capture: every edit is an example

After each build, S2R saves `learning-snapshot.json` in the project's reports folder. It lists every wall it made, with the reading's evidence: layer, faces, thickness, confidence, floor and surveyor.

Before the next build of that project, it compares the model with the snapshot. Each wall becomes an example: **kept**, **deleted**, **moved** (by how much) or **retyped** (to what thickness). The examples go to `examples/<surveyor>.jsonl`.

- A rebuild where nothing was touched writes nothing.
- Each snapshot is learned from once.
- Nothing here can stop a build.

## 2. Surveyor profiles

S2R identifies the surveyor from the ISO 19650 originator in the file names (`000002-PCL-…` gives PCL), or else from the firm named on the sheets. A layer whose walls are deleted at least 90% of the time, over at least 20 walls and 2 projects of one surveyor, becomes a proposed rule `profile-<surveyor>.txt`, such as `KERB = Other`. It uses the layers.txt format.

Once adopted, it is read after the survey's own layers.txt, so a survey's own file always wins.

## 3. Tuning

Every tolerance in `Tolerances` (the reading's thresholds) can be set office-wide in `adopted/tuning.json`, for example `{"MaxPierMm": 5000}`.

To try values across the benchmark: `SurveyLab --learning DIR --bench bench.txt --sweep MaxPierMm=4000,5000,6000`.

## 4. Ranking for low-confidence walls

`walls-model.json` records, for each kind of low-confidence wall (surveyor, layer, thickness to 50 mm), how often people deleted it. A kind deleted at least 80% of the time, over at least 8 walls and 2 projects, is left out of later readings, and the reading notes how many and why.

It is never used for sure walls, and never for an unknown surveyor.

## 5. The gate

```
SurveyLab --learning DIR --propose --bench bench.txt            # report only
SurveyLab --learning DIR --propose --bench bench.txt --adopt    # adopt when nothing got worse
```

`bench.txt` lists survey folders, one per line; Mount Row and Swallow Place are the first. Each survey is read twice, with the adopted lessons and with the proposals on top.

The proposals are adopted only if no survey loses:
- floors;
- sure walls;
- windows;
- doors;
- stairs;

and none gains self-checks. The adopted files replaced are kept in `history/`.

Generic rules that no data could learn ("a terrace slab is the roof of the floor below") still go in the code, with a line in `docs/LESSONS.md`.
