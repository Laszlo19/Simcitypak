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
    ├── _tools\                       nuget.exe, XNA installer+extract, SporeModder addon + convert_rw4.py
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
- **SimCity game install:** `C:\Games\SimCity\` (123 `.package` files under `SimCityData\` and
  `SimCityUserData\`). Real test data for the CLI — e.g.
  `SimCityData\Oppie_OFFLINE_CentralTrainStation.package` has RW4 models, textures and .prop
  files. The big base packages: `SimCity_App.package` (302 MB),
  `SimCityDataEP1.package` (391 MB), audio in `SimCity_Audio_*.package`.

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
- **Implemented commands** (input = `.package` | folder | single file for all):
  - `export-obj <input> <outputDir>` — RW4 models → Wavefront .obj. Tested 4/5 (1 texture-only skip).
  - `export-gltf <input> <outputDir>` — RW4 models → binary glTF 2.0 .glb (geometry: positions,
    normals, UVs, indices) PLUS the model's diffuse texture as the material base color.
    Pure-C# GLB writer in `RenderWare4\Exporters\GltfConverter.cs` (Newtonsoft.Json for the JSON
    chunk). Textures: of the model's DXT1/DXT5 Texture sections it picks the **largest that isn't
    a normal map** (`LooksLikeNormalMap` = blue-dominant heuristic), decodes with a built-in DXT
    decoder (`DecodeDxt1`/`DecodeDxt5`) to RGBA, encodes PNG via System.Drawing, embeds it (glTF
    allows only PNG/JPEG, not DDS). Validated: imports in Blender 4.3 textured; selected texture
    avgRGB is a natural diffuse colour, not (128,128,255). Two model commands share
    `RunExportModels(args, ext, exporter)` in CliRunner.

    **Per-mesh textures investigation (done):** all 563 models are SINGLE-mesh (verified by
    exporting all and finding zero `_mesh1` files), so per-mesh == per-model here. The
    authoritative chain Mesh -> `RWMeshMaterialAssignment`(0x2001a) -> `RW4TexMetadata`(0x2000b)
    -> Texture EXISTS in the format but is NOT usable: `RW4Model.Read` leaves 0x2001a parsing
    commented out, and 0x2000b is parsed as `RW4Material` while the assignment wants
    `RW4TexMetadata` — `RW4Section.GetObject` caches one obj per section so the cast throws
    (that's why it's disabled). Enabling it needs real parser surgery; hence the heuristic.
    TODO on the .glb: raw-bitmap (type 21) textures, normal/spec maps, skeleton, animation.
  - `export-texture <input> <outputDir>` — RW4 Texture sections (type 0x20003) → .dds files.
    Added `Texture.SaveDds(path)` in `RenderWare4\Texture.cs` — writes the DDS magic+header
    (same header ToImage() builds) + the raw block-compressed blob, WITHOUT the GraphicsDevice
    decode, so it's headless. Tested: DXT1/DXT5 .dds, ffmpeg decodes to valid RGBA PNG.
    Raw-bitmap textures (textureType 21) are skipped (NotSupportedException → SKIP), not failed.
    TODO: support type 21, and an optional direct-to-PNG mode.
  - `export-prop <input> <outputDir>` — property lists (type 0x00b1b104) → readable .txt.
    Uses the app's own `Gibbed.Spore.Properties.PropertyFile` (`.Read(stream)` or the
    `PropertyFile(index)` ctor) and each `Property.DisplayValue`; `DumpProp` writes
    `0x<hash8>  <Type>  = <value>` sorted by hash. Tested on REAL game data
    (`C:\Games\SimCity\SimCityData\Oppie_OFFLINE_CentralTrainStation.package`): 3 prop files,
    94 props each, correct Float/Bool/Key/Vector2/BoundingBox/Transform/array values.
    Polish TODO: `DisplayValue` uses CurrentCulture so decimals are locale-formatted (commas on
    a HU machine), which makes Transform/array values ambiguous; an invariant-culture dump mode
    would be cleaner.
    **Property names + JSON (done):** `DumpProp` resolves property hashes to readable names via
    `TGIRegistry.Instance.Properties.Cache` (`PropName`) — e.g. `Menu Item Title`, `Menu Item
    Description`, `LOD1` — falling back to hex for undocumented ones; `--json` writes `.json`.
    IMPORTANT FIX that enabled this: `TGITable.LoadCache` opened `database_main.s3db` by a
    *relative* path, so registries were empty unless the working dir was the exe folder (broke the
    CLI from elsewhere). Added `ResolveDbPath` (falls back to `AppDomain.BaseDirectory`) — also
    fixes the GUI launched from another directory. Verified end-to-end on DLC0 + en-us locale:
    "Airship Hangar.json" with Menu Item Title/Description etc. resolved.
    **GUI + invariant culture (done):** the dump core is now `public CliRunner.DumpPropertyFile`
    which wraps `DumpProp` in `CultureInfo.InvariantCulture` (Transform/vector values use '.'
    decimals, not locale commas). The GUI calls it from a new context-menu item
    `mnuExportProperties_Click` (MainWindow) -> SaveFileDialog (TXT/JSON) on any prop resource.
    Verified: Transform now `0.9993909,0.03489691,...` instead of `0,9993909,0,03489691`.
    **Log-driven fixes (done):** the user's `Documents\SimCityPak\*.log` showed a
    NullReferenceException at `GltfConverter.Export` line 36 for blend-shape meshes (null
    `mesh.vertices`). Guarded both in `Export` and in the `ExportRw4Bytes` mesh filter (skip
    meshes with null vertices/triangles). **Orientation:** added a `-90° about X` node rotation
    (`[-0.70710678,0,0,0.70710678]`) so RW4 Z-up models stand upright in glTF Y-up — confirmed
    correct by the prop bounding box (`MinZ=0..MaxZ` = height from base). **Texture formats:**
    `export-texture` / `export-all` now take `--format png|jpg|tga|dds` (default png) — DXT decoded
    via `GltfConverter.DecodeTextureToBitmap` + System.Drawing (png/jpg) or a small TGA writer.
    **Gameplay properties** (Power Consumer Rate/Amount/Capacity, kPropWork_MinimumWorkers...,
    kPropWaterConsumer_*) already resolve via the descriptor DB — they live in the building's
    GAMEPLAY prop, not the model prop.

  - **`export-prop --combine` (done):** when exporting from a `.package`, merge each asset's
    separate prop resources into ONE file. An asset's props relate like this (verified on
    `Oppie_OFFLINE_CentralTrainStation.package`): the **model** prop (`G 40e1c000`) and the
    **gameplay** prop (`G 40e0c000`, has Power Consumer/Workers) SHARE an InstanceId
    (`0x998ef8f7`); a separate **catalog/menu** prop (`G 09878a01`, has Menu Item Title/Desc)
    points to that instance via the **"Model Details (PROP)" property, hash `0x0975695f`** (a
    KeyProperty whose InstanceId = the asset instance).
    Implementation in `Cli\CliRunner.cs`:
      * const `MODEL_DETAILS_HASH = 0x0975695f`.
      * `RunExportProp` dispatches to `RunExportPropCombined(...)` when args contain `--combine`
        AND input is a `.package`.
      * `RunExportPropCombined`: loads all prop resources, builds a **union-find** — unions props
        that share an InstanceId, and unions a prop having Model Details with the prop(s) of the
        instance it references. Each connected component = one asset. Names it from the locale
        name map (any member instance that resolves), else the asset instance hash; `UniqueName`
        dedupes. Writes one file per asset via `DumpCombined`.
      * `DumpCombined(members, outPath, name, json)`: writes all member props into one file
        (one `== T:.. G:.. I:.. ==` section each for txt; a `resources[]` array for json),
        reusing `PropName`/`PropTypeName`/`PropValue` + invariant culture.
      * help text updated.
    Tested end-to-end: (1) `Oppie_OFFLINE_CentralTrainStation.package` → ONE file merging the
    model + gameplay + catalog props (3 resources, 94+137+17 props), instance-share + Model
    Details union both confirmed; (2) `SimCity_DLC0.package` + en-us locale → 554 assets from
    643 props, 0 failures, localized names ("Maxis Manor", "Eiffel Tower", "Baccarat Room", …),
    including 2-prop assets joined across *different* instances via the Model Details reference.
    Possible enhancement: also wire a GUI "Export combined…" action; and Model Details may have
    sibling/variant hashes for some asset types (only `0x0975695f` handled so far).

- **Localized export names (done):** for `.package` input, `export-obj/gltf/texture/prop` name
  files by **localized asset name** instead of TGI hashes. Add `--locale <Locale\xx-xx\Data.package>`
  or set it in the GUI Settings (`Properties.Settings.Default.LocaleFile`). Implementation in
  CliRunner: `BuildLocaleNameMap` loads the locale via `SimCityPak.LocaleRegistry.Create()` (reads
  `Settings.LocaleFile`), scans every prop file for a name property (hashes 0x09FB78CB / 0x0A09F5FA
  / 0x09B711C3 / 0x0E28B5BC / 0x0E28B5D5 → ArrayProperty[0] TextProperty → `GetLocalizedString`),
  and maps that name to the prop's own instance AND to every model (type 0x2f4e681b) it references
  via Key properties. `ResolveBaseName` sanitizes + de-duplicates (appends instance id on clash).
  Validated on `SimCity_DLC0.package` + `Locale\en-us\Data.package`: 507 names, models/props out as
  "Maxis Manor", "Baccarat Room", "Airship Hangar", etc. Locale at
  `C:\Games\SimCity\SimCityData\Locale\en-us\Data.package`. Loose-file / folder input has no name
  source, so it keeps hashes.
  **GUI too:** `MainWindow.mnuInstanceIds_Click` ("Load instance names from locale") now also maps
  models referenced by props (new `CollectModelNames`), so `GetExportFileName` (already
  `SCP_<InstanceName>_<TGI>`) localizes the model/texture "Export Instance". The OBJ/DDS/BMP/PNG
  sub-view exporters pre-fill their SaveFileDialog from `MainWindow.SelectedExportName` (set on
  selection). Both CLI + GUI changes live on branch `feature/locale-export-names`.
  - `export-audio <input> <outputDir>` — Wwise Vorbis (type id `0x0d9e5710`) → PCM .wav. Tested OK
    (output verified `pcm_s16le`). Drives **bundled vgmstream** at `SimCityPak\Tools\vgmstream\`
    (committed; copied next to the exe via a `<Content>` item with CopyToOutputDirectory). The
    old loose vgmstream that lived in `simcity sounds\_tools\` has since been removed — the
    app's bundled copy is now authoritative.
  - `export-all <input> <outputDir> [--locale]` — every model → `.glb` AND its textures → `.dds`
    into one folder (localized names). Core is `public CliRunner.ExportAllToFolder(IEnumerable
    <DatabaseIndex>, outDir, localeFile)`, also called by the **GUI** menu *File ▸ Export all
    models + textures to folder…* (`MainWindow.mnuExportAll_Click`, runs on a Task with a wait
    cursor). `BuildLocaleNameMap` now takes `IEnumerable<DatabaseIndex>` so both share it.

**Logging (added — debug the "app crashes sometimes" complaint):** `SimCityPak\Logging\Logger.cs`
writes to `%USERPROFILE%\Documents\SimCityPak\simcitypak-<date>.log` (thread-safe, never throws).
`App.OnStartup` wires `AppDomain.UnhandledException` + `DispatcherUnhandledException` (the latter
logs and sets `Handled=true` to keep the GUI alive). Operation logs added at package load, resource
selection, the previously-silent `catch{}` blocks in ViewRW4/ViewMesh/ViewTexture, and all CLI
commands. NOTE: the 64-bit request was dropped in favour of logging — XNA 4.0 is x86-only (PE32)
and is the math+graphics library across ~12 files (357 Vector/Matrix uses + 1770-line DDSLib), so
going x64 needs a full XNA removal (System.Numerics/MonoGame + managed DDS decode) — a separate
large effort.

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
players reject them. Decoded with **vgmstream**, which is now **integrated into the app** — re-run
via `SimCityPak.exe export-audio <input> <outputDir>` (see §4). The standalone vgmstream that used
to sit in `_tools\` was removed in cleanup; the app's bundled `Tools\vgmstream\` copy is the one to
use. (The bulk-converted `converted\` folder, ~4 GB, was deleted to save space; regenerate any time.)

---

## 6b. Installer (MSI) — done

`installer\SimCityPak.wxs` + `installer\build-msi.ps1` build a Windows Installer that drops
the whole Release output into `Program Files\SimCityPak` (Start-Menu shortcut + ARP entry).
Uses **WiX v5** (`dotnet tool install --global wix --version 5.0.2`) — NOT v6/v7, which demand
the paid OSMF EULA. The `<Files Include="$(PublishDir)\**">` element harvests the folder.
Built/validated: 9.9 MB MSI, 48 files via `msiexec /a` extract (exe, XNA, SQLite, vgmstream
tools, .s3db all present). A real `/i` install needs admin (UAC) — fine on a normal double-click;
it failed here only because the sandbox is non-elevated (Error 1925). `.msi`/`.wixpdb` are
gitignored; ship the MSI on a GitHub Release.

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
