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
SimCityPak.exe export-obj   <input> <outputDir>   # RW4 models -> Wavefront .obj
SimCityPak.exe export-gltf  <input> <outputDir>   # RW4 models -> binary glTF 2.0 (.glb)
SimCityPak.exe export-audio <input> <outputDir>   # Wwise Vorbis audio -> playable PCM .wav
SimCityPak.exe help                               # usage
```

For every command, `<input>` may be:
- a **`.package`** file — exports every matching resource inside it,
- a **folder** — exports every matching file (`*.rw4` / `*.wav`) in it,
- a single **file** — exports just that one.

- **`export-obj`** writes each model's mesh sections as `<name>[_meshN].obj`.
  Texture-only resources (no mesh) are reported as `SKIP`.
- **`export-gltf`** writes each model as a self-contained binary **glTF 2.0** `.glb`
  (positions + normals + texture coordinates + indices) — the in-app equivalent of the
  geometry side of the SporeModder Blender add-on. Opens in Blender, Windows 3D Viewer,
  three.js, Unity, Unreal, etc. (Materials/textures/skeleton/animation are not written yet.)
- **`export-audio`** decodes SimCity's Audiokinetic **Wwise Vorbis** audio (RIFF with
  codec `0xFFFF`, which normal players reject) into standard PCM `.wav`. It uses
  **[vgmstream](https://github.com/vgmstream/vgmstream)**, bundled in `Tools\vgmstream\`
  next to the executable.

> Roadmap (not yet implemented): `export-dae` (Collada), embedded textures → `.dds`/`.png`,
> glTF materials/skeleton/animation, `export-prop` (property dumps). See `HANDOFF.md`.

Running `SimCityPak.exe` with **no arguments** (or any non-CLI argument) launches the
normal GUI exactly as before.

---

## Building from source

Requirements:
- Windows, Visual Studio 2022+ (MSBuild "Current"), .NET Framework **4.8** dev pack
- `nuget.exe` (for package restore)
- The **Microsoft XNA Framework 4.0** assemblies (`Microsoft.Xna.Framework.dll`,
  `Microsoft.Xna.Framework.Graphics.dll`) — **included** in `References\Xna\x86\`, so a fresh
  clone builds without extra setup. (See `BUILD_NOTES.md` for their origin.)

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
