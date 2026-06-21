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
                        return RunExportModels(args, ".glb", (m, p) => new GltfConverter().Export(m, p));
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
            Console.WriteLine("  export-gltf    : RW4 models   -> binary glTF .glb (<name>[_meshN].glb)");
            Console.WriteLine("  export-texture : RW4 textures -> .dds images     (<name>[_texN].dds)");
            Console.WriteLine("  export-prop    : .prop property lists -> readable .txt (or .json with --json),");
            Console.WriteLine("                   property names resolved where SimCityPak knows them");
            Console.WriteLine("  export-audio   : Wwise Vorbis audio -> PCM .wav (via bundled vgmstream)");
            Console.WriteLine("  export-all     : every model -> .glb AND every texture -> .dds, one folder");
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
                .Where(s => s.TypeCode == SectionTypeCodes.Mesh && s.obj is RW4Mesh)
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
                Console.WriteLine("Usage: SimCityPak.exe export-texture <input> <outputDir>");
                return 2;
            }
            string input = args[1];
            string outDir = args[2];
            Directory.CreateDirectory(outDir);

            int res = 0, textures = 0, fails = 0;

            Action<byte[], string> handle = (data, baseName) =>
            {
                try
                {
                    int t = ExportRw4Textures(data, outDir, baseName);
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
            Console.WriteLine(string.Format("Done. resources={0} textures(dds)={1} failed={2}", res, textures, fails));
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return fails > 0 ? 1 : 0;
        }

        /// <summary>Parses RW4 bytes and writes each Texture section as a .dds.</summary>
        private static int ExportRw4Textures(byte[] data, string outDir, string baseName)
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
                string outPath = Path.Combine(outDir, baseName + suffix + ".dds");
                try
                {
                    tex.SaveDds(outPath);
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
                        DumpProp(pf, Path.Combine(outDir, baseName + ext), baseName, json);
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
                        DumpProp(pf, Path.Combine(outDir, baseName + ext), baseName, json);
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
                    DumpProp(pf, Path.Combine(outDir, baseName + ext), baseName, json);
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
                Console.WriteLine("Usage: SimCityPak.exe export-all <input> <outputDir> [--locale <pkg>]");
                return 2;
            }
            string input = args[1];
            string outDir = args[2];
            Directory.CreateDirectory(outDir);

            int glb = 0, dds = 0, fails = 0;
            Action<byte[], string> handle = (data, baseName) =>
            {
                try { glb += ExportRw4Bytes(data, outDir, baseName, ".glb", (m, p) => new GltfConverter().Export(m, p)); }
                catch (Exception ex) { fails++; Logger.Exception("export-all model " + baseName, ex); Console.WriteLine("FAIL " + baseName + " (model) :: " + ex.Message); }
                try { dds += ExportRw4Textures(data, outDir, baseName); }
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
            Console.WriteLine(string.Format("Done. models(glb)={0} textures(dds)={1} failed={2}", glb, dds, fails));
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return fails > 0 ? 1 : 0;
        }

        /// <summary>
        /// Exports every model (-&gt; .glb) and every texture (-&gt; .dds) from the given
        /// resources into one folder, naming files by localized asset name when
        /// <paramref name="localeFile"/> is provided. Returns a short summary; used by
        /// the GUI "Export all" menu. Never throws (errors are logged per resource).
        /// </summary>
        public static string ExportAllToFolder(IEnumerable<DatabaseIndex> indices, string outDir, string localeFile)
        {
            Directory.CreateDirectory(outDir);
            Logger.Info("Export-all -> " + outDir + (string.IsNullOrEmpty(localeFile) ? "" : " (locale: " + localeFile + ")"));
            var nameMap = BuildLocaleNameMap(indices, localeFile);
            var used = new HashSet<string>();
            int glb = 0, dds = 0, fails = 0;
            foreach (DatabaseIndex index in indices)
            {
                if (index.TypeId != RW4_MODEL_TYPE_ID) continue;
                string fallback = string.Format("{0:x8}-{1:x8}-{2:x8}",
                    index.TypeId, index.GroupContainer, index.InstanceId);
                string baseName = ResolveBaseName(index.InstanceId, fallback, nameMap, used);
                byte[] data;
                try { data = index.GetIndexData(true); }
                catch (Exception ex) { fails++; Logger.Exception("export-all read " + baseName, ex); continue; }
                try { glb += ExportRw4Bytes(data, outDir, baseName, ".glb", (m, p) => new GltfConverter().Export(m, p)); }
                catch (Exception ex) { fails++; Logger.Exception("export-all model " + baseName, ex); }
                try { dds += ExportRw4Textures(data, outDir, baseName); }
                catch (Exception ex) { fails++; Logger.Exception("export-all textures " + baseName, ex); }
            }
            string summary = string.Format(
                "Exported {0} models (.glb) and {1} textures (.dds); {2} failed.\nLocalized names: {3}.",
                glb, dds, fails, nameMap.Count);
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
