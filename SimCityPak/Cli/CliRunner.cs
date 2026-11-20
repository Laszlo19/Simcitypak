using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Gibbed.Spore.Package;
using Gibbed.Spore.Properties;
using Newtonsoft.Json;
using SporeMaster.RenderWare4;

namespace SimCityPak.Cli
{
    /// <summary>
    /// Headless command-line interface for SimCityPak.
    ///
    /// Currently supports mass-exporting RW4 3D models to Wavefront .OBJ.
    /// The GUI is unaffected: App.OnStartup routes to this class only when the
    /// first argument is a recognised CLI command, otherwise it shows the window.
    ///
    /// See README.md / HANDOFF.md for the roadmap (DAE, PNG textures, .prop dumps).
    /// </summary>
    public static class CliRunner
    {
        // SimCity 2013 type id for an RW4 model resource (the "0x2f4e681b" in
        // the extracted "SCP_0x2f4e681b-..." file names).
        private const uint RW4_MODEL_TYPE_ID = 0x2f4e681b;

        // SimCity 2013 type id for an audio resource (Audiokinetic Wwise Vorbis,
        // the "0x0d9e5710" in the extracted "SCP_0x0d9e5710-..." file names).
        private const uint AUDIO_TYPE_ID = 0x0d9e5710;

        // SimCity 2013 type id for a property list (.prop) resource.
        private const uint PROP_TYPE_ID = 0x00b1b104;

        // "Model Details (PROP)" property: a catalog prop's Key reference to the
        // model/gameplay asset instance it belongs to. Used by --combine.
        private const uint MODEL_DETAILS_HASH = 0x0975695f;

        // SimCity 2013 type id for a shader-source / ArgScript fragment container.
        // These live in SimCity_App.package (32 of them, ~37 MB) and hold the
        // GlassBox HLSL shader source (building4 / genericLot / decal families):
        // [fragment name][binary metadata][HLSL text][uniform declarations].
        private const uint SHADER_CONTAINER_TYPE_ID = 0x0469a3f7;

        // Which facade palette ROW (buildingVariation) to bake when reproducing the
        // building4 facade color. In-game this is per-building-placement instance data
        // (the engine picks it from wealth/variation); a single-model export has to pick
        // one of the ~8 rows. Overridable with --variation N on export-gltf / export-all.
        private static int _facadeVariation = 0;

