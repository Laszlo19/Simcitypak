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

        // SimCity 2013 type id for an "EA VP60 Video File" resource. The raw bytes are
        // an EA "MVhd" container holding a VP6 stream; ffmpeg's EA demuxer reads them.
        private const uint VIDEO_TYPE_ID = 0x376840D7;

        // "Model Details (PROP)" property: a catalog prop's Key reference to the
        // model/gameplay asset instance it belongs to. Used by --combine.
        private const uint MODEL_DETAILS_HASH = 0x0975695f;

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
                case "export-video":
                case "export-all":
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

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "export-obj":
                        return RunExportModels(args, ".obj", (m, p) => m.Export(p));
                    case "export-gltf":
                    {
                        var texLookup = BuildTextureLookupForInput(args.Length > 1 ? args[1] : "", args);
                        return RunExportModels(args, ".glb", (m, p) => new GltfConverter(mesh => ResolveTextures(mesh, texLookup)).Export(m, p));
                    }
                    case "export-texture":
                        return RunExportTextures(args);
                    case "export-prop":
                        return RunExportProp(args);
                    case "export-all":
                        return RunExportAll(args);
                    case "export-audio":
                        return RunExportAudio(args);
                    case "export-video":
                        return RunExportVideo(args);
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
            Console.WriteLine("  SimCityPak.exe export-video   <input> <outputDir> [--mp4]");
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
            Console.WriteLine("  export-video   : EA VP60 video -> raw .vp6 (plays in ffmpeg-based players);");
            Console.WriteLine("                   add --mp4 to re-encode to .mp4 (needs ffmpeg on PATH)");
            Console.WriteLine("  export-all     : every model -> .glb AND every texture -> image, one folder");
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
        private static GltfConverter.MaterialTextures ResolveTextures(RW4Mesh mesh, Dictionary<uint, DatabaseIndex> lookup)
        {
            try
            {
                if (lookup == null || mesh == null || mesh.model == null) return null;

                byte[] baseRgba = null, fbRgba = null, normalRgba = null, palLum = null;
                int baseW = 0, baseH = 0, fbW = 0, fbH = 0, nW = 0, nH = 0, palW = 0;
                long baseArea = -1, fbArea = -1, nArea = -1;

                foreach (RW4Section s in mesh.model.Sections)
                {
                    RW4Material mat = s.obj as RW4Material;
                    if (mat == null || mat.Materials == null) continue;
                    foreach (MaterialTextureReference mr in mat.Materials)
                    {
                        DatabaseIndex tdi;
                        if (mr.TextureInstanceId == 0) continue;
                        if (!lookup.TryGetValue(mr.TextureInstanceId, out tdi)) continue;
                        // The per-model facade palette (float32 strip, textureType 116): keep its
                        // row-0 luminance for compositing rather than treating it as a colour map.
                        if (palLum == null)
                        {
                            int pw; byte[] lum = DecodePaletteLuminance(tdi, out pw);
                            if (lum != null) { palLum = lum; palW = pw; continue; }
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
                // whose RED channel indexes the per-model palette. The palette is a 4-row HDR
                // material table, not an RGB LUT, but its row 0 is an albedo *luminance*; map the
                // region atlas through it to get a clean grey facade (light walls, dark windows)
                // instead of the neon atlas. Full colour needs the GlassBox shader.
                if (colorRgba != null && palLum != null && palW > 1)
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
            using (var bmp = GltfConverter.DecodeTextureToBitmap((Texture)texSec.obj))
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
                try { glb += ExportRw4Bytes(data, outDir, baseName, ".glb", (m, p) => new GltfConverter(mesh => ResolveTextures(mesh, texLookup)).Export(m, p)); }
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
        public static string ExportAllToFolder(IEnumerable<DatabaseIndex> indices, string outDir, string localeFile, string format = "png")
        {
            Directory.CreateDirectory(outDir);
            Logger.Info("Export-all -> " + outDir + " (textures: " + format + ")" + (string.IsNullOrEmpty(localeFile) ? "" : " (locale: " + localeFile + ")"));
            var nameMap = BuildLocaleNameMap(indices, localeFile);
            // Resolve model materials against every texture in the supplied resources
            // (for the GUI that's all loaded packages, so shared graphics textures apply).
            var texLookup = BuildTextureLookup(indices);
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
                try { glb += ExportRw4Bytes(data, outDir, baseName, ".glb", (m, p) => new GltfConverter(mesh => ResolveTextures(mesh, texLookup)).Export(m, p)); }
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

        // ----- export-video -------------------------------------------------

        /// <summary>Locates an ffmpeg executable: first a bundled copy in Tools\ffmpeg
        /// next to the exe, otherwise the first ffmpeg.exe on PATH. Returns null if none.</summary>
        public static string FindFfmpeg()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(local)) return local;
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string cand = Path.Combine(dir.Trim(), "ffmpeg.exe");
                        if (File.Exists(cand)) return cand;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Re-encodes an EA VP6 video file to H.264 .mp4 via ffmpeg. Audio is
        /// dropped (-an): the EA audio sub-stream isn't decodable by ffmpeg's EA demuxer.</summary>
        public static bool TranscodeVideoToMp4(string ffmpeg, string inputPath, string outPath, out string error)
        {
            error = null;
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = string.Format(
                    "-y -hide_banner -loglevel error -i \"{0}\" -c:v libx264 -pix_fmt yuv420p -an \"{1}\"",
                    inputPath, outPath),
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
                    return true;
                error = string.IsNullOrWhiteSpace(err) ? ("ffmpeg exit " + p.ExitCode) : err.Trim();
                return false;
            }
        }

        private static int RunExportVideo(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: SimCityPak.exe export-video <input> <outputDir> [--mp4]");
                return 2;
            }
            string input = args[1];
            string outDir = args[2];
            bool toMp4 = args.Any(a => a.Equals("--mp4", StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(outDir);

            string ffmpeg = null;
            if (toMp4)
            {
                ffmpeg = FindFfmpeg();
                if (ffmpeg == null)
                {
                    Console.WriteLine("ERROR: --mp4 requested but ffmpeg was not found on PATH " +
                        "(or in Tools\\ffmpeg next to the exe).");
                    return 2;
                }
            }

            int ok = 0, fails = 0;

            if (input.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            {
                DatabasePackedFile package = DatabasePackedFile.LoadFromFile(input);
                foreach (DatabaseIndex index in package.Indices)
                {
                    if (index.TypeId != VIDEO_TYPE_ID) continue;
                    string baseName = string.Format("{0:x8}-{1:x8}-{2:x8}",
                        index.TypeId, index.GroupContainer, index.InstanceId);
                    if (ExportOneVideo(index.GetIndexData(true), baseName, outDir, ffmpeg)) ok++; else fails++;
                }
            }
            else if (Directory.Exists(input))
            {
                foreach (string file in Directory.GetFiles(input, "*.vp6"))
                {
                    if (ExportOneVideo(File.ReadAllBytes(file),
                        Path.GetFileNameWithoutExtension(file), outDir, ffmpeg)) ok++; else fails++;
                }
            }
            else if (File.Exists(input))
            {
                if (ExportOneVideo(File.ReadAllBytes(input),
                    Path.GetFileNameWithoutExtension(input), outDir, ffmpeg)) ok++; else fails++;
            }
            else
            {
                Console.WriteLine("ERROR: input not found: " + input);
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("Done. exported={0} failed={1}", ok, fails));
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return fails > 0 ? 1 : 0;
        }

        /// <summary>Writes one video resource: raw .vp6 always, plus .mp4 when ffmpeg is given.</summary>
        private static bool ExportOneVideo(byte[] data, string baseName, string outDir, string ffmpeg)
        {
            string vp6Path = Path.Combine(outDir, baseName + ".vp6");
            try
            {
                File.WriteAllBytes(vp6Path, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + baseName + " :: " + ex.Message);
                return false;
            }

            if (ffmpeg == null)
            {
                Console.WriteLine("OK   " + baseName + ".vp6");
                return true;
            }

            string mp4Path = Path.Combine(outDir, baseName + ".mp4");
            string error;
            if (TranscodeVideoToMp4(ffmpeg, vp6Path, mp4Path, out error))
            {
                Console.WriteLine("OK   " + baseName + ".vp6 + .mp4");
                return true;
            }
            Console.WriteLine("WARN " + baseName + ".vp6 written, but mp4 transcode failed :: " +
                error.Split('\n')[0].Trim());
            return false;
        }
    }
}
