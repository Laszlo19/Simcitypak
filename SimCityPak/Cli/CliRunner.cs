using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Gibbed.Spore.Package;
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
                case "export-audio":
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

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "export-obj":
                        return RunExportObj(args);
                    case "export-audio":
                        return RunExportAudio(args);
                    default:
                        PrintHelp();
                        return 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("SimCityPak CLI");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  SimCityPak.exe export-obj   <input> <outputDir>");
            Console.WriteLine("  SimCityPak.exe export-audio <input> <outputDir>");
            Console.WriteLine();
            Console.WriteLine("  <input> may be:");
            Console.WriteLine("    - a .package file  -> exports every matching resource inside it");
            Console.WriteLine("    - a folder         -> exports every matching file in it");
            Console.WriteLine("    - a single file    -> exports just that one");
            Console.WriteLine();
            Console.WriteLine("  export-obj   : RW4 models -> Wavefront .obj  (<name>[_meshN].obj)");
            Console.WriteLine("  export-audio : Wwise Vorbis audio -> playable PCM .wav (via bundled vgmstream)");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  SimCityPak.exe export-obj   SimCity.package C:\\out");
            Console.WriteLine("  SimCityPak.exe export-obj   C:\\models-dlc0 C:\\out");
            Console.WriteLine("  SimCityPak.exe export-audio C:\\game-audio  C:\\out");
        }

        private static int RunExportObj(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: SimCityPak.exe export-obj <input> <outputDir>");
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
                        int m = ExportRw4Bytes(File.ReadAllBytes(file), outDir, baseName);
                        if (m > 0) { models++; meshes += m; }
                        else Console.WriteLine("SKIP " + baseName + " (no mesh)");
                    }
                    catch (Exception ex) { fails++; Console.WriteLine("FAIL " + baseName + " :: " + ex.Message); }
                }
            }
            else if (input.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            {
                DatabasePackedFile package = DatabasePackedFile.LoadFromFile(input);
                foreach (DatabaseIndex index in package.Indices)
                {
                    if (index.TypeId != RW4_MODEL_TYPE_ID) continue;
                    string baseName = string.Format("{0:x8}-{1:x8}-{2:x8}",
                        index.TypeId, index.GroupContainer, index.InstanceId);
                    try
                    {
                        int m = ExportRw4Bytes(index.GetIndexData(true), outDir, baseName);
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
                    int m = ExportRw4Bytes(File.ReadAllBytes(input), outDir, baseName);
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
            Console.WriteLine(string.Format("Done. models={0} meshes(obj)={1} failed={2}", models, meshes, fails));
            Console.WriteLine("Output: " + Path.GetFullPath(outDir));
            return fails > 0 ? 1 : 0;
        }

        /// <summary>
        /// Parses RW4 bytes and writes each Mesh section as an .obj.
        /// Returns the number of .obj files written.
        /// </summary>
        private static int ExportRw4Bytes(byte[] data, string outDir, string baseName)
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
                string outPath = Path.Combine(outDir, baseName + suffix + ".obj");
                mesh.Export(outPath);
                written++;
                Console.WriteLine("OK   " + Path.GetFileName(outPath));
            }
            return written;
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