        // EXPERIMENTAL and off by default: the building4 palette-colour bake. It is not yet
        // faithful (the shader selects colour per-vertex via palU and blends two UV layers; a
        // single per-mask-texel bake with an averaged palU, plus the mask.b brightness term,
        // collapses the dominant mask regions to black). Until it reproduces correctly, exports
        // keep the greyscale facade. Enable the experiment with --facade-color.
        private static bool _facadeColorExperimental = false;

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        /// <summary>True when the args indicate the app should run headless (no GUI).</summary>
        public static bool IsCliCommand(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            switch (args[0].ToLowerInvariant())
            {
                case "export-obj":
                case "export-gltf":
                case "export-texture":
                case "export-prop":
                case "export-audio":
                case "export-all":
                case "export-shaders":
                case "inspect-materials":
                case "bake-facade":
                case "describe-resource":
                case "help":
                case "--help":
                case "-h":
                case "/?":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Entry point for CLI mode. Returns a process exit code.</summary>
        public static int Run(string[] args)
        {
            // Make Console.Write* show up in the calling terminal (WPF apps have no
            // console of their own). Harmless if launched without a parent console.
            AttachConsole(ATTACH_PARENT_PROCESS);
            Console.WriteLine();
            Logger.Info("CLI: " + string.Join(" ", args));
            _facadeVariation = GetIntArg(args, "--variation", 0);
            _facadeColorExperimental = args != null && args.Any(a => a.Equals("--facade-color", StringComparison.OrdinalIgnoreCase));

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "export-obj":
                        return RunExportModels(args, ".obj", (m, p) => m.Export(p));
                    case "export-gltf":
                    {
                        var texLookup = BuildTextureLookupForInput(args.Length > 1 ? args[1] : "", args);
                        return RunExportModels(args, ".glb", (m, p) => new GltfConverter(mesh => ResolveTextures(mesh, texLookup), mesh => ResolvePerVertexColor(mesh, texLookup)).Export(m, p));
                    }
                    case "export-texture":
                        return RunExportTextures(args);
                    case "export-prop":
                        return RunExportProp(args);
                    case "export-all":
                        return RunExportAll(args);
                    case "export-audio":
                        return RunExportAudio(args);
                    case "export-shaders":
                        return RunExportShaders(args);
                    case "inspect-materials":
                        return RunInspectMaterials(args);
                    case "bake-facade":
                        return RunBakeFacade(args);
                    case "describe-resource":
                        return RunDescribeResource(args);
                    default:
                        PrintHelp();
                        return 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Exception("CLI " + (args.Length > 0 ? args[0] : ""), ex);
                Console.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("SimCityPak CLI");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  SimCityPak.exe export-obj     <input> <outputDir>");
            Console.WriteLine("  SimCityPak.exe export-gltf    <input> <outputDir>");
            Console.WriteLine("  SimCityPak.exe export-texture <input> <outputDir>");
            Console.WriteLine("  SimCityPak.exe export-prop    <input> <outputDir>");
            Console.WriteLine("  SimCityPak.exe export-audio   <input> <outputDir>");
            Console.WriteLine("  SimCityPak.exe export-shaders <input.package> <outputDir> [--filter <substr>]");
            Console.WriteLine();
            Console.WriteLine("  <input> may be:");
            Console.WriteLine("    - a .package file  -> exports every matching resource inside it");
            Console.WriteLine("    - a folder         -> exports every matching file in it");
            Console.WriteLine("    - a single file    -> exports just that one");
            Console.WriteLine();
            Console.WriteLine("  export-obj     : RW4 models   -> Wavefront .obj  (<name>[_meshN].obj)");
            Console.WriteLine("  export-gltf    : RW4 models   -> binary glTF .glb (<name>[_meshN].glb), with the");
            Console.WriteLine("                   model's base color + normal + specular maps resolved from its");
            Console.WriteLine("                   material (searches sibling packages; override --textures <pkg|dir>)");
            Console.WriteLine("  export-texture : RW4 textures -> images (--format png|jpg|tga|dds, default png)");
            Console.WriteLine("  export-prop    : .prop property lists -> readable .txt (or .json with --json),");
            Console.WriteLine("                   property names resolved; --combine merges an asset's model +");
            Console.WriteLine("                   gameplay + catalog props into one file (package input)");
            Console.WriteLine("  export-audio   : Wwise Vorbis audio -> PCM .wav (via bundled vgmstream)");
            Console.WriteLine("  export-all     : every model -> .glb AND every texture -> image, one folder");
            Console.WriteLine("  export-shaders : shader-source containers (type 0x0469a3f7) -> .bin + .txt");
            Console.WriteLine("                   (HLSL source); --filter <substr> keeps only matching ones");
            Console.WriteLine();
            Console.WriteLine("  When exporting from a .package, add  --locale <Locale\\xx-xx\\Data.package>");
            Console.WriteLine("  (or set it in the GUI Settings) to name files by their localized asset");
            Console.WriteLine("  names instead of hashes. Falls back to hashes when no name is found.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  SimCityPak.exe export-gltf  SimCity.package C:\\out");
            Console.WriteLine("  SimCityPak.exe export-obj   C:\\models-dlc0 C:\\out");
            Console.WriteLine("  SimCityPak.exe export-audio C:\\game-audio  C:\\out");
        }

        /// <summary>
        /// Shared driver for the RW4-model export commands. <paramref name="ext"/> is the
        /// output extension (e.g. ".obj" / ".glb"); <paramref name="exporter"/> writes one
        /// mesh to one file.
        /// </summary>
        private static int RunExportModels(string[] args, string ext, Action<RW4Mesh, string> exporter)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: SimCityPak.exe " + args[0] + " <input> <outputDir>");
                return 2;
            }
            string input = args[1];
            string outDir = args[2];
            Directory.CreateDirectory(outDir);

            int models = 0, meshes = 0, fails = 0;

            if (Directory.Exists(input))
            {
                foreach (string file in Directory.GetFiles(input, "*.rw4"))
                {
                    string baseName = Path.GetFileNameWithoutExtension(file);
                    try
                    {
                        int m = ExportRw4Bytes(File.ReadAllBytes(file), outDir, baseName, ext, exporter);
                        if (m > 0) { models++; meshes += m; }
                        else Console.WriteLine("SKIP " + baseName + " (no mesh)");
                    }
                    catch (Exception ex) { fails++; Console.WriteLine("FAIL " + baseName + " :: " + ex.Message); }
                }
            }
            else if (input.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            {
                DatabasePackedFile package = DatabasePackedFile.LoadFromFile(input);
                var nameMap = BuildLocaleNameMap(package.Indices, GetLocaleFile(args));
                if (nameMap.Count > 0) Console.WriteLine("(using " + nameMap.Count + " localized names from the locale file)");
                var used = new HashSet<string>();
                foreach (DatabaseIndex index in package.Indices)
                {
                    if (index.TypeId != RW4_MODEL_TYPE_ID) continue;
                    string fallback = string.Format("{0:x8}-{1:x8}-{2:x8}",
                        index.TypeId, index.GroupContainer, index.InstanceId);
                    string baseName = ResolveBaseName(index.InstanceId, fallback, nameMap, used);
                    try
                    {
                        int m = ExportRw4Bytes(index.GetIndexData(true), outDir, baseName, ext, exporter);
                        if (m > 0) { models++; meshes += m; }
                        else Console.WriteLine("SKIP " + baseName + " (no mesh)");
                    }
                    catch (Exception ex) { fails++; Console.WriteLine("FAIL " + baseName + " :: " + ex.Message); }
                }
            }
            else if (File.Exists(input))
            {
                string baseName = Path.GetFileNameWithoutExtension(input);
                try
                {
                    int m = ExportRw4Bytes(File.ReadAllBytes(input), outDir, baseName, ext, exporter);
                    if (m > 0) { models++; meshes += m; }
                    else Console.WriteLine("SKIP " + baseName + " (no mesh)");
                }
                catch (Exception ex) { fails++; Console.WriteLine("FAIL " + baseName + " :: " + ex.Message); }
            }
            else
            {
                Console.WriteLine("ERROR: input not found: " + input);
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("Done. models={0} meshes={1} failed={2}", models, meshes, fails));
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return fails > 0 ? 1 : 0;
        }

        /// <summary>
        /// Dumps shader-source containers (type 0x0469a3f7) from a .package. Writes each
        /// matching resource's raw decompressed bytes (.bin) and its extracted printable
        /// text (.txt), which holds the fragment names + HLSL source. Optional
        /// <c>--filter &lt;substr&gt;</c> keeps only containers whose text contains that
        /// substring (case-insensitive), e.g. <c>--filter building4</c>.
        /// </summary>
        private static int RunExportShaders(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: SimCityPak.exe export-shaders <input.package> <outputDir> [--filter <substr>]");
                return 2;
            }
            string input = args[1];
            string outDir = args[2];
            string filter = null;
            for (int i = 3; i < args.Length - 1; i++)
                if (args[i].Equals("--filter", StringComparison.OrdinalIgnoreCase)) filter = args[i + 1];

            if (!input.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || !File.Exists(input))
            {
                Console.WriteLine("ERROR: export-shaders needs a .package input: " + input);
                return 2;
            }
            Directory.CreateDirectory(outDir);

            DatabasePackedFile package = DatabasePackedFile.LoadFromFile(input);
            int scanned = 0, written = 0; long totalBytes = 0;
            foreach (DatabaseIndex index in package.Indices)
            {
                if (index.TypeId != SHADER_CONTAINER_TYPE_ID) continue;
                scanned++;
                byte[] data;
                try { data = index.GetIndexData(true); }
                catch (Exception ex) { Console.WriteLine("FAIL decompress " + index.InstanceId.ToString("x8") + " :: " + ex.Message); continue; }

                string text = ExtractAsciiStrings(data, 4);
                if (filter != null && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                string baseName = string.Format("{0:x8}-{1:x8}", index.GroupContainer, index.InstanceId);
                File.WriteAllBytes(Path.Combine(outDir, baseName + ".bin"), data);
                File.WriteAllText(Path.Combine(outDir, baseName + ".txt"), text);
                written++; totalBytes += data.Length;
                Console.WriteLine(string.Format("  {0}  ({1:n0} bytes)", baseName, data.Length));
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("Done. shader containers: scanned={0} written={1} ({2:n0} bytes){3}",
                scanned, written, totalBytes, filter != null ? "  filter='" + filter + "'" : ""));
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return 0;
        }

        /// <summary>
        /// Pulls runs of printable ASCII (incl. tab/newline, so HLSL formatting survives)
        /// at least <paramref name="minLen"/> chars long out of a binary blob, separating
        /// runs with a marker line. Used to read shader source out of a container.
        /// </summary>
        private static string ExtractAsciiStrings(byte[] data, int minLen)
        {
            var sb = new StringBuilder();
            var run = new StringBuilder();
            Action flush = null;
            flush = () =>
            {
                if (run.Length >= minLen) { sb.Append(run); sb.Append("\n────────\n"); }
                run.Clear();
            };
            foreach (byte b in data)
            {
                if (b == 0x09 || b == 0x0a || b == 0x0d || (b >= 0x20 && b <= 0x7e)) run.Append((char)b);
                else flush();
            }
            flush();
            return sb.ToString();
        }

        /// <summary>Diagnostic: for each RW4 model with a textureType-116 texture (the facade
        /// palette/materialData format), prints its material texture slots — slot byte Unknown1,
        /// referenced instance id, and the resolved texture's type + dimensions. Pins down which
        /// slot is the real BuildingPaletteSample palette (expect ~256x8) vs the per-model
        /// materialData strip (~69x4). Add --all to list every model, --limit N to cap output.</summary>
        private static int RunInspectMaterials(string[] args)
        {
            if (args.Length < 2) { Console.WriteLine("Usage: SimCityPak.exe inspect-materials <input.package> [--limit N] [--all]"); return 2; }
            string input = args[1];
            int limit = GetIntArg(args, "--limit", 12);
            bool all = args.Any(a => a.Equals("--all", StringComparison.OrdinalIgnoreCase));
            if (!input.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || !File.Exists(input))
            { Console.WriteLine("ERROR: inspect-materials needs a .package: " + input); return 2; }

            var lookup = BuildTextureLookupForInput(input, args);
            DatabasePackedFile package = DatabasePackedFile.LoadFromFile(input);
            Console.WriteLine("slots: u1=<slotByte>:<instanceId>:<kind/type/WxH>   (tt74=f32-RGBA=116)");
            int shown = 0;
            foreach (DatabaseIndex index in package.Indices)
            {
                if (index.TypeId != RW4_MODEL_TYPE_ID) continue;
                RW4Model model;
                try { model = new RW4Model(); using (var ms = new MemoryStream(index.GetIndexData(true))) model.Read(ms); }
                catch { continue; }
                var refs = new List<MaterialTextureReference>();
                foreach (RW4Section s in model.Sections)
                {
                    RW4Material mat = s.obj as RW4Material;
                    if (mat != null && mat.Materials != null) refs.AddRange(mat.Materials);
                }
                if (refs.Count == 0) continue;
                var descs = new List<string>();
                bool has116 = false;
                foreach (var mr in refs)
                {
                    if (mr.TextureInstanceId == 0) { descs.Add("u1=" + mr.Unknown1 + ":--"); continue; }
                    string d; DatabaseIndex tdi;
                    if (lookup != null && lookup.TryGetValue(mr.TextureInstanceId, out tdi)) d = DescribeTexture(tdi, ref has116);
                    else d = "unresolved";
                    descs.Add("u1=" + mr.Unknown1 + ":" + mr.TextureInstanceId.ToString("x8") + ":" + d);
                }
                if (!all && !has116) continue;
                Console.WriteLine(index.InstanceId.ToString("x8") + "  " + string.Join("  ", descs));
                if (++shown >= limit) break;
            }
            Console.WriteLine();
            Console.WriteLine("shown=" + shown + (all ? " (all models)" : " (only models with a texType-116 texture)"));
            return 0;
        }

        /// <summary>Diagnostic: bake one building's facade albedo (the building4 palette composite)
        /// to a PNG so it can be eyeballed, independent of the glTF facade-UV gating. Resolves the
        /// model's palette (slot u1=0, texType 116) and region mask (slot u1=1), runs
        /// CompositeFacadePalette with the mesh's representative palU and the chosen --variation,
        /// and writes &lt;inst&gt;_baked_vN.png plus &lt;inst&gt;_mask.png for comparison.</summary>
        private static int RunBakeFacade(string[] args)
        {
            if (args.Length < 4) { Console.WriteLine("Usage: SimCityPak.exe bake-facade <input.package> <instanceHex> <outDir> [--variation N]"); return 2; }
            string input = args[1]; string outDir = args[3];
            uint inst;
            try { inst = Convert.ToUInt32(args[2].Replace("0x", ""), 16); }
            catch { Console.WriteLine("bad instance id: " + args[2]); return 2; }
            Directory.CreateDirectory(outDir);

            var lookup = BuildTextureLookupForInput(input, args);
            var package = DatabasePackedFile.LoadFromFile(input);
            var index = package.Indices.FirstOrDefault(i => i.TypeId == RW4_MODEL_TYPE_ID && i.InstanceId == inst);
            if (index == null) { Console.WriteLine("model not found: " + inst.ToString("x8")); return 2; }

            var model = new RW4Model();
            using (var ms = new MemoryStream(index.GetIndexData(true))) model.Read(ms);
            MaterialTextureReference palRef = null, maskRef = null;
            foreach (RW4Section s in model.Sections)
            {
                RW4Material mat = s.obj as RW4Material;
                if (mat == null || mat.Materials == null) continue;
                foreach (var mr in mat.Materials)
                {
                    if (mr.Unknown1 == 0 && palRef == null) palRef = mr;
                    if (mr.Unknown1 == 1 && maskRef == null) maskRef = mr;
                }
            }
            if (palRef == null || maskRef == null) { Console.WriteLine("missing palette (u1=0) or mask (u1=1) slot"); return 2; }

            // --palette <hex> overrides the palette source (e.g. the global tint atlas 0x9590d255)
            uint palInst = palRef.TextureInstanceId;
            for (int i = 4; i < args.Length - 1; i++)
                if (args[i].Equals("--palette", StringComparison.OrdinalIgnoreCase))
                    try { palInst = Convert.ToUInt32(args[i + 1].Replace("0x", ""), 16); } catch { }

            DatabaseIndex palIdx, maskIdx;
            lookup.TryGetValue(palInst, out palIdx);
            lookup.TryGetValue(maskRef.TextureInstanceId, out maskIdx);
            int pw = 0, ph = 0; float[] pal = palIdx != null ? DecodePaletteFloatRgba(palIdx, out pw, out ph) : null;
            if (pal == null && palIdx != null)   // non-float palette (DXT / raster) -> decode to float RGBA
            {
                int bw2, bh2; byte[] prgba = DecodeTextureResourceRgba(palIdx, out bw2, out bh2);
                if (prgba != null) { pw = bw2; ph = bh2; pal = new float[pw * ph * 4]; for (int i = 0; i < pal.Length; i++) pal[i] = prgba[i] / 255f; }
            }
            Console.WriteLine("palette instance: " + palInst.ToString("x8") + (palIdx == null ? "  (NOT FOUND in lookup)" : ""));
            int mw = 0, mh = 0; byte[] mask = maskIdx != null ? DecodeTextureResourceRgba(maskIdx, out mw, out mh) : null;
            if (pal == null || mask == null) { Console.WriteLine("decode failed: palette=" + (pal != null) + " mask=" + (mask != null)); return 2; }

            RW4Mesh mesh = model.Sections.Select(s => s.obj as RW4Mesh).FirstOrDefault(x => x != null && x.vertices != null);
            float palU, palU2; ComputeAveragePalette(mesh, out palU, out palU2);
            // vertex COLOR r/g spread (tells us if palette selection varies per-vertex)
            float rMin = 1, rMax = 0, gMin = 1, gMax = 0; int vc = 0;
            if (mesh != null && mesh.vertices != null && mesh.vertices.vertices != null)
                foreach (Vertex v in mesh.vertices.vertices)
                {
                    if (v.VertexComponents == null) continue;
                    foreach (var comp in v.VertexComponents)
                    {
                        if (comp.Usage != D3DDECLUSAGE.D3DDECLUSAGE_COLOR) continue;
                        var col = comp as VertexD3DColorValue;
                        if (col != null) { float r = col.R / 255f, g = col.G / 255f; if (r < rMin) rMin = r; if (r > rMax) rMax = r; if (g < gMin) gMin = g; if (g > gMax) gMax = g; vc++; }
                        break;
                    }
                }
            int variation = GetIntArg(args, "--variation", 0);
            float palV = variation * (1f / 8f);
            string tag = inst.ToString("x8");
            // Bake both layers' palette columns so we can see which carries the colour.
            File.WriteAllBytes(Path.Combine(outDir, tag + "_baked_base_v" + variation + ".png"), GltfConverter.RgbaToPng(CompositeFacadePalette(mask, mw, mh, pal, pw, ph, palU, palV), mw, mh));
            File.WriteAllBytes(Path.Combine(outDir, tag + "_baked_top_v" + variation + ".png"), GltfConverter.RgbaToPng(CompositeFacadePalette(mask, mw, mh, pal, pw, ph, palU2, palV), mw, mh));
            File.WriteAllBytes(Path.Combine(outDir, tag + "_mask.png"), GltfConverter.RgbaToPng(mask, mw, mh));
            File.WriteAllBytes(Path.Combine(outDir, tag + "_palette.png"), RenderPalettePng(pal, pw, ph, 20));
            Console.WriteLine(string.Format("pal {0}x{1}  mask {2}x{3}  variation={4}", pw, ph, mw, mh, variation));
            Console.WriteLine(string.Format("vtx COLOR: r[{0:f3}..{1:f3}] g[{2:f3}..{3:f3}]  avg palU={4:f3} palU2={5:f3}  verts={6}", rMin, rMax, gMin, gMax, palU, palU2, vc));
            for (int row = 0; row < ph; row++)
            {
                var c0 = SamplePaletteBilinear(pal, pw, ph, 0.02f, (row + 0.5f) / ph);
                var cM = SamplePaletteBilinear(pal, pw, ph, 0.5f, (row + 0.5f) / ph);
                var cE = SamplePaletteBilinear(pal, pw, ph, 0.98f, (row + 0.5f) / ph);
                Console.WriteLine(string.Format("  palette row{0}: L({1:f2},{2:f2},{3:f2}) M({4:f2},{5:f2},{6:f2}) R({7:f2},{8:f2},{9:f2})",
                    row, c0[0], c0[1], c0[2], cM[0], cM[1], cM[2], cE[0], cE[1], cE[2]));
            }
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return 0;
        }

        /// <summary>Diagnostic: finds every resource with a given instance id across the input
        /// package and its siblings, printing TypeId/Group/sizes, the first bytes, and (for RW4 or
        /// raster textures) the decoded header fields — WITHOUT a full decode, so it never OOMs on
        /// a mis-sized texture. Used to identify what a referenced id (e.g. the global tint atlas
        /// 0x9590d255) actually is.</summary>
        private static int RunDescribeResource(string[] args)
        {
            if (args.Length < 3) { Console.WriteLine("Usage: SimCityPak.exe describe-resource <input.package> <instanceHex>"); return 2; }
            string input = args[1]; uint inst;
            try { inst = Convert.ToUInt32(args[2].Replace("0x", ""), 16); }
            catch { Console.WriteLine("bad instance id: " + args[2]); return 2; }

            var files = new List<string>();
            if (input.EndsWith(".package", StringComparison.OrdinalIgnoreCase) && File.Exists(input))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(input));
                if (dir != null) files.AddRange(Directory.GetFiles(dir, "*.package"));
            }
            if (files.Count == 0) { Console.WriteLine("no packages found next to: " + input); return 2; }

