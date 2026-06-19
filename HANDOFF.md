# SimCity 2013 modding project — HANDOFF

Last updated: 2026-06-19. This file lets a new chat session continue without prior context.
In the new session, select **`C:\Projects\simcitypak`** as the working folder.

---

## 1. Folder map

```
C:\Projects\simcitypak\
├── SimCityPak-main\SimCityPak-main\   <- THE APP (git repo root = folder with SimCityPak.sln)
│   ├── SimCityPak\                    main WPF app (GUI + new CLI in Cli\CliRunner.cs)
│   ├── SimCityPak.Packages\          library (newer DBPF reader; only SimCityModManager uses it)
│   ├── nQuant.Core\                  library (color quantization)
│   ├── SimCityModManager\            secondary tool — DOES NOT BUILD (missing Gibbed libs)
│   ├── References\                   bundled DLLs incl. References\Xna\x86\ (added by us)
│   ├── README.md  BUILD_NOTES.md  HANDOFF.md  rebuild.ps1   (all added by us)
│   └── .gitignore
└── simcity sounds\                   <- ASSET PROJECT (NOT in the app repo)
    ├── models-dlc0\                  780 source .rw4 models (extracted from SimCity)
    ├── models-converted\            563 .glb models (converted via Blender, see §5)
    ├── dlc0\                         24 source audio .rw4
    ├── _tools\                       vgmstream, nuget.exe, XNA extract, convert_rw4.py, build logs
    ├── SimCityPak-main\             (STRAY duplicate of the app — safe to delete)
    └── 91 × SCP_*.wav               original game audio (EA-codec; see §6)
```

