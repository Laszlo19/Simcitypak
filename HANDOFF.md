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

    **Material-resolved textures (done — the real fix):** the heuristic above usually FAILS for
    SimCity buildings because a model's *internal* Texture section is a 2x2 placeholder; the real
    texture lives in a SEPARATE resource referenced by the model's `RW4Material` (section 0x2000b,
    parsed as `RW4Material` with up to 6 `MaterialTextureReference`s, each a `TextureInstanceId` +
    slot byte `Unknown1`). Those texture resources usually live in OTHER packages
    (`SimCity_Graphics` etc.) and come in two containers: **RW4-wrapped** (type id `0x2f4e681b`,
    same as models — a 0-mesh RW4 with a `Texture` section, often DXT) and **raster image**
    (`0x2f4e681c` — header of 6 big-endian uints then per-mip `[u32 blockSize][RGBA]`; only
    `pixFmt==21` raw RGBA is headless-decodable). Slots: `Unknown1==0x2d` is the shader def, then
    0,1,2,3,4 are texture slots (slot 0 is often a tiny facade strip, the 256x256 albedo/normal/
    spec are other slots). Implementation in `CliRunner.cs`:
      * `GltfConverter` now takes an optional `Func<RW4Mesh,byte[]> diffuseProvider`; if it returns
        a PNG that's used as base color, else it falls back to the internal-texture heuristic.
      * `BuildTextureLookup(indices)` indexes instanceId -> texture resource; `BuildTextureLookupForInput`
        scans the input .package PLUS every sibling .package (override: `--textures <pkg|dir>`).
      * `ResolveDiffusePng(mesh, lookup)` walks the model's `RW4Material` refs, decodes each via
        `DecodeTextureResourceRgba` (RW4 DXT via `GltfConverter.DecodeTextureToBitmap`; raw raster
        directly), skips tiny strips (<16px), and picks the largest non-normal-map candidate.
      * Wired into CLI `export-gltf` + `export-all` (sibling-package lookup) and the shared
        `ExportAllToFolder` (used by the GUI *File ▸ Export all* — lookup built from ALL loaded
        packages, so load the graphics packages for textures to resolve in the GUI). All headless
        (no GraphicsDevice), so it runs on the GUI's background export Task fine.
    Validated: DLC0 buildings + Central Train Station now embed real 256x256 diffuse PNGs (natural
    avg RGB, not the 2x2 placeholder), confirmed by parsing the GLB. NOTE the old authoritative
    chain Mesh -> `RWMeshMaterialAssignment`(0x2001a) -> `RW4TexMetadata`(0x2000b) is a Spore-ism
    and is NOT how SimCity links textures (it's `RW4Material` + external instance ids); 0x2001a
    stays disabled.
    **Normal + specular maps (done):** ground-truthed the material slots by dumping each
    resolved map to PNG and looking: slot u1=1 is a false-colour zone/material mask (the best
    available "colour" map — used as base color), slot u1=2 is the NORMAL map, slot u1=3 a
    secondary mask. SimCity normal maps are stored "pink" (flat ~255,128,128 = up component in
    RED), so they need an R<->B swizzle to the glTF convention (flat 128,128,255); the SporeModder
    addon's 3-texture static model confirms slot1=normal with **specular packed in the normal's
    alpha**. Implementation: `GltfConverter.MaterialTextures{BaseColor,Normal,Specular}` returned
    by the resolver; `GltfConverter` now emits `normalTexture` and `KHR_materials_specular`
    (specularColorTexture). `CliRunner.ResolveTextures` detects the pink normal
    (`IsSimCityNormalMap`: R>180, G/B near 128), unswizzles it (`UnswizzleNormal`), and lifts the
    alpha into a greyscale spec map (`SpecularFromAlpha`, skipped if alpha is constant). Verified
    on DLC0 + Central Train Station: glb has 3 images, normal avg ~(128,128,252) (correct blue
    flat) confirmed by extracting+viewing the PNG, KHR_materials_specular declared in extensionsUsed.
    TODO on the .glb: DXT-compressed raster images (pixFmt != 21), .obj/.mtl textures.
    export-obj is untextured (no .mtl emitted).

    **Skeleton + skin (done).** `RW4Model.Read` now parses `RW4Skeleton` (0x7000c) — joint
    `HierarchyInfo` (names/parents) + bind matrices — and `Anim` (0x70001) was rewritten for
    SimCity's keyframe formats (0x101 LocRot etc.; see commit). `GltfConverter.ExtractSkin` builds
    a glTF skin: bind position per joint = `R·(−t)` from the inverse-bind (Matrices4x4, the 3x3
    rotation + translation, per the SporeModder importer); inverse-bind matrices are pure
    `translate(−bindPos)` and joint nodes are translation-only, so the **bind pose is undeformed**
    while a scene-root node carries the Z-up→Y-up −90° rotation (which then applies uniformly to
    the skinned mesh). Per-vertex `JOINTS_0`/`WEIGHTS_0` come from the mesh's `BLENDINDICES`/
    `BLENDWEIGHT` components (UBYTE4/SHORT4/FLOAT4, normalized); meshes with a skeleton but no blend
    data bind fully to joint 0. Verified on DLC0: ec3eade0 (17 joints, real per-vertex weights) and
    6b6d124f (3 joints, bind-to-root) produce valid glTF skins (joints in range, weights normalized,
    bind pose provably undeformed). Static models (no skeleton) are unchanged.
    **Animation — NOT yet exported (next step).** The keyframe data now parses (Anim.channels =
    per-joint TRS+time), but emitting glTF animation samplers/channels (and resolving local-vs-
    absolute pose space + the bind-rotation we currently fold into the root) is the remaining work.

    **Base color: greyscale facade from palette luminance (done — replaces the neon look).**
    SimCity buildings have **no baked albedo**. The material slots are: u1=0 a small per-model
    **palette** strip (float32 RGBA, D3DFMT_A32B32G32R32F = textureType **116**, e.g. 69x4), u1=1
    a **region/zone mask** (the false-colour atlas), u1=2 normal, u1=3 a secondary mask. The game
    composites the facade colour in the GlassBox deferred shader. We ground-truthed the palette by
    dumping its raw floats (`dump-slots` diagnostic, removed): it is a **4-row HDR material table**,
    NOT an RGB colour LUT — row 0 is an albedo *luminance* (R≈G varying dark→bright, B pinned ~0.13),
    rows 1-3 are HDR spec/emissive/param data (values up to 50, negatives). Tested every row +
    2-D + tint hypotheses against the in-game look (The Academy = white/teal/green): **no
    combination yields the real colour** — it's computed shader-side and isn't recoverable from the
    textures. So the exporter now maps the region atlas's RED channel through palette **row-0
    luminance** (`DecodePaletteLuminance` + `CompositeFacadeLuminance`, linear) to produce a clean
    **greyscale** facade (light walls, dark windows, real surface detail) instead of the neon atlas.
    Combined with the normal + specular maps this reads as a proper architectural model; The Academy
    (mostly white) approximates well. Falls back to the raw map when no palette is present.
    (History: an earlier attempt composited row-0 *RGB* as if it were colour — that produced flat
    yellow/wrong colours and was reverted; the B≈0.13 pin was the giveaway it isn't colour.)
    Also note: many buildings legitimately **share** the same region + normal atlas (e.g. 18 EP1
    models share 0x1188b12e / 0x a3791e5c) — correct (shared atlases indexed by per-model UVs +
    per-model palette), NOT a bug. **TODO if the real colour is ever wanted:** decode the GlassBox
    facade shader (mask channels -> palette rows blend + HDR tonemap).

    **Robustness fixes (done):** meshes without FLOAT4 texcoords used to throw in
    `Vertex.TextureCoordinates` (`.First()`), failing ~34 models in a full EP1 export; `Vertex`
    now exposes `HasTextureCoordinates` and `Normal` is null-safe, and `GltfConverter` exports
    geometry-only (no UVs/material) when texcoords are absent — EP1 failures dropped 34 -> 6. The
    remaining 6 are a pre-existing parser NRE in `RW4Material.Read` (undecoded `VertexFormat`
    section); the whole model fails to load. Localized-name coverage: `BuildLocaleNameMap` now
    propagates a catalog prop's name to the RW4 models referenced by props sharing its instance or
    sitting at its Model-Details target (asset-group aware, via `PropRec`/`CollectModelRefs`), and
    the GUI summary now reports "N of M models got a localized name" instead of the misleading map
    size. NOTE coverage is inherently limited (~364/1025 on EP1): most exported meshes are LOD
    variants / sub-models / shared texture resources not tied to a named catalog entry.
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
    **GUI (done):** the combine core is now `public CliRunner.ExportCombinedPropsToFolder(
    IEnumerable<DatabaseIndex>, outDir, localeFile, json)` (CLI `RunExportPropCombined` is a thin
    wrapper that loads the package and prints the returned summary). The GUI menu *File ▸ Export
    combined properties to folder…* (`MainWindow.mnuExportCombinedProps_Click`) asks JSON/TXT,
    picks a folder, and runs it on a Task with a wait cursor — same pattern as *Export all*.
    **Sibling/variant Model Details hashes — INVESTIGATED, deliberately NOT added.** Question:
    are there other property hashes that, like Model Details (`0x0975695f`), should link a
    prop to its asset so combine assembles the *whole* asset (model + gameplay + lights + …)?
    Empirically analysed `SimCity_DLC0`, `SimCity_Game` (base) and `SimCityDataEP1` with a
    throwaway `analyze-prop-links` diagnostic (now removed). Findings:
      * The descriptor DB has exactly ONE property named "Model Details" — no named sibling.
      * Prop group containers encode facets: `…f0c000`/`40e1c000` = model (LODs, bounding box),
        `…f0c900`/`09878a01` = catalog/menu, `…f0e800`/`40e0c000` = gameplay (kProp power/water/
        jobs), `…f0c600` = lights/vehicles, `…f0c500` = actions, `…efc100`/`40e02d00` =
        empty/effect placeholders (often SHARED across assets).
      * `0x0975695f` (catalog→model) is the only CLEAN asset-identity link. Combined with the
        existing same-instance union it correctly groups model+gameplay+catalog when model &
        gameplay share an instance (the Oppie/train-station layout).
      * The remaining cross-prop references are NOT safe to union on:
        - `0x0d5a28b8` (model→gameplay/actions, very common: x1302 in base game) — following it
          OVER-MERGES. A curated rule (Model Details + this link, gameplay facet only) looked
          clean on DLC0 (largest component 8 props) but produced a **320-prop blob** on
          `SimCity_Game` fusing 139 model + 110 gameplay + 69 catalog props of one building
          family. Full transitive following gave 90-prop cross-family blobs even on DLC0.
        - `Parent Menu` (`0x0db9fc63`) and Category Palette (`0x09a4ba03` etc.) point to SHARED
          menu/category props referenced by many assets → would collapse everything.
        - `Menu Item Icon` (`0x0977aa8f`), `Preview Image`, tool-unlock images → icon/image
          refs, not asset structure.
      * Conclusion: SimCity's prop graph genuinely interconnects whole building families, so
        reference-following cannot define an asset boundary. `0x0975695f` + same-instance is the
        correct, safe extent. Do NOT add more hashes. (Consequence: for base-game / EP1 assets
        whose gameplay prop is a separate instance, the gameplay facet stays a separate file —
        this is intentional, not a bug.)

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