            int found = 0;
            foreach (string pf in files)
            {
                DatabasePackedFile pkg;
                try { pkg = DatabasePackedFile.LoadFromFile(pf); } catch { continue; }
                foreach (DatabaseIndex idx in pkg.Indices)
                {
                    if (idx.InstanceId != inst) continue;
                    found++;
                    byte[] data = null; try { data = idx.GetIndexData(true); } catch (Exception ex) { Console.WriteLine("  (decompress failed: " + ex.Message + ")"); }
                    string hdr = "";
                    if (data != null) { int n = Math.Min(48, data.Length); var sb = new StringBuilder(); for (int i = 0; i < n; i++) sb.Append(data[i].ToString("x2") + (i % 4 == 3 ? " " : "")); hdr = sb.ToString(); }
                    Console.WriteLine(string.Format("{0}  T={1:x8} G={2:x8} I={3:x8}  dec={4}  [0..48]= {5}",
                        Path.GetFileName(pf), idx.TypeId, idx.GroupContainer, idx.InstanceId, data != null ? data.Length : -1, hdr));
                    if (data != null && idx.TypeId == RW4_MODEL_TYPE_ID)
                    {
                        try
                        {
                            var m = new RW4Model(); using (var ms = new MemoryStream(data)) m.Read(ms);
                            foreach (RW4Section s in m.Sections) { var t = s.obj as Texture; if (t != null) Console.WriteLine(string.Format("    RW4 Texture: type={0} {1}x{2}", t.textureType, t.width, t.height)); }
                        }
                        catch (Exception ex) { Console.WriteLine("    RW4 parse err: " + ex.Message); }
                    }
                    else if (data != null && idx.TypeId == TEX_RASTER_TYPE_ID && data.Length >= 28)
                        Console.WriteLine(string.Format("    raster: w={0} h={1} pixFmt={2} blockSize={3}", BE(data, 4), BE(data, 8), BE(data, 20), BE(data, 24)));
                }
            }
            Console.WriteLine("matches=" + found);
            return 0;
        }

        private static string DescribeTexture(DatabaseIndex tdi, ref bool has116)
        {
            try
            {
                if (tdi.TypeId == TEX_RASTER_TYPE_ID)
                {
                    byte[] data = tdi.GetIndexData(true);
                    if (data.Length >= 24) return string.Format("raster/pf{0}/{1}x{2}", BE(data, 20), BE(data, 4), BE(data, 8));
                    return "raster";
                }
                var model = new RW4Model();
                using (var ms = new MemoryStream(tdi.GetIndexData(true))) model.Read(ms);
                var texSec = model.Sections.FirstOrDefault(x => x.TypeCode == SectionTypeCodes.Texture && x.obj is Texture);
                if (texSec == null) return "rw4/notex";
                Texture t = (Texture)texSec.obj;
                if (t.textureType == 116) has116 = true;
                return string.Format("rw4/tt{0}/{1}x{2}", t.textureType, t.width, t.height);
            }
            catch { return "err"; }
        }



        // ----- texture resolution (for textured model export) ---------------

        // SimCity stores textures as separate resources, referenced from a model's
        // RW4Material by instance id: RW4-wrapped textures share the model type id
        // (0x2f4e681b); raster images use 0x2f4e681c. They often live in OTHER
        // packages (SimCity_Graphics, ...), so we index across packages.
        private const uint TEX_RASTER_TYPE_ID = 0x2f4e681c;

        /// <summary>
        /// instanceId -> resource, for every texture-bearing resource in <paramref name="indices"/>
        /// (RW4 image / raster image). Used to resolve a model's material texture references.
        /// </summary>
        private static Dictionary<uint, DatabaseIndex> BuildTextureLookup(IEnumerable<DatabaseIndex> indices)
        {
            var map = new Dictionary<uint, DatabaseIndex>();
            foreach (DatabaseIndex idx in indices)
            {
                if (idx.TypeId != RW4_MODEL_TYPE_ID && idx.TypeId != TEX_RASTER_TYPE_ID) continue;
                if (!map.ContainsKey(idx.InstanceId)) map[idx.InstanceId] = idx;
            }
            return map;
        }

        /// <summary>Adds texture resources from every <c>.package</c> file found in the given
        /// directories to an existing lookup, WITHOUT overwriting entries already present (so the
        /// caller's loaded/edited resources win). Lets the GUI resolve textures that live in
        /// sibling packages (e.g. SimCity_Graphics.package) when only one package is open. Never
        /// throws per package.</summary>
        private static void AddSiblingTextures(Dictionary<uint, DatabaseIndex> map, IEnumerable<string> dirs)
        {
            if (map == null || dirs == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string[] files;
                try { files = Directory.GetFiles(dir, "*.package"); } catch { continue; }
                foreach (string pf in files)
                {
                    if (!seen.Add(pf)) continue;
                    try
                    {
                        foreach (var kv in BuildTextureLookup(DatabasePackedFile.LoadFromFile(pf).Indices))
                            if (!map.ContainsKey(kv.Key)) map[kv.Key] = kv.Value;
                    }
                    catch (Exception ex) { Logger.Exception("sibling texture index " + Path.GetFileName(pf), ex); }
                }
            }
        }

        // Cached sibling-package texture lookup for the viewer, so selecting meshes doesn't rescan
        // the game folder (~33 packages) every time. Rebuilt only when the directory set changes.
        private static Dictionary<uint, DatabaseIndex> _siblingTexCache;
        private static string _siblingTexCacheKey;