**Desktop vs Projects:** the original project lived at
`C:\Users\tamas\Desktop\simcity sounds`. It was copied into `C:\Projects\simcitypak\simcity sounds`
(which is now MORE complete — it has the build tooling). The Desktop copy is redundant and
can be deleted after a final glance. (The converted PCM audio folder `converted\`, ~4 GB,
was already removed from both before the move — regenerate via §6 if needed.)

---

## 2. Environment / tooling (paths on this machine)

- **Blender:** `C:\Program Files\Blender Foundation\Blender 4.3\blender.exe`
- **MSBuild:** `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`
- **dotnet:** 10.0.301 (not used — app is .NET Framework)
- **git:** installed, user `Laszlo19` / laszlotamas108yahoo.com@gmail.com. **No `gh` CLI.**
- **nuget.exe:** `C:\Projects\simcitypak\simcity sounds\_tools\nuget.exe`
- **ffmpeg/ffprobe:** on PATH.

---

## 3. SimCityPak — how it builds (the app we're developing)

Upstream `main` (altinctrl/SimCityPak) did **not** build as-is. Fixes applied (full detail in
`BUILD_NOTES.md`):
1. `nuget restore SimCityPak.sln` (EntityFramework + System.Data.SQLite).
2. **XNA Framework 4.0** assemblies were missing. Extracted from the official
   `xnafx40_redist.msi` (via `msiexec /a`, no system install) into `References\Xna\x86\`.
   The two referenced DLLs (Framework + Graphics) are now **committed** to the repo, so a
   fresh clone builds without extra setup.
3. Retarget v4.0 → **v4.8** at build time (`/p:TargetFrameworkVersion=v4.8`,
   `/p:TargetFrameworkProfile=` to drop the dead Client Profile).
4. Added a fallback `OutputPath` to `nQuant.Core.csproj` and `SimCityPak.Packages.csproj`
   (they only defined it for AnyCPU; the x86 platform propagated in and failed).
5. Source typo: `MainWindow.xaml.cs` called `InsertSubFile` → should be `InsertIndex`.

**Build command (or run `rebuild.ps1`):**
```powershell
nuget restore SimCityPak.sln
msbuild SimCityPak\SimCityPak.csproj /p:Configuration=Release /p:Platform=x86 `
  /p:TargetFrameworkVersion=v4.8 /p:TargetFrameworkProfile=
```
Build the **project**, not the .sln — the solution build drags in SimCityModManager (broken)
and hits a WPF `_wpftmp` ProjectReference quirk. Output: `SimCityPak\bin\Release\SimCityPak.exe`.

---

## 4. The CLI feature (what we added) + how to extend it

- **Code:** `SimCityPak\Cli\CliRunner.cs` (registered in `SimCityPak.csproj` `<Compile>`).
- **Wiring:** `App.xaml` had `StartupUri` removed; `App.xaml.cs` `OnStartup` now routes to
  `CliRunner` when `IsCliCommand(args)`, else creates `MainWindow` manually.
- **Console:** `AttachConsole(ATTACH_PARENT_PROCESS)` so `Console.WriteLine` reaches the terminal.
- **Implemented:** `export-obj <input> <outputDir>` — input = `.package` | folder | single `.rw4`.
  Tested OK on extracted RW4 models (4/5 exported, 1 texture-only skipped).

**Export pipeline (reuse this for new formats):**
```
DatabasePackedFile.LoadFromFile(path)        // Gibbed.Spore.Package
  .Indices                                   // ObservableList<DatabaseIndex>
index.TypeId == 0x2f4e681b                   // RW4 MODEL type id (SimCity 2013)
index.GetIndexData(true)                     // decompressed bytes (RefPack-aware)
new RW4Model().Read(MemoryStream)            // SporeMaster.RenderWare4
model.Sections.Where(TypeCode == SectionTypeCodes.Mesh).obj as RW4Mesh
mesh.Export(path)                            // RW4MeshExtensions -> WaveFrontOBJConverter
```
Useful constants/locations:
- `SectionTypeCodes`: `Mesh = 0x20009`, `Texture = 0x20003`, `RW4Skeleton = 0x7000c`.
- Existing converters in `SimCityPak\RenderWare4\Exporters\`:
  `WaveFrontOBJConverter` (.obj), `ColladaConverter` + `AdvancedColladaConverter` (.dae).
  All implement `IConverter.Export(RW4Mesh, fileName)`.

**Roadmap (requested, not yet done):**
- `export-dae`  → swap `WaveFrontOBJConverter` for `AdvancedColladaConverter` (keeps more
  rig/material info). Same loop as `export-obj`.
- `export-png`  → texture/raster resources. Look at `RenderWare4\Texture.cs` and the GUI
  `ViewTexture` / DDS handling (`GraphicsHelpers\DDSLib.cs`). NOTE: textures touch XNA
  `GraphicsDevice` (Texture.cs:88,94) — may need a headless graphics device or a pure-managed
  DDS decode path. This is the one format with a real headless risk.
- `export-prop` → `.prop` property files → text/XML. See the property views/converters under
  `Views\valueConverters\Properties\` and the property reader in `SimCityPak.Packages`/PackageReader.
- General: add a `--filter <typeId>` option and a recursive folder mode.

---

## 5. Blender RW4 → glTF pipeline (already done, reproducible)

Converted all 780 `models-dlc0\*.rw4` → 563 `.glb` in `models-converted\` (217 skipped =
texture-only). Uses the **SporeModder Blender add-on**, patched for SimCity + Blender 4.3.

- Add-on installed at: `C:\Users\tamas\AppData\Roaming\Blender Foundation\Blender\4.3\scripts\addons\sporemodder\`
  **(patched in 5 places — these patches live in AppData, NOT in any project folder):**
  1. `rw4_animation_config.py` — guard module-level GPU shader (background mode).
  2. enable via `addon_utils.enable()` (Blender 4.3 operator bug).
  3. `rw4_enums.py create_rw_vertex_class` — de-duplicate vertex field names (SimCity dup 'tangent').
  4. `materials\static_model.py` — `Specular` socket renamed to `Specular IOR Level` in Blender 4.
  5. `rw4_importer.py` — guard/bound blend-weight skinning (SimCity meshes vary).
  6. `materials\rw_material.py` — tolerate null alpha_type.
- Batch script: `simcity sounds\_tools\convert_rw4.py`. Modes: `clean` (default) / `static` /
  `faithful` (animation handling — fixes cross-file animation contamination). Re-run command
  is in `models-converted\README_models.txt`.

---

## 6. Audio pipeline (already done, reproducible)

The 91 `SCP_*.wav` are **Audiokinetic Wwise Vorbis** (codec 0xFFFF) — not real WAVs, so normal
players reject them. Decoded with **vgmstream** (`_tools\vgmstream-cli.exe`) → standard PCM WAV.
Re-run: `vgmstream-cli.exe -o out.wav in.wav`. (The bulk-converted `converted\` folder, ~4 GB,
was deleted to save space; regenerate any time.)

---

## 7. Git / GitHub status

- Repo root: `C:\Projects\simcitypak\SimCityPak-main\SimCityPak-main\` (folder with `SimCityPak.sln`).
- Scope: **app source only** (assets excluded — too large/binary). `.gitignore` covers bin/obj,
  packages/, *.user, test output. The two referenced XNA DLLs ARE committed (force-added past
  the generic `x86/` ignore rule) so the repo builds from a clean clone.
- Decision: **public** repo. User will create the empty GitHub repo and provide the URL; then
  add remote + push. (No `gh`; first push may trigger Git Credential Manager browser login.)
- TODO when URL is known:
  `git remote add origin <url>; git branch -M main; git push -u origin main`

---

## 8. Open items / TODO

- [ ] Push to GitHub once the user supplies the repo URL.
- [ ] Implement `export-dae`, `export-png`, `export-prop` (see §4 roadmap).
- [ ] `SimCityModManager` doesn't build — needs `Gibbed.Spore` libraries (missing upstream).
      Optional; not needed for SimCityPak.
- [ ] Delete the redundant `C:\Users\tamas\Desktop\simcity sounds` and the stray
      `simcity sounds\SimCityPak-main\` duplicate after a final check.
- [x] XNA DLLs committed — fresh clone builds without extra setup.
