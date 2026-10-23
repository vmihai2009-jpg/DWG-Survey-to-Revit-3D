# Third-party components

| Component | Use | Licence | Shipped with add-in? |
|---|---|---|---|
| Nice3point.Revit.Api.RevitAPI / RevitAPIUI 2025.4.60 (NuGet) | Revit API reference assemblies for compiling | MIT (package); Autodesk API assemblies are supplied by Revit at run time | No |
| ACadSharp 3.8.0 (NuGet) | Reads DWG files directly (lines, polylines, text, blocks) | MIT | Yes (ACadSharp.dll) |
| Microsoft.Web.WebView2 1.0.2792.45 (NuGet) | Office Test-Fit window (shows the test-fit page inside Revit) | BSD-3-Clause style (Microsoft); the WebView2 Runtime itself is part of Windows 10/11 | Yes (Microsoft.Web.WebView2.Core.dll, .Wpf.dll; WebView2Loader.dll inside the engine) |
| three.js r128 | 3D view of the Office Test-Fit page | MIT | Yes (inside the engine, written out with the page) |
| xunit, Microsoft.NET.Test.Sdk, coverlet (NuGet) | Tests only | Apache-2.0 / MIT | No |

Reference projects reviewed (no code copied):

| Repository | Licence | Notes |
|---|---|---|
| OdedCas/mytools-pyrevit | none found (all rights reserved) | General wall-pairing approach used as inspiration only |
| HorizunGroup/horizun-revit-mcp | Apache-2.0 | Possible later dev/validation tool |
| hungpixi/revit-mcp | MIT | Alternative MCP, not used |
| ParkerCai/Trace-BIM | none found | Raster-plan workflow, not used |
| ian-quinn/Manicotti | BSD-3-Clause-Clear | Revit add-in, walls from CAD by pairing parallel lines; studied for the pairing and face-finding approach |
| ian-quinn/Gingerbread | BSD-3-Clause | Space boundaries for energy models; reviewed, not relevant |
| horizun-revit-mcp (CadInterpretationRules) | Apache-2.0 | Closest-pair-first pairing, letting one face continue along further partners, using fill evidence to say which strips are material; same idea used here with a room/wall map instead of hatching |

Tried and dropped: NetTopologySuite (BSD-3) to trace exact faces from the linework; the survey linework has too many small gaps for exact faces, so the grid method was kept and the dependency removed.

Development only: LibreDWG (GPL-3.0) was used once to inspect the survey files; it is not part of the product.

Development only: tifffile (BSD-3) and imagecodecs (BSD-3) made the small synthetic GeoTIFF test grids in `tests/SurveyToRevit.Core.Tests/Fixtures`; they are not part of the product. The GeoTIFF, ASCII grid and shapefile readers are written here from the published formats.

Data (downloaded by the user, not shipped): Environment Agency LIDAR Composite DSM/DTM 1m, Open Government Licence v3.0 ("© Environment Agency copyright and/or database right"). OS MasterMap Topography, if used, is under the user's own OS licence.