        private static Dictionary<uint, DatabaseIndex> GetSiblingTextureLookup(IEnumerable<string> dirs)
        {
            if (dirs == null) return new Dictionary<uint, DatabaseIndex>();
            string key = string.Join("|", dirs.Where(d => !string.IsNullOrEmpty(d)).Distinct().OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
            if (_siblingTexCache != null && _siblingTexCacheKey == key) return _siblingTexCache;
            var map = new Dictionary<uint, DatabaseIndex>();
            AddSiblingTextures(map, dirs);
            _siblingTexCache = map; _siblingTexCacheKey = key;
            return map;
        }

        /// <summary>Texture lookup for the viewer: the loaded resources (priority) plus the cached
        /// sibling .package files on disk, so a mod whose textures live in the base packages still
        /// resolves even when only the mod is open.</summary>
        private static Dictionary<uint, DatabaseIndex> BuildViewerLookup(IEnumerable<DatabaseIndex> indices, IEnumerable<string> textureDirs)
        {
            var map = BuildTextureLookup(indices);
            if (textureDirs != null)
                foreach (var kv in GetSiblingTextureLookup(textureDirs))
                    if (!map.ContainsKey(kv.Key)) map[kv.Key] = kv.Value;
            return map;
        }

        /// <summary>
        /// Texture lookup for a .package CLI input: the input package PLUS every other
        /// .package beside it (so textures stored in the shared graphics packages resolve).
        /// A <c>--textures &lt;package|folder&gt;</c> argument overrides the search location.
        /// Returns null when there's no package context (folder/loose-file input).
        /// </summary>
        private static Dictionary<uint, DatabaseIndex> BuildTextureLookupForInput(string input, string[] args)
        {
            var searchFiles = new List<string>();
            string overridePath = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("--textures", StringComparison.OrdinalIgnoreCase)) overridePath = args[i + 1];

            if (overridePath != null)
            {
                if (Directory.Exists(overridePath)) searchFiles.AddRange(Directory.GetFiles(overridePath, "*.package"));
                else if (File.Exists(overridePath)) searchFiles.Add(overridePath);
            }
            else if (input.EndsWith(".package", StringComparison.OrdinalIgnoreCase) && File.Exists(input))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(input));
                if (dir != null) searchFiles.AddRange(Directory.GetFiles(dir, "*.package"));
            }
            if (searchFiles.Count == 0) return null;

            var map = new Dictionary<uint, DatabaseIndex>();
            foreach (string pf in searchFiles)
            {
                try
                {
                    foreach (var kv in BuildTextureLookup(DatabasePackedFile.LoadFromFile(pf).Indices))
                        if (!map.ContainsKey(kv.Key)) map[kv.Key] = kv.Value;
                }
                catch (Exception ex) { Logger.Exception("texture index " + Path.GetFileName(pf), ex); }
            }
            Console.WriteLine("(indexed " + map.Count + " textures across " + searchFiles.Count + " package(s) for material resolution)");
            return map;
        }

        /// <summary>
        /// Resolves a mesh's texture maps (base color + normal + specular) via its model's
        /// RW4Material texture references, decoding the referenced resources (RW4 DXT or raw
        /// raster). Returns null if none resolve (caller falls back to the internal-texture
        /// heuristic). Never throws.
        ///
        /// SimCity slot layout (from the SporeModder addon's 3-texture static model): a colour
        /// /zone map, a NORMAL map whose alpha is the specular mask, plus secondary masks. The
        /// normal map is stored "pink" (flat ~255,128,128), i.e. R&lt;-&gt;B swizzled versus the
        /// glTF convention (flat 128,128,255); we detect and unswizzle it, and lift its alpha
        /// into an RGB specular-colour map.
        /// </summary>
        /// <summary>Public: the model's resolved base-colour map as PNG bytes (the same diffuse the
        /// glTF export embeds), resolved against the given resources, or null. Used by the in-app 3D
        /// viewer so non-facade models (landmarks, props, mods) show their real texture instead of
        /// flat grey. Facade buildings are handled separately via <see cref="GetFacadePaletteRgba"/>.</summary>
        public static byte[] GetModelBaseColorPng(RW4Mesh mesh, IEnumerable<DatabaseIndex> indices, IEnumerable<string> textureDirs)
        {
            if (mesh == null || indices == null) return null;
            try
            {
                var tex = ResolveTextures(mesh, BuildViewerLookup(indices, textureDirs));
                return tex != null ? tex.BaseColor : null;
            }
            catch (Exception ex) { Logger.Exception("GetModelBaseColorPng", ex); return null; }
        }

        private static GltfConverter.MaterialTextures ResolveTextures(RW4Mesh mesh, Dictionary<uint, DatabaseIndex> lookup)
        {
            try
            {
                if (lookup == null || mesh == null || mesh.model == null) return null;

                byte[] baseRgba = null, fbRgba = null, normalRgba = null, palLum = null;
                int baseW = 0, baseH = 0, fbW = 0, fbH = 0, nW = 0, nH = 0, palW = 0;
                long baseArea = -1, fbArea = -1, nArea = -1;
                float[] palFloat = null; int palFW = 0, palFH = 0;   // full HDR palette (texType 116)

                foreach (RW4Section s in mesh.model.Sections)
                {
                    RW4Material mat = s.obj as RW4Material;
                    if (mat == null || mat.Materials == null) continue;
                    foreach (MaterialTextureReference mr in mat.Materials)
                    {
                        DatabaseIndex tdi;
                        if (mr.TextureInstanceId == 0) continue;
                        if (!lookup.TryGetValue(mr.TextureInstanceId, out tdi)) continue;
                        // The per-model facade palette (float32 strip, textureType 116). Always keep
                        // its row-0 luminance (the default greyscale facade path). Additionally
                        // decode the FULL HDR palette when the experimental --facade-color bake is
                        // on (the real building4 BuildingPaletteSample composite).
                        if (palLum == null)
                        {
                            if (_facadeColorExperimental && palFloat == null)
                            {
                                float[] pf = DecodePaletteFloatRgba(tdi, out palFW, out palFH);
                                if (pf != null) palFloat = pf;
                            }
                            int pw; byte[] lum = DecodePaletteLuminance(tdi, out pw);
                            if (lum != null) { palLum = lum; palW = pw; continue; }
                            if (palFloat != null) continue;   // a palette, even if the luminance helper declined
                        }
                        int w, h;
                        byte[] rgba = DecodeTextureResourceRgba(tdi, out w, out h);
                        if (rgba == null || w < 16 || h < 16) continue;   // skip strips/atlases
                        long area = (long)w * h;
                        if (IsSimCityNormalMap(rgba))
                        {
                            if (area > nArea) { normalRgba = rgba; nW = w; nH = h; nArea = area; }
                            continue;
                        }
                        if (area > fbArea) { fbRgba = rgba; fbW = w; fbH = h; fbArea = area; }
                        if (!GltfConverter.LooksLikeNormalMap(rgba) && area > baseArea)
                        { baseRgba = rgba; baseW = w; baseH = h; baseArea = area; }
                    }
                }

                byte[] colorRgba = baseRgba ?? fbRgba;
                int cW = baseRgba != null ? baseW : fbW, cH = baseRgba != null ? baseH : fbH;
                if (colorRgba == null && normalRgba == null) return null;

                // SimCity has no baked albedo. The colour map is a false-colour region/zone atlas
                // that the GlassBox building4 pixel shader recolours through the per-model HDR
                // palette (BuildingPaletteSample). We now reproduce that composite offline: the
                // mask's R,G sub-sample the palette cell, the mesh's vertex COLOR selects the
                // palette column (palU), the chosen variation row selects palV, and the mask's B
                // is a brightness multiplier. This yields the real facade colour, not greyscale.
                // Falls back to the old row-0 luminance greyscale when only that is available.
                if (_facadeColorExperimental && colorRgba != null && palFloat != null && palFW > 0 && palFH > 0)
                {
                    float palU, palU2;
                    ComputeAveragePalette(mesh, out palU, out palU2);
                    float palV = _facadeVariation * (1f / 8f);   // BuildingPaletteVariationVS: row/8
                    colorRgba = CompositeFacadePalette(colorRgba, cW, cH, palFloat, palFW, palFH, palU, palV);
                }
                else if (colorRgba != null && palLum != null && palW > 1)
                    colorRgba = CompositeFacadeLuminance(colorRgba, cW, cH, palLum, palW);

                var result = new GltfConverter.MaterialTextures();
                if (colorRgba != null) result.BaseColor = GltfConverter.RgbaToPng(colorRgba, cW, cH);
                if (normalRgba != null)
                {
                    result.Normal = GltfConverter.RgbaToPng(UnswizzleNormal(normalRgba), nW, nH);
                    byte[] spec = SpecularFromAlpha(normalRgba);
                    if (spec != null) result.Specular = GltfConverter.RgbaToPng(spec, nW, nH);
                }
                return result;
            }
            catch (Exception ex) { Logger.Exception("ResolveTextures", ex); return null; }
        }

        /// <summary>Per-vertex facade palette albedo for glTF COLOR_0 (path A, faithful PBR): for
        /// each vertex, select the palette column from its COLOR.r/.g (palU/palU2, the two layers),
        /// sample the model's HDR palette (material slot u1=0, texType 116) at the chosen
        /// variation row, and take the brighter of the two layers. Values stay LINEAR (glTF COLOR_0
        /// is linear) so a PBR viewer lights them. Returns vCount*4 RGBA bytes, or null when the
        /// model has no palette (non-facade meshes keep their normal textures). Gated by
        /// --facade-color while under development.</summary>
        /// <summary>Export path: per-vertex facade colour. On by default now that it uses the real
        /// slot-4 tint palette (believable colour); --facade-color additionally turns on the
        /// experimental per-texel palette composite in ResolveTextures.</summary>
        private static byte[] ResolvePerVertexColor(RW4Mesh mesh, Dictionary<uint, DatabaseIndex> lookup)
        {
            return ComputeFacadeVertexColorsCore(mesh, lookup);
        }

        /// <summary>Public: per-vertex facade palette colour (RGBA bytes, length vCount*4), resolving
        /// the mesh's slot-4 tint palette against the given resources (e.g. all loaded packages).
        /// Null when the mesh has no facade palette. Used by the in-app 3D viewer to show buildings
        /// in colour — WPF's Viewport3D has no custom shaders, so the palette colour is fed in as a
        /// per-vertex colour (via a 1-D colour texture), the same colour the glTF export bakes.</summary>
        public static byte[] ComputeFacadeVertexColors(RW4Mesh mesh, IEnumerable<DatabaseIndex> indices)
        {
            if (mesh == null || indices == null) return null;
            return ComputeFacadeVertexColorsCore(mesh, BuildTextureLookup(indices));
        }

        /// <summary>Public: the mesh's slot-4 tint palette as RGBA (and its size), resolved against
        /// the given resources, or null if the mesh has no facade palette. The in-app 3D viewer
        /// uses this as the diffuse texture and feeds each vertex's palette coordinate
        /// (COLOR.r = column, COLOR.g = material row) as its UV, so WPF samples the facade colour
        /// without a custom shader.</summary>
        public static byte[] GetFacadePaletteRgba(RW4Mesh mesh, IEnumerable<DatabaseIndex> indices, IEnumerable<string> textureDirs, out int width, out int height)
        {
            width = 0; height = 0;
            if (mesh == null || mesh.model == null || indices == null) return null;
            var lookup = BuildViewerLookup(indices, textureDirs);
            foreach (RW4Section s in mesh.model.Sections)
            {
                RW4Material mat = s.obj as RW4Material;
                if (mat == null || mat.Materials == null) continue;
                foreach (var mr in mat.Materials)
                {
                    if (mr.Unknown1 != 4 || mr.TextureInstanceId == 0) continue;   // slot4 = tint palette
                    DatabaseIndex tdi;
                    if (!lookup.TryGetValue(mr.TextureInstanceId, out tdi)) continue;
                    byte[] rgba = DecodeTextureResourceRgba(tdi, out width, out height);
                    if (rgba != null) { NeutralizeGreenPlaceholders(rgba); return rgba; }
                }
            }
            return null;
        }

        /// <summary>The slot-4 tint palette fills its unused entries with pure green (0,255,0) as a
        /// placeholder. A vertex that indexes one would otherwise glow green, so remap those texels
        /// to a neutral grey (they are "no tint" slots, not a real colour).</summary>
        private static void NeutralizeGreenPlaceholders(byte[] rgba)
        {
            if (rgba == null) return;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                byte r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                if (g > 200 && r < 80 && b < 80) { rgba[i] = 150; rgba[i + 1] = 150; rgba[i + 2] = 150; }
            }
        }

        private static byte[] ComputeFacadeVertexColorsCore(RW4Mesh mesh, Dictionary<uint, DatabaseIndex> lookup)
        {
            try
            {
                if (lookup == null || mesh == null || mesh.model == null || mesh.vertices == null || mesh.vertices.vertices == null) return null;

                // Facade colour = OpenSCP's building4 lookup, per-vertex approximation.
                //   slot0 "params table" (float4 rows, W x 4): row 0 holds the palette UV origin
                //           (palOrigin.x = base palette column) for each MATERIAL COLUMN;
                //   slot4 "palette" (512x16 albedo lookup) is sampled at that column.
                // The material column is selected per-vertex by D3DCOLOR.G (0..W-1).  Using the
                // param table here (instead of COLOR.r) is what makes each material/region land on
                // its OWN colour -- sampling COLOR.r collapsed every building to palette column 0
                // (the uniform blue bug).
                float[] prm = null; int prw = 0, prh = 0;   // slot0 param table
                float[] pal = null; int pw = 0, ph = 0;     // slot4 palette
                foreach (RW4Section s in mesh.model.Sections)
                {
                    RW4Material mat = s.obj as RW4Material;
                    if (mat == null || mat.Materials == null) continue;
                    foreach (var mr in mat.Materials)
                    {
                        if (mr.TextureInstanceId == 0) continue;
                        DatabaseIndex tdi;
                        if (!lookup.TryGetValue(mr.TextureInstanceId, out tdi)) continue;
                        if (mr.Unknown1 == 0 && prm == null)            // slot0 = param table (float)
                        {
                            prm = DecodePaletteFloatRgba(tdi, out prw, out prh);
                        }
                        else if (mr.Unknown1 == 4 && pal == null)        // slot4 = tint palette
                        {
                            int bw2, bh2; byte[] rgba = DecodeTextureResourceRgba(tdi, out bw2, out bh2);
                            if (rgba != null) { NeutralizeGreenPlaceholders(rgba); pw = bw2; ph = bh2; pal = new float[pw * ph * 4]; for (int i = 0; i < pal.Length; i++) pal[i] = rgba[i] / 255f; }
                        }
                    }
                    if (pal != null && prm != null) break;
                }
                if (pal == null) return null;

                // Palette row: buildings use row 0 (uPaletteRow = 0); add the variation offset so the
                // --facade-variation knob still works for props/instances that use other rows.
                float palV = (_facadeVariation + 0.5f) / Math.Max(ph, 1);
                var verts = mesh.vertices.vertices;
                int vCount = verts.Length;
                byte[] outp = new byte[vCount * 4];
                int vi = 0;
                foreach (Vertex v in verts)
                {
                    int cgByte = 0; float cr = 0;
                    if (v.VertexComponents != null)
                        foreach (var comp in v.VertexComponents)
                        {
                            if (comp.Usage != D3DDECLUSAGE.D3DDECLUSAGE_COLOR) continue;
                            var col = comp as VertexD3DColorValue;
                            if (col != null) { cgByte = col.G; cr = col.R / 255f; }
                            break;
                        }

                    // palU: from slot0 param-table row 0, column = material index (COLOR.G).
                    float palU;
                    if (prm != null && prw > 0)
                    {
                        int colx = cgByte; if (colx >= prw) colx = prw - 1; if (colx < 0) colx = 0;
                        palU = prm[colx * 4];               // row 0, R channel = palOrigin.x
                        if (palU < 0f) palU = 0f; else if (palU > 1f) palU = 1f;
                    }
                    else
                    {
                        // No param table -> fall back to the old COLOR.r column (better than nothing).
                        palU = cr * (255f / 256f);
                    }

                    float[] c = SamplePaletteBilinear(pal, pw, ph, palU, palV);
                    outp[vi * 4]     = ToByteClamp(c[0]);   // slot4 is already a proper albedo
                    outp[vi * 4 + 1] = ToByteClamp(c[1]);
                    outp[vi * 4 + 2] = ToByteClamp(c[2]);
                    outp[vi * 4 + 3] = 255;
                    vi++;
                }
                return outp;
            }
            catch (Exception ex) { Logger.Exception("ResolvePerVertexColor", ex); return null; }
        }

        /// <summary>SimCity's normal maps are "pink": flat areas ~ (255,128,128), i.e. the up
        /// component is in RED with X/Y centred in the other channels. Detected by a dominant,
        /// high red average with green and blue clustered near mid-grey.</summary>
        private static bool IsSimCityNormalMap(byte[] rgba)
        {
            int n = rgba.Length / 4;
            if (n == 0) return false;
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < n; i++) { r += rgba[i * 4]; g += rgba[i * 4 + 1]; b += rgba[i * 4 + 2]; }
            double ar = (double)r / n, ag = (double)g / n, ab = (double)b / n;
            return ar > 180 && Math.Abs(ag - 128) < 45 && Math.Abs(ab - 128) < 45 && ar > ag + 60 && ar > ab + 60;
        }

        /// <summary>Converts SimCity's "pink" normal (flat 255,128,128) to the glTF convention
        /// (flat 128,128,255) by swapping the R and B channels. Alpha set opaque.</summary>
        private static byte[] UnswizzleNormal(byte[] rgba)
        {
            byte[] outp = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                outp[i] = rgba[i + 2];      // R <- B
                outp[i + 1] = rgba[i + 1];  // G
                outp[i + 2] = rgba[i];      // B <- R
                outp[i + 3] = 255;
            }
            return outp;
        }

        /// <summary>The specular mask is packed in the normal map's alpha channel; expand it to
        /// an opaque greyscale RGB image for KHR_materials_specular. Returns null if alpha is
        /// effectively constant (no usable spec data).</summary>
        private static byte[] SpecularFromAlpha(byte[] rgba)
        {
            int n = rgba.Length / 4;
            byte mn = 255, mx = 0;
            for (int i = 0; i < n; i++) { byte a = rgba[i * 4 + 3]; if (a < mn) mn = a; if (a > mx) mx = a; }
            if (mx - mn < 8) return null;
            byte[] outp = new byte[rgba.Length];
            for (int i = 0; i < n; i++)
            {
                byte a = rgba[i * 4 + 3];
                outp[i * 4] = a; outp[i * 4 + 1] = a; outp[i * 4 + 2] = a; outp[i * 4 + 3] = 255;
            }
            return outp;
        }

        /// <summary>Reads the per-model facade palette (float32 RGBA, textureType 116) and returns
        /// row 0 as <paramref name="width"/> luminance bytes (its R channel is the albedo
        /// brightness; the other rows/channels are HDR material params, not colour). Null if the
        /// resource isn't such a palette.</summary>
        private static byte[] DecodePaletteLuminance(DatabaseIndex tdi, out int width)
        {
            width = 0;
            if (tdi.TypeId != RW4_MODEL_TYPE_ID) return null;
            try
            {
                var model = new RW4Model();
                using (var ms = new MemoryStream(tdi.GetIndexData(true))) model.Read(ms);
                var texSec = model.Sections.FirstOrDefault(x => x.TypeCode == SectionTypeCodes.Texture && x.obj is Texture);
                if (texSec == null) return null;
                Texture t = (Texture)texSec.obj;
                if (t.textureType != 116) return null;       // D3DFMT_A32B32G32R32F (float32 RGBA)
                int w = t.width; byte[] blob = t.texData.blob;
                if (w <= 0 || blob == null || blob.Length < w * 16) return null;
                byte[] lum = new byte[w];
                for (int x = 0; x < w; x++)
                {
                    float r = BitConverter.ToSingle(blob, x * 16);   // row 0, R = albedo luminance
                    int v = (int)(r * 255f + 0.5f);
                    lum[x] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
                }
                width = w;
                return lum;
            }
            catch { return null; }
        }

        /// <summary>Reads the per-model facade palette (float32 RGBA, textureType 116) as a full
        /// width x height HDR palette (all rows/channels), for the real BuildingPaletteSample
        /// composite. Returns null if the resource isn't such a palette.</summary>
        private static float[] DecodePaletteFloatRgba(DatabaseIndex tdi, out int width, out int height)
        {
            width = 0; height = 0;
            if (tdi.TypeId != RW4_MODEL_TYPE_ID) return null;
            try
            {
                var model = new RW4Model();
                using (var ms = new MemoryStream(tdi.GetIndexData(true))) model.Read(ms);
                var texSec = model.Sections.FirstOrDefault(x => x.TypeCode == SectionTypeCodes.Texture && x.obj is Texture);
                if (texSec == null) return null;
                Texture t = (Texture)texSec.obj;
                if (t.textureType != 116) return null;        // D3DFMT_A32B32G32R32F (float32 RGBA)
                int w = t.width, h = t.height; byte[] blob = t.texData != null ? t.texData.blob : null;
                if (w <= 0 || h <= 0 || blob == null || blob.LongLength < (long)w * h * 16) return null;
                float[] pal = new float[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    pal[i * 4]     = BitConverter.ToSingle(blob, i * 16);
                    pal[i * 4 + 1] = BitConverter.ToSingle(blob, i * 16 + 4);
                    pal[i * 4 + 2] = BitConverter.ToSingle(blob, i * 16 + 8);
                    pal[i * 4 + 3] = BitConverter.ToSingle(blob, i * 16 + 12);
                }
                width = w; height = h;
                return pal;
            }
            catch { return null; }
        }

        /// <summary>Bilinear sample (clamp to edge, UV in [0,1]) of an HDR float RGBA palette,
        /// matching the GlassBox D3D9 linear sampler used by BuildingPaletteSample.</summary>
        private static float[] SamplePaletteBilinear(float[] pal, int w, int h, float u, float v)
        {
            if (u < 0) u = 0; else if (u > 1) u = 1;
            if (v < 0) v = 0; else if (v > 1) v = 1;
            float fx = u * w - 0.5f, fy = v * h - 0.5f;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            if (x0 < 0) x0 = 0; else if (x0 > w - 1) x0 = w - 1;
            if (x1 < 0) x1 = 0; else if (x1 > w - 1) x1 = w - 1;
            if (y0 < 0) y0 = 0; else if (y0 > h - 1) y0 = h - 1;
            if (y1 < 0) y1 = 0; else if (y1 > h - 1) y1 = h - 1;
            var outc = new float[4];
            for (int c = 0; c < 4; c++)
            {
                float a = pal[(y0 * w + x0) * 4 + c], b = pal[(y0 * w + x1) * 4 + c];
                float cc = pal[(y1 * w + x0) * 4 + c], d = pal[(y1 * w + x1) * 4 + c];
                float top = a + (b - a) * tx, bot = cc + (d - cc) * tx;
                outc[c] = top + (bot - top) * ty;
            }
            return outc;
        }

        /// <summary>Reproduces the building4 facade colour composite (BuildingPaletteSample) per
        /// mask texel: palette-U = palU + mask.r sub-sample, palette-V = palV (variation) + mask.g
        /// sub-sample, result scaled by mask.b*2 (the brightness term). Clamps the HDR result to
        /// 8-bit. Keeps the mask alpha. This is the base layer; the second (relief) layer needs the
        /// mesh's second UV set and is not baked here.</summary>
        private static byte[] CompositeFacadePalette(byte[] maskRgba, int w, int h, float[] pal, int pw, int ph, float palU, float palV)
        {
            // kPaletteSize = (256,8): sub-sample scale = invSize*0.5, offset = invSize*0.25.
            const float subScaleX = 0.5f / 256f, subOffX = 0.25f / 256f;
            const float subScaleY = 0.5f / 8f, subOffY = 0.25f / 8f;
            byte[] outp = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                float mr = maskRgba[i * 4] / 255f, mg = maskRgba[i * 4 + 1] / 255f, mb = maskRgba[i * 4 + 2] / 255f;
                float su = palU + mr * subScaleX + subOffX;
                float sv = palV + mg * subScaleY + subOffY;
                float[] c = SamplePaletteBilinear(pal, pw, ph, su, sv);
                float bright = mb * 2f;
                outp[i * 4]     = ToByteClamp(c[0] * bright);
                outp[i * 4 + 1] = ToByteClamp(c[1] * bright);
                outp[i * 4 + 2] = ToByteClamp(c[2] * bright);
                outp[i * 4 + 3] = maskRgba[i * 4 + 3];
            }
            return outp;
        }

        private static byte ToByteClamp(float v)
        {
            int x = (int)(v * 255f + 0.5f);
            return (byte)(x < 0 ? 0 : (x > 255 ? 255 : x));
        }

        /// <summary>Renders an HDR float palette to a PNG (clamped), nearest-upscaled by
        /// <paramref name="scale"/> so a tiny W x4 strip is viewable.</summary>
        private static byte[] RenderPalettePng(float[] pal, int w, int h, int scale)
        {
            int ow = w * scale, oh = h * scale;
            byte[] rgba = new byte[ow * oh * 4];
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++)
                {
                    int sx = x / scale, sy = y / scale, si = (sy * w + sx) * 4, di = (y * ow + x) * 4;
                    rgba[di] = ToByteClamp(pal[si]); rgba[di + 1] = ToByteClamp(pal[si + 1]);
                    rgba[di + 2] = ToByteClamp(pal[si + 2]); rgba[di + 3] = 255;
                }
            return GltfConverter.RgbaToPng(rgba, ow, oh);
        }

        /// <summary>Average per-vertex COLOR r/g over the mesh, scaled to palette U (the building4
        /// VS does palU = color.r * (255/256)). COLOR.r selects the base palette column (palU),
        /// COLOR.g the top-layer column (palU2). Averaging is an approximation for meshes whose
        /// vertex colour varies; it is exact for the (common) uniform-colour facade mesh.</summary>
        private static void ComputeAveragePalette(RW4Mesh mesh, out float palU, out float palU2)
        {
            palU = 0; palU2 = 0;
            double sr = 0, sg = 0; int n = 0;
            if (mesh != null && mesh.vertices != null && mesh.vertices.vertices != null)
            {
                foreach (Vertex v in mesh.vertices.vertices)
                {
                    if (v.VertexComponents == null) continue;
                    foreach (var comp in v.VertexComponents)
                    {
                        if (comp.Usage != D3DDECLUSAGE.D3DDECLUSAGE_COLOR) continue;
                        var col = comp as VertexD3DColorValue;
                        if (col != null) { sr += col.R / 255.0; sg += col.G / 255.0; n++; }
                        break;
                    }
                }
            }
            const float k = 255f / 256f;
            if (n > 0) { palU = (float)(sr / n) * k; palU2 = (float)(sg / n) * k; }
        }

        /// <summary>Reads an integer value following <paramref name="name"/> in the args
        /// (e.g. <c>--variation 3</c>); returns <paramref name="def"/> if absent/unparseable.</summary>
        private static int GetIntArg(string[] args, string name, int def)
        {
            if (args == null) return def;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                { int v; if (int.TryParse(args[i + 1], out v)) return v; }
            return def;
        }

        /// <summary>Maps a region/zone mask through the palette luminance (mask RED channel is a
        /// 1-D coordinate, linearly sampled) to a clean greyscale facade. Keeps the mask alpha.</summary>
        private static byte[] CompositeFacadeLuminance(byte[] maskRgba, int w, int h, byte[] lum, int palW)
        {
            byte[] outp = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                float u = maskRgba[i * 4] / 255f * (palW - 1);
                int c0 = (int)u; if (c0 < 0) c0 = 0; if (c0 > palW - 1) c0 = palW - 1;
                int c1 = c0 + 1 < palW ? c0 + 1 : c0; float t = u - c0;
                byte g = (byte)(lum[c0] + (lum[c1] - lum[c0]) * t);
                outp[i * 4] = g; outp[i * 4 + 1] = g; outp[i * 4 + 2] = g; outp[i * 4 + 3] = maskRgba[i * 4 + 3];
            }
            return outp;
        }

        /// <summary>Decodes a texture resource (RW4-wrapped DXT texture or a raw raster
        /// image, the two SimCity texture containers) to RGBA. Returns null if unsupported.</summary>
        private static byte[] DecodeTextureResourceRgba(DatabaseIndex tdi, out int w, out int h)
        {
            w = 0; h = 0;
            byte[] data = tdi.GetIndexData(true);
            if (tdi.TypeId == TEX_RASTER_TYPE_ID)
            {
                // raster header: 6 big-endian uints, then per mip [u32 blockSize][RGBA bytes].
                if (data.Length < 28) return null;
                uint rw = BE(data, 4), rh = BE(data, 8), pixFmt = BE(data, 20);
                if (pixFmt != 21) return null;               // only raw RGBA rasters are headless-decodable
                uint blockSize = BE(data, 24);
                if (rw == 0 || rh == 0 || 28L + blockSize > data.Length) return null;
                w = (int)rw; h = (int)rh;
                byte[] rgba = new byte[w * h * 4];
                Array.Copy(data, 28, rgba, 0, Math.Min(rgba.Length, (int)blockSize));   // stored R,G,B,A
                return rgba;
            }
            // RW4-wrapped texture: find its Texture section and DXT-decode it.
            var model = new RW4Model();
            using (var ms = new MemoryStream(data)) model.Read(ms);
            var texSec = model.Sections.FirstOrDefault(x => x.TypeCode == SectionTypeCodes.Texture && x.obj is Texture);
            if (texSec == null) return null;
            Texture tx = (Texture)texSec.obj;
            if (tx.textureType == 21)   // raw bitmap (e.g. the 512x16 slot-4 tint palette): blob is w*h*4 RGBA
            {
                byte[] blob = tx.texData != null ? tx.texData.blob : null;
                if (blob == null || tx.width <= 0 || tx.height <= 0) return null;
                w = tx.width; h = tx.height;
                byte[] rraw = new byte[w * h * 4];
                Array.Copy(blob, 0, rraw, 0, Math.Min(rraw.Length, blob.Length));
                return rraw;
            }
            using (var bmp = GltfConverter.DecodeTextureToBitmap(tx))
            {
                if (bmp == null) return null;
                w = bmp.Width; h = bmp.Height;
                return BitmapToRgba(bmp);
            }
        }

        private static byte[] BitmapToRgba(System.Drawing.Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            byte[] bgra = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, bgra, 0, bgra.Length);
            bmp.UnlockBits(bd);
            byte[] rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                rgba[i * 4 + 0] = bgra[i * 4 + 2];
                rgba[i * 4 + 1] = bgra[i * 4 + 1];
                rgba[i * 4 + 2] = bgra[i * 4 + 0];
                rgba[i * 4 + 3] = bgra[i * 4 + 3];
            }
            return rgba;
        }

        private static uint BE(byte[] b, int o) { return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]); }

        /// <summary>
        /// Parses RW4 bytes and writes each Mesh section via <paramref name="exporter"/>.
        /// Returns the number of files written.
        /// </summary>
        private static int ExportRw4Bytes(byte[] data, string outDir, string baseName,
            string ext, Action<RW4Mesh, string> exporter)
        {
            RW4Model model = new RW4Model();
            using (var ms = new MemoryStream(data))
                model.Read(ms);

            var meshSections = model.Sections
                .Where(s => s.TypeCode == SectionTypeCodes.Mesh && s.obj is RW4Mesh
                            && ((RW4Mesh)s.obj).vertices != null
                            && ((RW4Mesh)s.obj).vertices.vertices != null
                            && ((RW4Mesh)s.obj).triangles != null
                            && ((RW4Mesh)s.obj).triangles.triangles != null)
                .ToList();

            int written = 0;
            for (int i = 0; i < meshSections.Count; i++)
            {
                RW4Mesh mesh = (RW4Mesh)meshSections[i].obj;
                string suffix = meshSections.Count > 1 ? ("_mesh" + i) : "";
                string outPath = Path.Combine(outDir, baseName + suffix + ext);
                exporter(mesh, outPath);
                written++;
                Console.WriteLine("OK   " + Path.GetFileName(outPath));
            }
            return written;
        }

        // ----- localized names ----------------------------------------------

        // Prop hashes that hold a resource's display-name (an ArrayProperty whose
        // first element is a TextProperty -> locale table/id). From the GUI's
        // MainWindow.mnuInstanceIds_Click.
        private static readonly uint[] NameHashes =
            { 0x09FB78CB, 0x0A09F5FA, 0x09B711C3, 0x0E28B5BC, 0x0E28B5D5 };

        /// <summary>Locale package path: --locale &lt;path&gt; if given, else the
        /// configured Settings.LocaleFile, else null.</summary>
        private static string GetLocaleFile(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("--locale", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            try
            {
                string s = Properties.Settings.Default.LocaleFile;
                if (!string.IsNullOrEmpty(s) && File.Exists(s)) return s;
            }
            catch { }
            // Fall back to auto-detecting the game's locale package next to the input package.
            try
            {
                if (args.Length > 1 && !string.IsNullOrEmpty(args[1]))
                {
                    string inp = args[1];
                    string dir = File.Exists(inp) ? Path.GetDirectoryName(Path.GetFullPath(inp))
                               : (Directory.Exists(inp) ? Path.GetFullPath(inp) : null);
                    if (dir != null) { string d = AutoDetectLocaleFile(new[] { dir }); if (d != null) return d; }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Tries to locate the game's localized-name package automatically by looking for
        /// <c>Locale\&lt;lang&gt;\Data.package</c> under each given directory (and its parent) —
        /// SimCity ships it beside the data packages, e.g.
        /// <c>SimCityData\Locale\en-us\Data.package</c>. Prefers en-us, then any English locale,
        /// then the first one found. Returns the path or null. Used so the GUI "Export all" names
        /// assets even when no locale is set in Settings.</summary>
        public static string AutoDetectLocaleFile(IEnumerable<string> searchDirs)
        {
            if (searchDirs == null) return null;
            // Search each directory and its parent (packages may sit one level from the Locale root).
            var dirs = new List<string>();
            foreach (string d in searchDirs)
            {
                if (string.IsNullOrEmpty(d)) continue;
                dirs.Add(d);
                try { var parent = Directory.GetParent(d); if (parent != null) dirs.Add(parent.FullName); } catch { }
            }
            foreach (string dir in dirs)
            {
                try
                {
                    string localeRoot = Path.Combine(dir, "Locale");
                    if (!Directory.Exists(localeRoot)) continue;
                    string enus = Path.Combine(localeRoot, "en-us", "Data.package");
                    if (File.Exists(enus)) return enus;
                    // any English locale next
                    foreach (string sub in Directory.GetDirectories(localeRoot))
                    {
                        string data = Path.Combine(sub, "Data.package");
                        if (File.Exists(data) && Path.GetFileName(sub).StartsWith("en", StringComparison.OrdinalIgnoreCase))
                            return data;
                    }
                    // else the first locale that has a Data.package
                    foreach (string sub in Directory.GetDirectories(localeRoot))
                    {
                        string data = Path.Combine(sub, "Data.package");
                        if (File.Exists(data)) return data;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Builds instanceId -> localized display-name by scanning the package's prop
        /// files: each prop's name property resolves to a locale string, which is mapped
        /// to the prop's own instance and to every model (type 0x2f4e681b) it references
        /// via Key properties. Empty map if no locale / no names. Never throws.
        /// </summary>
        private static Dictionary<uint, string> BuildLocaleNameMap(IEnumerable<DatabaseIndex> indices, string localeFile)
        {
            var map = new Dictionary<uint, string>();
            if (string.IsNullOrEmpty(localeFile) || !File.Exists(localeFile)) return map;
            LocaleRegistry locale;
            try
            {
                Properties.Settings.Default.LocaleFile = localeFile;   // in-memory only (no Save)
                locale = LocaleRegistry.Create();
            }
            catch { return map; }

            // Parse every prop once: its name (if any), the RW4 models it references, and its
            // "Model Details" target instance (a catalog prop's link to the model/gameplay asset).
            var byInstance = new Dictionary<uint, List<PropRec>>();
            var recs = new List<PropRec>();
            foreach (DatabaseIndex index in indices)
            {
                if (index.TypeId != PROP_TYPE_ID) continue;
                PropertyFile pf;
                try { pf = new PropertyFile(index); }
                catch { continue; }

                var rec = new PropRec { Instance = index.InstanceId, ModelRefs = new List<uint>() };
                foreach (uint h in NameHashes)
                {
                    Property prop;
                    if (!pf.Values.TryGetValue(h, out prop)) continue;
                    ArrayProperty arr = prop as ArrayProperty;
                    if (arr == null || arr.Values.Count == 0) continue;
                    TextProperty tp = arr.Values[0] as TextProperty;
                    if (tp == null) continue;
                    string s = locale.GetLocalizedString(tp.TableId, tp.InstanceId);
                    if (!string.IsNullOrEmpty(s)) { rec.Name = s; break; }
                }
                foreach (var kv in pf.Values) CollectModelRefs(kv.Value, rec.ModelRefs);
                Property md;
                if (pf.Values.TryGetValue(MODEL_DETAILS_HASH, out md))
                {
                    KeyProperty kp = md as KeyProperty;
                    if (kp != null) rec.ModelDetails = kp.InstanceId;
                }
                recs.Add(rec);
                List<PropRec> list;
                if (!byInstance.TryGetValue(rec.Instance, out list)) { list = new List<PropRec>(); byInstance[rec.Instance] = list; }
                list.Add(rec);
            }

            // Assign each name to: the named prop's own instance and model refs, plus the model
            // refs of every prop sharing that instance or sitting at its Model-Details target
            // (so a building's catalog name reaches the models its unnamed model-prop references).
            Action<uint, string> nameInstanceModels = (inst, name) =>
            {
                List<PropRec> list;
                if (!byInstance.TryGetValue(inst, out list)) return;
                foreach (PropRec q in list)
                    foreach (uint mref in q.ModelRefs)
                        if (!map.ContainsKey(mref)) map[mref] = name;
            };
            foreach (PropRec r in recs)
            {
                if (string.IsNullOrEmpty(r.Name)) continue;
                if (!map.ContainsKey(r.Instance)) map[r.Instance] = r.Name;
                foreach (uint mref in r.ModelRefs) if (!map.ContainsKey(mref)) map[mref] = r.Name;
                nameInstanceModels(r.Instance, r.Name);
                if (r.ModelDetails != 0) nameInstanceModels(r.ModelDetails, r.Name);
            }
            return map;
        }

        private class PropRec
        {
            public uint Instance;
            public string Name;
            public List<uint> ModelRefs;
            public uint ModelDetails;
        }

        /// <summary>Collects the instance ids of every RW4 model (type id 0x2f4e681b) referenced
        /// by a property, recursing into arrays.</summary>
        private static void CollectModelRefs(Property p, List<uint> refs)
        {
            KeyProperty kp = p as KeyProperty;
            if (kp != null)
            {
                if (kp.TypeId == RW4_MODEL_TYPE_ID) refs.Add(kp.InstanceId);
                return;
            }
            ArrayProperty arr = p as ArrayProperty;
            if (arr != null)
                foreach (Property sub in arr.Values) CollectModelRefs(sub, refs);
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim();
            return string.IsNullOrEmpty(name) ? "unnamed" : name;
        }

        /// <summary>Localized base file name for a resource, unique within this run.</summary>
        private static string ResolveBaseName(uint instanceId, string fallback,
            Dictionary<uint, string> nameMap, HashSet<string> used)
        {
            string baseName = fallback;
            string nm;
            if (nameMap != null && nameMap.TryGetValue(instanceId, out nm))
                baseName = Sanitize(nm);

            string candidate = baseName;
            if (used.Contains(candidate.ToLowerInvariant()))
                candidate = baseName + "_" + instanceId.ToString("x8");
            int n = 2;
            while (used.Contains(candidate.ToLowerInvariant()))
                candidate = baseName + "_" + instanceId.ToString("x8") + "_" + (n++);
            used.Add(candidate.ToLowerInvariant());
            return candidate;
        }

        // ----- export-texture -----------------------------------------------

        private static int RunExportTextures(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: SimCityPak.exe export-texture <input> <outputDir> [--format png|jpg|tga|dds]");
                return 2;
            }
            string input = args[1];
            string outDir = args[2];
            Directory.CreateDirectory(outDir);
            string format = GetTextureFormat(args);

            int res = 0, textures = 0, fails = 0;

            Action<byte[], string> handle = (data, baseName) =>
            {
                try
                {
                    int t = ExportRw4Textures(data, outDir, baseName, format);
                    if (t > 0) { res++; textures += t; }
                    else Console.WriteLine("SKIP " + baseName + " (no texture)");
                }
                catch (Exception ex) { fails++; Console.WriteLine("FAIL " + baseName + " :: " + ex.Message); }
            };

            if (Directory.Exists(input))
            {
                foreach (string file in Directory.GetFiles(input, "*.rw4"))
                    handle(File.ReadAllBytes(file), Path.GetFileNameWithoutExtension(file));
            }
            else if (input.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            {
                DatabasePackedFile package = DatabasePackedFile.LoadFromFile(input);
                var nameMap = BuildLocaleNameMap(package.Indices, GetLocaleFile(args));
                if (nameMap.Count > 0) Console.WriteLine("(using " + nameMap.Count + " localized names from the locale file)");
                var used = new HashSet<string>();
                foreach (DatabaseIndex index in package.Indices)
                {
                    if (index.TypeId != RW4_MODEL_TYPE_ID) continue;
                    string fallback = string.Format("{0:x8}-{1:x8}-{2:x8}",
                        index.TypeId, index.GroupContainer, index.InstanceId);
                    handle(index.GetIndexData(true), ResolveBaseName(index.InstanceId, fallback, nameMap, used));
                }
            }
            else if (File.Exists(input))
            {
                handle(File.ReadAllBytes(input), Path.GetFileNameWithoutExtension(input));
            }
            else
            {
                Console.WriteLine("ERROR: input not found: " + input);
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("Done. resources={0} textures({1})={2} failed={3}", res, format, textures, fails));
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return fails > 0 ? 1 : 0;
        }

        /// <summary>Parses RW4 bytes and writes each Texture section as a .dds.</summary>
        /// <summary>Texture output format from --format (png|jpg|tga|dds); default png.</summary>
        private static string GetTextureFormat(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("--format", StringComparison.OrdinalIgnoreCase))
                {
                    string fmt = args[i + 1].ToLowerInvariant().TrimStart('.');
                    if (fmt == "jpeg") fmt = "jpg";
                    if (fmt == "png" || fmt == "jpg" || fmt == "tga" || fmt == "dds") return fmt;
                }
            return "png";
        }

        private static void SaveBitmap(System.Drawing.Bitmap bmp, string path, string format)
        {
            if (format == "jpg") bmp.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
            else if (format == "tga") SaveTga(bmp, path);
            else bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        private static void SaveTga(System.Drawing.Bitmap bmp, string path)
        {
            int w = bmp.Width, h = bmp.Height;
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            byte[] bgra = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, bgra, 0, bgra.Length);
            bmp.UnlockBits(bd);
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)2);     // uncompressed true-color
                bw.Write((short)0); bw.Write((short)0); bw.Write((byte)0);   // color map spec
                bw.Write((short)0); bw.Write((short)0);                      // x/y origin
                bw.Write((short)w); bw.Write((short)h);
                bw.Write((byte)32);                                         // 32 bpp (BGRA)
                bw.Write((byte)0x20);                                       // top-left origin, 8 alpha bits
                bw.Write(bgra);                                            // BGRA, top-down
            }
        }

        private static int ExportRw4Textures(byte[] data, string outDir, string baseName, string format)
        {
            RW4Model model = new RW4Model();
            using (var ms = new MemoryStream(data))
                model.Read(ms);

            var texSections = model.Sections
                .Where(s => s.TypeCode == SectionTypeCodes.Texture && s.obj is Texture)
                .ToList();

            int written = 0;
            for (int i = 0; i < texSections.Count; i++)
            {
                Texture tex = (Texture)texSections[i].obj;
                string suffix = texSections.Count > 1 ? ("_tex" + i) : "";
                string outPath = Path.Combine(outDir, baseName + suffix + "." + format);
                try
                {
                    if (format == "dds")
                    {
                        tex.SaveDds(outPath);
                    }
                    else
                    {
                        using (System.Drawing.Bitmap bmp = GltfConverter.DecodeTextureToBitmap(tex))
                        {
                            if (bmp == null)
                            {
                                Console.WriteLine("SKIP " + baseName + suffix + " (unsupported texture format)");
                                continue;
                            }
                            SaveBitmap(bmp, outPath, format);
                        }
                    }
                    written++;
                    Console.WriteLine("OK   " + Path.GetFileName(outPath));
                }
                catch (NotSupportedException)
                {
                    // e.g. raw-bitmap (textureType 21) textures: skip, don't fail the model.
                    Console.WriteLine("SKIP " + baseName + suffix + " (unsupported texture format)");
                }
            }
            return written;
        }

        // ----- export-prop --------------------------------------------------

        private static int RunExportProp(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: SimCityPak.exe export-prop <input> <outputDir> [--json] [--locale <pkg>]");
                return 2;
            }
            string input = args[1];
            string outDir = args[2];
            Directory.CreateDirectory(outDir);

            bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
            string ext = json ? ".json" : ".txt";

            // --combine: one file per asset, merging the model + gameplay + catalog
            // props that belong together (package input only).
            if (args.Any(a => a.Equals("--combine", StringComparison.OrdinalIgnoreCase))
                && input.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            {
                return RunExportPropCombined(input, outDir, GetLocaleFile(args), json);
            }

            int ok = 0, fails = 0;

            if (Directory.Exists(input))
            {
                foreach (string file in Directory.GetFiles(input, "*.prop"))
                {
                    string baseName = Path.GetFileNameWithoutExtension(file);
                    try
                    {
                        var pf = new PropertyFile();
                        using (var ms = new MemoryStream(File.ReadAllBytes(file))) pf.Read(ms);
                        DumpPropertyFile(pf, Path.Combine(outDir, baseName + ext), baseName, json);
                        ok++; Console.WriteLine("OK   " + baseName + ext);
                    }
                    catch (Exception ex) { fails++; Console.WriteLine("FAIL " + baseName + " :: " + ex.Message); }
                }
            }
            else if (input.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            {
                DatabasePackedFile package = DatabasePackedFile.LoadFromFile(input);
                var nameMap = BuildLocaleNameMap(package.Indices, GetLocaleFile(args));
                if (nameMap.Count > 0) Console.WriteLine("(using " + nameMap.Count + " localized names from the locale file)");
                var used = new HashSet<string>();
                foreach (DatabaseIndex index in package.Indices)
                {
                    if (index.TypeId != PROP_TYPE_ID) continue;
                    string fallback = string.Format("{0:x8}-{1:x8}-{2:x8}",
                        index.TypeId, index.GroupContainer, index.InstanceId);
                    string baseName = ResolveBaseName(index.InstanceId, fallback, nameMap, used);
                    try
                    {
                        var pf = new PropertyFile(index);
                        DumpPropertyFile(pf, Path.Combine(outDir, baseName + ext), baseName, json);
                        ok++; Console.WriteLine("OK   " + baseName + ext);
                    }
                    catch (Exception ex) { fails++; Console.WriteLine("FAIL " + baseName + " :: " + ex.Message); }
                }
            }
            else if (File.Exists(input))
            {
                string baseName = Path.GetFileNameWithoutExtension(input);
                try
                {
                    var pf = new PropertyFile();
                    using (var ms = new MemoryStream(File.ReadAllBytes(input))) pf.Read(ms);
                    DumpPropertyFile(pf, Path.Combine(outDir, baseName + ext), baseName, json);
                    ok++; Console.WriteLine("OK   " + baseName + ext);
                }
                catch (Exception ex) { fails++; Console.WriteLine("FAIL " + baseName + " :: " + ex.Message); }
            }
            else
            {
                Console.WriteLine("ERROR: input not found: " + input);
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("Done. dumped={0} failed={1}", ok, fails));
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return fails > 0 ? 1 : 0;
        }

        /// <summary>Readable name of a property hash from SimCityPak's descriptor DB
        /// (database_main.s3db), or null if unknown.</summary>
        private static string PropName(uint hash)
        {
            try
            {
                TGIRecord rec;
                if (TGIRegistry.Instance.Properties.Cache.TryGetValue(hash, out rec))
                {
                    if (!string.IsNullOrEmpty(rec.Comments)) return rec.Comments;
                    if (!string.IsNullOrEmpty(rec.DisplayName)) return rec.DisplayName;
                }
            }
            catch { }
            return null;
        }

        private static string PropTypeName(Property p)
        {
            if (p == null) return "null";
            string t = p.GetType().Name;
            return t.EndsWith("Property") ? t.Substring(0, t.Length - 8) : t;
        }

        private static string PropValue(Property p)
        {
            try { return p == null ? "" : p.DisplayValue; }
            catch (Exception ex) { return "<error: " + ex.Message + ">"; }
        }

        /// <summary>
        /// CLI wrapper for --combine: loads the package and delegates to
        /// <see cref="ExportCombinedPropsToFolder"/>, printing the summary.
        /// </summary>
        private static int RunExportPropCombined(string input, string outDir, string localeFile, bool json)
        {
            DatabasePackedFile package = DatabasePackedFile.LoadFromFile(input);
            string summary = ExportCombinedPropsToFolder(package.Indices, outDir, localeFile, json);
            Console.WriteLine();
            Console.WriteLine(summary);
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return 0;
        }

        /// <summary>
        /// --combine core: groups prop resources into assets (props sharing an InstanceId belong
        /// together; a catalog prop's "Model Details" reference is unioned with the instance it
        /// points to) and writes ONE file per asset, merging the model, gameplay and catalog
        /// props. Named by the asset's localized name when known. Public so the GUI reuses it.
        /// Returns a human-readable summary.
        /// </summary>
        public static string ExportCombinedPropsToFolder(IEnumerable<DatabaseIndex> indices, string outDir, string localeFile, bool json)
        {
            Directory.CreateDirectory(outDir);
            Logger.Info("Export combined props -> " + outDir + (string.IsNullOrEmpty(localeFile) ? "" : " (locale: " + localeFile + ")"));
            var nameMap = BuildLocaleNameMap(indices, localeFile);

            var props = new List<KeyValuePair<DatabaseIndex, PropertyFile>>();
            foreach (DatabaseIndex index in indices)
            {
                if (index.TypeId != PROP_TYPE_ID) continue;
                try { props.Add(new KeyValuePair<DatabaseIndex, PropertyFile>(index, new PropertyFile(index))); }
                catch (Exception ex) { Logger.Exception("combine read prop", ex); }
            }
            int n = props.Count;

            // Union-Find over the props.
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            Func<int, int> find = null;
            find = x => { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
            Action<int, int> union = (a, b) => { int ra = find(a), rb = find(b); if (ra != rb) parent[ra] = rb; };

            var firstByInstance = new Dictionary<uint, int>();
            for (int i = 0; i < n; i++)
            {
                uint inst = props[i].Key.InstanceId;
                int other;
                if (firstByInstance.TryGetValue(inst, out other)) union(i, other);  // same asset instance
                else firstByInstance[inst] = i;
            }
            for (int i = 0; i < n; i++)
            {
                Property md;
                if (props[i].Value.Values.TryGetValue(MODEL_DETAILS_HASH, out md))
                {
                    KeyProperty kp = md as KeyProperty;
                    int target;
                    if (kp != null && firstByInstance.TryGetValue(kp.InstanceId, out target)) union(i, target);
                }
            }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = find(i);
                List<int> g;
                if (!groups.TryGetValue(r, out g)) { g = new List<int>(); groups[r] = g; }
                g.Add(i);
            }

            var used = new HashSet<string>();
            int ok = 0, fails = 0;
            foreach (var g in groups.Values)
            {
                // sort facets by group container for stable output
                g.Sort((a, b) => props[a].Key.GroupContainer.CompareTo(props[b].Key.GroupContainer));
                string name = null;
                uint assetInst = props[g[0]].Key.InstanceId;
                foreach (int i in g)
                {
                    string nm;
                    if (nameMap.TryGetValue(props[i].Key.InstanceId, out nm)) { name = nm; break; }
                }
                string baseName = UniqueName(name, string.Format("{0:x8}", assetInst), used);
                string outPath = Path.Combine(outDir, baseName + (json ? ".json" : ".txt"));
                try
                {
                    var members = new List<KeyValuePair<DatabaseIndex, PropertyFile>>();
                    foreach (int i in g) members.Add(props[i]);
                    DumpCombined(members, outPath, name ?? baseName, json);
                    ok++; Logger.Info("combine OK " + Path.GetFileName(outPath) + " (" + g.Count + " props)");
                }
                catch (Exception ex) { fails++; Logger.Exception("combine dump " + baseName, ex); }
            }

            string summary = string.Format(
                "Combined {0} props into {1} asset files ({2} failed).\nLocalized names: {3}.",
                n, ok, fails, nameMap.Count);
            Logger.Info(summary.Replace(Environment.NewLine, " "));
            return summary;
        }

        private static string UniqueName(string name, string fallback, HashSet<string> used)
        {
            string baseName = Sanitize(string.IsNullOrEmpty(name) ? fallback : name);
            string candidate = baseName;
            int k = 2;
            while (used.Contains(candidate.ToLowerInvariant())) candidate = baseName + "_" + (k++);
            used.Add(candidate.ToLowerInvariant());
            return candidate;
        }

        /// <summary>Writes several property files into one combined dump (one section per
        /// resource), with resolved names and invariant-culture numbers.</summary>
        private static void DumpCombined(List<KeyValuePair<DatabaseIndex, PropertyFile>> members,
            string outPath, string name, bool json)
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                if (json)
                {
                    var resources = new List<object>();
                    foreach (var m in members)
                    {
                        var keys = new List<uint>(m.Value.Values.Keys); keys.Sort();
                        var pl = new List<object>();
                        foreach (uint hash in keys)
                        {
                            Property p = m.Value.Values[hash];
                            pl.Add(new Dictionary<string, object>
                            {
                                ["name"] = PropName(hash),
                                ["hash"] = "0x" + hash.ToString("x8"),
                                ["type"] = PropTypeName(p),
                                ["value"] = PropValue(p)
                            });
                        }
                        resources.Add(new Dictionary<string, object>
                        {
                            ["type"] = "0x" + m.Key.TypeId.ToString("x8"),
                            ["group"] = "0x" + m.Key.GroupContainer.ToString("x8"),
                            ["instance"] = "0x" + m.Key.InstanceId.ToString("x8"),
                            ["propertyCount"] = m.Value.Values.Count,
                            ["properties"] = pl
                        });
                    }
                    var root = new Dictionary<string, object> { ["name"] = name, ["resourceCount"] = members.Count, ["resources"] = resources };
                    File.WriteAllText(outPath, JsonConvert.SerializeObject(root, Formatting.Indented));
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("# SimCityPak combined property dump: " + name);
                sb.AppendLine("# resources: " + members.Count);
                sb.AppendLine();
                foreach (var m in members)
                {
                    sb.AppendFormat("== T:0x{0:x8} G:0x{1:x8} I:0x{2:x8}  ({3} properties) ==\r\n",
                        m.Key.TypeId, m.Key.GroupContainer, m.Key.InstanceId, m.Value.Values.Count);
                    var keys = new List<uint>(m.Value.Values.Keys); keys.Sort();
                    foreach (uint hash in keys)
                    {
                        Property p = m.Value.Values[hash];
                        string pname = PropName(hash);
                        string label = pname != null ? string.Format("{0} [0x{1:x8}]", pname, hash) : string.Format("0x{0:x8}", hash);
                        sb.AppendFormat("{0,-44} {1,-12} = {2}\r\n", label, PropTypeName(p), PropValue(p));
                    }
                    sb.AppendLine();
                }
                File.WriteAllText(outPath, sb.ToString());
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
        }

        /// <summary>Writes a property file as readable text or JSON, resolving property
        /// names where SimCityPak knows them, with invariant-culture numbers ('.' decimals).
        /// Public so the GUI can reuse the exact same dump. Sorted by hash.</summary>
        public static void DumpPropertyFile(PropertyFile pf, string outPath, string name, bool json)
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            try { DumpProp(pf, outPath, name, json); }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
        }

        /// <summary>Writes a property file as readable text or JSON, resolving property
        /// names where SimCityPak knows them. Sorted by hash.</summary>
        private static void DumpProp(PropertyFile pf, string outPath, string name, bool json)
        {
            var keys = new List<uint>(pf.Values.Keys);
            keys.Sort();

            if (json)
            {
                var props = new List<object>();
                foreach (uint hash in keys)
                {
                    Property p = pf.Values[hash];
                    props.Add(new Dictionary<string, object>
                    {
                        ["name"] = PropName(hash),                       // null when unknown
                        ["hash"] = "0x" + hash.ToString("x8"),
                        ["type"] = PropTypeName(p),
                        ["value"] = PropValue(p)
                    });
                }
                var root = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["propertyCount"] = pf.Values.Count,
                    ["properties"] = props
                };
                File.WriteAllText(outPath, JsonConvert.SerializeObject(root, Formatting.Indented));
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("# SimCityPak property dump: " + name);
            sb.AppendLine("# properties: " + pf.Values.Count);
            sb.AppendLine();
            foreach (uint hash in keys)
            {
                Property p = pf.Values[hash];
                string pname = PropName(hash);
                string label = pname != null
                    ? string.Format("{0} [0x{1:x8}]", pname, hash)
                    : string.Format("0x{0:x8}", hash);
                sb.AppendFormat("{0,-44} {1,-12} = {2}\r\n", label, PropTypeName(p), PropValue(p));
            }
            File.WriteAllText(outPath, sb.ToString());
        }

        // ----- export-all (models -> glTF + textures -> images) -------------

        private static int RunExportAll(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: SimCityPak.exe export-all <input> <outputDir> [--format png|jpg|tga|dds] [--locale <pkg>]");
                return 2;
            }
            string input = args[1];
            string outDir = args[2];
            Directory.CreateDirectory(outDir);
            string format = GetTextureFormat(args);
            var texLookup = BuildTextureLookupForInput(input, args);

            int glb = 0, tex = 0, fails = 0;
            Action<byte[], string> handle = (data, baseName) =>
            {
                try { glb += ExportRw4Bytes(data, outDir, baseName, ".glb", (m, p) => new GltfConverter(mesh => ResolveTextures(mesh, texLookup), mesh => ResolvePerVertexColor(mesh, texLookup)).Export(m, p)); }
                catch (Exception ex) { fails++; Logger.Exception("export-all model " + baseName, ex); Console.WriteLine("FAIL " + baseName + " (model) :: " + ex.Message); }
                try { tex += ExportRw4Textures(data, outDir, baseName, format); }
                catch (Exception ex) { fails++; Logger.Exception("export-all textures " + baseName, ex); Console.WriteLine("FAIL " + baseName + " (textures) :: " + ex.Message); }
            };

            if (Directory.Exists(input))
            {
                foreach (string file in Directory.GetFiles(input, "*.rw4"))
                    handle(File.ReadAllBytes(file), Path.GetFileNameWithoutExtension(file));
            }
            else if (input.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            {
                DatabasePackedFile package = DatabasePackedFile.LoadFromFile(input);
                var nameMap = BuildLocaleNameMap(package.Indices, GetLocaleFile(args));
                if (nameMap.Count > 0) Console.WriteLine("(using " + nameMap.Count + " localized names from the locale file)");
                var used = new HashSet<string>();
                foreach (DatabaseIndex index in package.Indices)
                {
                    if (index.TypeId != RW4_MODEL_TYPE_ID) continue;
                    string fallback = string.Format("{0:x8}-{1:x8}-{2:x8}",
                        index.TypeId, index.GroupContainer, index.InstanceId);
                    string baseName = ResolveBaseName(index.InstanceId, fallback, nameMap, used);
                    handle(index.GetIndexData(true), baseName);
                }
            }
            else if (File.Exists(input))
            {
                handle(File.ReadAllBytes(input), Path.GetFileNameWithoutExtension(input));
            }
            else
            {
                Console.WriteLine("ERROR: input not found: " + input);
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("Done. models(glb)={0} textures({1})={2} failed={3}", glb, format, tex, fails));
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return fails > 0 ? 1 : 0;
        }

        /// <summary>
        /// Exports every model (-&gt; .glb) and every texture (-&gt; .dds) from the given
        /// resources into one folder, naming files by localized asset name when
        /// <paramref name="localeFile"/> is provided. Returns a short summary; used by
        /// the GUI "Export all" menu. Never throws (errors are logged per resource).
        /// </summary>
        public static string ExportAllToFolder(IEnumerable<DatabaseIndex> indices, string outDir, string localeFile, string format = "png", IEnumerable<string> textureSearchDirs = null)
        {
            Directory.CreateDirectory(outDir);
            Logger.Info("Export-all -> " + outDir + " (textures: " + format + ")" + (string.IsNullOrEmpty(localeFile) ? "" : " (locale: " + localeFile + ")"));
            var nameMap = BuildLocaleNameMap(indices, localeFile);
            // Resolve model materials against every texture in the supplied resources (the loaded
            // packages). When textureSearchDirs is given, ALSO index the sibling .package files on
            // disk (e.g. SimCity_Graphics.package) so textures resolve even if the user only opened
            // one package — the loaded resources keep priority (so edits still win).
            var texLookup = BuildTextureLookup(indices);
            if (textureSearchDirs != null) AddSiblingTextures(texLookup, textureSearchDirs);
            var used = new HashSet<string>();
            int glb = 0, tex = 0, fails = 0, models = 0, named = 0;
            foreach (DatabaseIndex index in indices)
            {
                if (index.TypeId != RW4_MODEL_TYPE_ID) continue;
                models++;
                if (nameMap.ContainsKey(index.InstanceId)) named++;
                string fallback = string.Format("{0:x8}-{1:x8}-{2:x8}",
                    index.TypeId, index.GroupContainer, index.InstanceId);
                string baseName = ResolveBaseName(index.InstanceId, fallback, nameMap, used);
                byte[] data;
                try { data = index.GetIndexData(true); }
                catch (Exception ex) { fails++; Logger.Exception("export-all read " + baseName, ex); continue; }
                try { glb += ExportRw4Bytes(data, outDir, baseName, ".glb", (m, p) => new GltfConverter(mesh => ResolveTextures(mesh, texLookup), mesh => ResolvePerVertexColor(mesh, texLookup)).Export(m, p)); }
                catch (Exception ex) { fails++; Logger.Exception("export-all model " + baseName, ex); }
                try { tex += ExportRw4Textures(data, outDir, baseName, format); }
                catch (Exception ex) { fails++; Logger.Exception("export-all textures " + baseName, ex); }
            }
            string summary = string.Format(
                "Exported {0} models (.glb) and {1} textures (.{2}); {3} failed.\n{4} of {5} models got a localized name (the rest keep their hash id).",
                glb, tex, format, fails, named, models);
            Logger.Info(summary.Replace(Environment.NewLine, " "));
            return summary;
        }

        // ----- export-audio -------------------------------------------------

        /// <summary>Path to the bundled vgmstream CLI (copied next to the exe).</summary>
        private static string VgmstreamExe()
        {
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Tools", "vgmstream", "vgmstream-cli.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException(
                    "Bundled vgmstream not found at: " + exe +
                    "\n(It should ship in Tools\\vgmstream next to SimCityPak.exe.)");
            return exe;
        }

        private static int RunExportAudio(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: SimCityPak.exe export-audio <input> <outputDir>");
                return 2;
            }
            string input = args[1];
            string outDir = args[2];
            Directory.CreateDirectory(outDir);
            string vgm = VgmstreamExe();

            int ok = 0, fails = 0;

            if (Directory.Exists(input))
            {
                foreach (string file in Directory.GetFiles(input, "*.wav"))
                {
                    string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".wav");
                    if (ConvertAudio(vgm, file, outPath)) ok++; else fails++;
                }
            }
            else if (input.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            {
                DatabasePackedFile package = DatabasePackedFile.LoadFromFile(input);
                foreach (DatabaseIndex index in package.Indices)
                {
                    if (index.TypeId != AUDIO_TYPE_ID) continue;
                    string baseName = string.Format("{0:x8}-{1:x8}-{2:x8}",
                        index.TypeId, index.GroupContainer, index.InstanceId);
                    string tmp = Path.Combine(Path.GetTempPath(), baseName + ".wav");
                    try
                    {
                        File.WriteAllBytes(tmp, index.GetIndexData(true));
                        string outPath = Path.Combine(outDir, baseName + ".wav");
                        if (ConvertAudio(vgm, tmp, outPath)) ok++; else fails++;
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                }
            }
            else if (File.Exists(input))
            {
                string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + ".wav");
                if (ConvertAudio(vgm, input, outPath)) ok++; else fails++;
            }
            else
            {
                Console.WriteLine("ERROR: input not found: " + input);
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("Done. converted={0} failed={1}", ok, fails));
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return fails > 0 ? 1 : 0;
        }

        /// <summary>Runs vgmstream to decode one audio file to a standard PCM .wav.</summary>
        private static bool ConvertAudio(string vgmExe, string inputPath, string outPath)
        {
            string name = Path.GetFileNameWithoutExtension(inputPath);
            var psi = new ProcessStartInfo
            {
                FileName = vgmExe,
                Arguments = string.Format("-o \"{0}\" \"{1}\"", outPath, inputPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                {
                    Console.WriteLine("OK   " + name + ".wav");
                    return true;
                }
                Console.WriteLine("FAIL " + name + " :: vgmstream exit " + p.ExitCode +
                    (string.IsNullOrWhiteSpace(err) ? "" : " :: " + err.Split('\n')[0].Trim()));
                return false;
            }
        }
    }
}
