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
                        return RunExportModels(args, ".glb", (m, p) => new GltfConverter(mesh => ResolveDiffusePng(mesh, texLookup)).Export(m, p));
                    }
                    case "export-texture":
                        return RunExportTextures(args);
                    case "export-prop":
                        return RunExportProp(args);
                    case "export-all":
                        return RunExportAll(args);
                    case "export-audio":
                        return RunExportAudio(args);
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
            Console.WriteLine();
            Console.WriteLine("  <input> may be:");
            Console.WriteLine("    - a .package file  -> exports every matching resource inside it");
            Console.WriteLine("    - a folder         -> exports every matching file in it");
            Console.WriteLine("    - a single file    -> exports just that one");
            Console.WriteLine();
            Console.WriteLine("  export-obj     : RW4 models   -> Wavefront .obj  (<name>[_meshN].obj)");
            Console.WriteLine("  export-gltf    : RW4 models   -> binary glTF .glb (<name>[_meshN].glb),");
            Console.WriteLine("                   with the model's diffuse texture resolved from its material");
            Console.WriteLine("                   (searches sibling packages; override with --textures <pkg|dir>)");
            Console.WriteLine("  export-texture : RW4 textures -> images (--format png|jpg|tga|dds, default png)");
            Console.WriteLine("  export-prop    : .prop property lists -> readable .txt (or .json with --json),");
            Console.WriteLine("                   property names resolved; --combine merges an asset's model +");
            Console.WriteLine("                   gameplay + catalog props into one file (package input)");
            Console.WriteLine("  export-audio   : Wwise Vorbis audio -> PCM .wav (via bundled vgmstream)");
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
        /// Resolves a mesh's diffuse texture as PNG bytes via its model's RW4Material
        /// texture references, decoding the referenced resources (RW4 DXT or raw raster)
        /// and choosing the largest non-normal-map candidate. Returns null if none resolve
        /// (the caller then falls back to the internal-texture heuristic). Never throws.
        /// </summary>
        private static byte[] ResolveDiffusePng(RW4Mesh mesh, Dictionary<uint, DatabaseIndex> lookup)
        {
            try
            {
                if (lookup == null || mesh == null || mesh.model == null) return null;

                byte[] bestRgba = null, fallbackRgba = null;
                int bestW = 0, bestH = 0, fbW = 0, fbH = 0;
                long bestArea = -1, fbArea = -1;

                foreach (RW4Section s in mesh.model.Sections)
                {
                    RW4Material mat = s.obj as RW4Material;
                    if (mat == null || mat.Materials == null) continue;
                    foreach (MaterialTextureReference mr in mat.Materials)
                    {
                        DatabaseIndex tdi;
                        if (mr.TextureInstanceId == 0) continue;
                        if (!lookup.TryGetValue(mr.TextureInstanceId, out tdi)) continue;
                        int w, h;
                        byte[] rgba = DecodeTextureResourceRgba(tdi, out w, out h);
                        if (rgba == null) continue;
                        // ignore strips/atlases (tiny dimensions) as a diffuse base color
                        if (w < 16 || h < 16) continue;
                        long area = (long)w * h;
                        if (area > fbArea) { fallbackRgba = rgba; fbW = w; fbH = h; fbArea = area; }
                        if (GltfConverter.LooksLikeNormalMap(rgba)) continue;
                        if (area > bestArea) { bestRgba = rgba; bestW = w; bestH = h; bestArea = area; }
                    }
                }
                if (bestRgba != null) return GltfConverter.RgbaToPng(bestRgba, bestW, bestH);
                if (fallbackRgba != null) return GltfConverter.RgbaToPng(fallbackRgba, fbW, fbH);
                return null;
            }
            catch (Exception ex) { Logger.Exception("ResolveDiffusePng", ex); return null; }
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

            foreach (DatabaseIndex index in indices)
            {
                if (index.TypeId != PROP_TYPE_ID) continue;
                PropertyFile pf;
                try { pf = new PropertyFile(index); }
                catch { continue; }

                string name = null;
                foreach (uint h in NameHashes)
                {
                    Property prop;
                    if (!pf.Values.TryGetValue(h, out prop)) continue;
                    ArrayProperty arr = prop as ArrayProperty;
                    if (arr == null || arr.Values.Count == 0) continue;
                    TextProperty tp = arr.Values[0] as TextProperty;
                    if (tp == null) continue;
                    string s = locale.GetLocalizedString(tp.TableId, tp.InstanceId);
                    if (!string.IsNullOrEmpty(s)) { name = s; break; }
                }
                if (string.IsNullOrEmpty(name)) continue;

                if (!map.ContainsKey(index.InstanceId)) map[index.InstanceId] = name;
                foreach (var kv in pf.Values) CollectKeyRefs(kv.Value, name, map);
            }
            return map;
        }

        private static void CollectKeyRefs(Property p, string name, Dictionary<uint, string> map)
        {
            KeyProperty kp = p as KeyProperty;
            if (kp != null)
            {
                if (kp.TypeId == RW4_MODEL_TYPE_ID && !map.ContainsKey(kp.InstanceId))
                    map[kp.InstanceId] = name;
                return;
            }
            ArrayProperty arr = p as ArrayProperty;
            if (arr != null)
                foreach (Property sub in arr.Values) CollectKeyRefs(sub, name, map);
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
                try { glb += ExportRw4Bytes(data, outDir, baseName, ".glb", (m, p) => new GltfConverter(mesh => ResolveDiffusePng(mesh, texLookup)).Export(m, p)); }
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
            int glb = 0, tex = 0, fails = 0;
            foreach (DatabaseIndex index in indices)
            {
                if (index.TypeId != RW4_MODEL_TYPE_ID) continue;
                string fallback = string.Format("{0:x8}-{1:x8}-{2:x8}",
                    index.TypeId, index.GroupContainer, index.InstanceId);
                string baseName = ResolveBaseName(index.InstanceId, fallback, nameMap, used);
                byte[] data;
                try { data = index.GetIndexData(true); }
                catch (Exception ex) { fails++; Logger.Exception("export-all read " + baseName, ex); continue; }
                try { glb += ExportRw4Bytes(data, outDir, baseName, ".glb", (m, p) => new GltfConverter(mesh => ResolveDiffusePng(mesh, texLookup)).Export(m, p)); }
                catch (Exception ex) { fails++; Logger.Exception("export-all model " + baseName, ex); }
                try { tex += ExportRw4Textures(data, outDir, baseName, format); }
                catch (Exception ex) { fails++; Logger.Exception("export-all textures " + baseName, ex); }
            }
            string summary = string.Format(
                "Exported {0} models (.glb) and {1} textures (.{2}); {3} failed.\nLocalized names: {4}.",
                glb, tex, format, fails, nameMap.Count);
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
