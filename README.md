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
SimCityPak.exe export-obj     <input> <outputDir>  # RW4 models   -> Wavefront .obj
SimCityPak.exe export-gltf    <input> <outputDir>  # RW4 models   -> binary glTF 2.0 (.glb)
SimCityPak.exe export-texture <input> <outputDir>  # RW4 textures -> .dds images
SimCityPak.exe export-prop    <input> <outputDir>  # .prop property lists -> readable .txt
SimCityPak.exe export-audio   <input> <outputDir>  # Wwise Vorbis audio -> playable PCM .wav
SimCityPak.exe export-all     <input> <outputDir>  # every model -> .glb AND every texture -> .dds
SimCityPak.exe help                                # usage
```

`export-all` writes **all models as `.glb` and all their textures as `.dds`** into one folder
(localized names with `--locale`). The GUI has the same action under **File ▸ Export all models
+ textures to folder…** for the currently loaded package(s).

### Logging
The app writes a log to **`%USERPROFILE%\Documents\SimCityPak\simcitypak-<date>.log`** — startup
info, the resource being viewed, package loads, exports, and (most importantly) **full stack
traces for any crash or swallowed error**. Unhandled UI-thread exceptions are logged and the app
tries to stay alive instead of crashing. Attach this log when reporting a bug.

For every command, `<input>` may be:
- a **`.package`** file — exports every matching resource inside it,
- a **folder** — exports every matching file (`*.rw4` / `*.wav`) in it,
- a single **file** — exports just that one.

**Localized names:** when exporting from a `.package`, add
`--locale "<SimCity>\SimCityData\Locale\xx-xx\Data.package"` (or set the locale file in the
GUI Settings) and exported models / textures / props are named by their **localized asset
name** (e.g. `Maxis Manor.glb`, `Baccarat Room.obj`) instead of TGI hashes. Names are resolved
through the package's property files; resources without a resolvable name keep the hash name.

- **`export-obj`** writes each model's mesh sections as `<name>[_meshN].obj`.
  Texture-only resources (no mesh) are reported as `SKIP`.
- **`export-gltf`** writes each model as a self-contained binary **glTF 2.0** `.glb`
  (positions + normals + texture coordinates + indices) **with the model's diffuse texture**
  baked in: it picks the largest DXT1/DXT5 texture that isn't a normal map, decodes it to PNG,
  and applies it as the material base color — so models show textured in Blender, Windows 3D
  Viewer, three.js, Unity, Unreal, etc. (Models whose only textures are raw bitmaps (type 21)
  export untextured. Normal/spec maps, skeleton and animation are not written yet.)
- **`export-texture`** writes each model's embedded textures as `.dds` images
  (`<name>[_texN].dds`). Block-compressed (DXT1/DXT5) textures are supported; raw-bitmap
  textures (`textureType 21`) are skipped. Open in any DDS viewer, or convert with
  `ffmpeg -i tex.dds tex.png`.
- **`export-prop`** dumps **property-list (`.prop`) resources** (type `0x00b1b104`) — one
  building per file, named by its localized name (with `--locale`). **Property names are
  resolved** where SimCityPak knows them (e.g. `Menu Item Title`, `Menu Item Description`,
  `LOD1`, `Is Module`), falling back to `0x<hash>` for undocumented ones — so a building's
  display name, description, menu placement, model/LOD refs and tuning come out readable.
  Output is `.txt` by default or **`.json`** with `--json`. Handles every property type (Float,
  Bool, Key/TGI, Vector2/3/4, Color, BoundingBox, Transform, String8/16, arrays, …). Numbers use
  invariant culture (always `.` decimals) so Transform/vector values aren't ambiguous on
  comma-decimal locales. The **GUI** has the same export under right-click ▸ **Export Properties
  (TXT/JSON)…** on any property resource.
- **`export-audio`** decodes SimCity's Audiokinetic **Wwise Vorbis** audio (RIFF with
  codec `0xFFFF`, which normal players reject) into standard PCM `.wav`. It uses
  **[vgmstream](https://github.com/vgmstream/vgmstream)**, bundled in `Tools\vgmstream\`
  next to the executable.

> Roadmap (not yet implemented): `export-dae` (Collada), direct `.png` texture output,
> raw-bitmap (type 21) textures, glTF materials/skeleton/animation, `export-prop`
> (property dumps). See `HANDOFF.md`.

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

## Installer (MSI)

A Windows Installer package can be built from the Release output with the
**[WiX Toolset](https://wixtoolset.org/) v5** (the last free version — v6+ requires the
paid OSMF EULA):

```powershell
dotnet tool install --global wix --version 5.0.2
installer\build-msi.ps1          # or  build-msi.ps1 -Rebuild  to compile first
```

This produces `installer\SimCityPak.msi` (~10 MB) — installs the app and all its
dependencies (XNA, SQLite, the bundled vgmstream tools, the `.s3db` databases) into
`Program Files\SimCityPak`, with a Start-Menu shortcut and an Add/Remove Programs entry.
The authoring is in `installer\SimCityPak.wxs`. (Double-click the MSI to install; it
elevates via UAC.)

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
