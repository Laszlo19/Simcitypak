# SimCityPak — build notes

Built successfully on Windows with Visual Studio 18 (MSBuild Current) + .NET Framework 4.8.
Output: `SimCityPak\bin\Release\SimCityPak.exe` (x86, Release). It launches and is self-contained.

## What had to be fixed to build (the repo `main` did not build as-is here)

1. **NuGet restore** — `nuget restore SimCityPak.sln` (EntityFramework + System.Data.SQLite).

2. **Missing XNA Framework 4.0** — `SimCityPak.csproj` references
   `References\Xna\x86\Microsoft.Xna.Framework[.Graphics].dll`, but the `References\Xna`
   folder is absent from the repo. Fixed by extracting the official **Microsoft XNA
   Framework 4.0 Redistributable** (`xnafx40_redist.msi`) and copying its DLLs into
   `References\Xna\x86\`. (Done via `msiexec /a` administrative extract — no system install.)

3. **.NET 4.0 retarget** — only the v4.8 targeting pack is installed, so build with
   `/p:TargetFrameworkVersion=v4.8`. Also `/p:TargetFrameworkProfile=` (empty) to drop the
   discontinued Client Profile. 4.x is in-place/back-compatible, so behavior is unchanged.

4. **OutputPath fallback** in `nQuant.Core.csproj` and `SimCityPak.Packages.csproj` — they
   only defined `OutputPath` for `AnyCPU`, so the `x86` platform (propagated from SimCityPak
   and its WPF `_wpftmp` markup-compile pass) failed with "OutputPath is not set". Added a
   `Condition=" '$(OutputPath)' == '' "` fallback PropertyGroup to each.

5. **Source typo** — `SimCityPak\MainWindow.xaml.cs:558` called `packageFile.InsertSubFile(...)`,
   but the method is named `InsertIndex` (here and upstream). Changed `InsertSubFile` -> `InsertIndex`.
   (This was a local modification in this checkout; upstream `main` uses `InsertIndex`.)

## How it's built

Build the **SimCityPak project directly** (not the .sln). Building the project avoids:
  - **SimCityModManager** — a separate, optional tool that needs the `Gibbed.*` libraries
    (also missing from the repo). Not required for the main app.
  - A solution-build quirk where the WPF `_wpftmp` temp project fails to resolve the
    `nQuant.Core` ProjectReference.

See `rebuild.ps1` in this folder for the exact command.

## Still not built (optional)

- **SimCityModManager** — needs the missing `Gibbed.Spore` libraries. Left unbuilt; not
  needed to run SimCityPak. Can be revisited if you want that tool too.
