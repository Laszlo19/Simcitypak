# SimCityPak (CLI-enabled fork)

A package/asset editor for **SimCity (2013)**. This is a fork of
[altinctrl/SimCityPak](https://github.com/altinctrl/SimCityPak) that:

- **builds on modern toolchains** (Visual Studio 2022+/MSBuild Current, .NET Framework 4.8), and
- adds a **headless command-line interface** for batch asset export.

The original GUI is unchanged in behaviour.

---

## Features added in this fork

### Command-line interface
Run the same `SimCityPak.exe` headlessly to mass-export assets — no GUI.

```
SimCityPak.exe export-obj <input> <outputDir>
```

`<input>` may be:
- a **`.package`** file — exports every RW4 model inside it,
- a **folder** — exports every `*.rw4` file in it,
- a single **`.rw4`** file — exports just that model.

Each model's mesh sections are written as `<name>[_meshN].obj` (Wavefront OBJ).
Texture-only resources (no mesh) are reported as `SKIP`.

```
SimCityPak.exe help        # usage
```

> Roadmap (not yet implemented): `export-dae` (Collada), `export-png` (textures),
> `export-prop` (property dumps). See `HANDOFF.md`.

Running `SimCityPak.exe` with **no arguments** (or any non-CLI argument) launches the
normal GUI exactly as before.

---

## Building from source

Requirements:
- Windows, Visual Studio 2022+ (MSBuild "Current"), .NET Framework **4.8** dev pack
- `nuget.exe` (for package restore)
- The **Microsoft XNA Framework 4.0** assemblies in `References\Xna\x86\`
  (`Microsoft.Xna.Framework.dll`, `Microsoft.Xna.Framework.Graphics.dll`) — see
  `BUILD_NOTES.md` for how to obtain them; they are **not** committed to the repo.

Then either run `rebuild.ps1`, or:

```powershell
nuget restore SimCityPak.sln
msbuild SimCityPak\SimCityPak.csproj `
  /p:Configuration=Release /p:Platform=x86 `
  /p:TargetFrameworkVersion=v4.8 /p:TargetFrameworkProfile=
```

Output: `SimCityPak\bin\Release\SimCityPak.exe`.

See **`BUILD_NOTES.md`** for the full list of fixes required to build (XNA, retarget,
OutputPath, a source typo) and **`HANDOFF.md`** for project history and the roadmap.

---

## Status

- ✅ Builds and runs (GUI + CLI).
- ✅ CLI `export-obj` tested against extracted SimCity RW4 models.
- ⚠️ `SimCityModManager` (a separate tool in the solution) does **not** build — it
  needs the `Gibbed.Spore` libraries, which are missing from the upstream repo. Not
  required for SimCityPak itself.

## Credits / license

Forked from **altinctrl/SimCityPak** (originally on CodePlex). Original authors retain
their rights; see upstream for license terms. This fork adds the CLI and build fixes.
