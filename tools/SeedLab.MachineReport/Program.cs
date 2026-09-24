using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace SeedLab.MachineReport
{
    public static class Program
    {
        public const string ToolVersion = "1.0.0";

        /// <summary>The SeedLab commit the vendored source was taken from (VENDORED.md).</summary>
        public const string SeedLabCommit = "11aeb8f";

        public const string Usage = @"SeedLab machine report " + ToolVersion + @"

  Run it through ""Run SeedLab machine report.bat"". It checks that this PC reproduces SeedLab's
  world-generation arithmetic bit for bit at every instruction-set level, measures how fast it runs,
  and writes seedlab-machine-report.txt in the package folder.

  --no-pause              accepted and ignored (the .bat uses it)
  --timing-seconds <s>    seconds per timing measurement (default 10)
  --timing-reps <n>       measurements per timing configuration (default 3)
  --make-reference <file> build reference\fingerprints.json on the reference machine
  --which-runtime         say which .NET runtime this program runs on, and exit";

        public static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception) { }

            try
            {
                if (args.Length >= 1 && args[0] == "--child") return Child(args);
                if (args.Length >= 1 && args[0] == "--which-runtime")
                {
                    // Used by the build script: proves the apphost found the package's own runtime.
                    Package.RuntimeProof p = Package.ProveRuntime();
                    Console.WriteLine("runtime " + p.Version + " from " + p.RuntimeDir + " bundled=" + p.Bundled);
                    foreach ((string m, bool inside, string where) in p.Modules)
                        Console.WriteLine("  " + m + " inside=" + inside + " " + where);
                    return p.Bundled ? 0 : 1;
                }
                if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0 || Array.IndexOf(args, "/?") >= 0)
                {
                    Console.WriteLine(Usage);
                    return 0;
                }

                double seconds = 10;
                int reps = 3;
                string? reference = null;
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--no-pause":
                            break;
                        case "--timing-seconds" when i + 1 < args.Length:
                            seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                            break;
                        case "--timing-reps" when i + 1 < args.Length:
                            reps = int.Parse(args[++i], CultureInfo.InvariantCulture);
                            break;
                        case "--make-reference" when i + 1 < args.Length:
                            reference = args[++i];
                            break;
                        default:
                            Console.Error.WriteLine("Unknown argument '" + args[i] + "'.\n\n" + Usage);
                            return 2;
                    }
                }

                if (seconds < 0.5 || seconds > 120 || reps < 1 || reps > 20)
                {
                    Console.Error.WriteLine("--timing-seconds must be 0.5-120 and --timing-reps 1-20.");
                    return 2;
                }

                return new Orchestrator(seconds, reps, reference).Run();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("SeedLab machine report stopped: " + Package.Sanitize(LevelRun.Describe(e)));
                return 3;
            }
        }

        // ---- child processes ---------------------------------------------------------------------

        private static int Child(string[] args)
        {
            string mode = args.Length > 1 ? args[1] : "";
            string? outFile = null;
            double seconds = 10;
            int reps = 3;
            string level = "";
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--out" when i + 1 < args.Length: outFile = args[++i]; break;
                    case "--level" when i + 1 < args.Length: level = args[++i]; break;
                    case "--seconds" when i + 1 < args.Length: seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--reps" when i + 1 < args.Length: reps = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                }
            }

            if (outFile == null || !Package.IsInside(outFile, Package.WorkDir))
            {
                Console.Error.WriteLine("internal: a child needs --out inside the work folder");
                return 2;
            }

            void Progress(string s) => Console.WriteLine("@" + s);

            JsonObject result = mode switch
            {
                "level" => LevelRun.Run(level, Progress),
                "timing" => Timing.Run(seconds, reps, Progress),
                _ => throw new ArgumentException("unknown child mode " + mode),
            };
            File.WriteAllText(outFile, ToJson(result), new UTF8Encoding(false));
            return 0;
        }

        public static string ToJson(JsonNode node)
        {
            JsonSerializerOptions o = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                NewLine = "\n",
            };
            return node.ToJsonString(o);
        }
    }

    /// <summary>The parent process: runs every child, compares, and writes the report.</summary>
    public sealed class Orchestrator
    {
        private readonly double _seconds;
        private readonly int _reps;
        private readonly string? _referenceOut;
        private int _step;
        private int _steps;

        public Orchestrator(double seconds, int reps, string? referenceOut)
        {
            _seconds = seconds;
            _reps = reps;
            _referenceOut = referenceOut;
        }

        private bool MakingReference => _referenceOut != null;

        public int Run()
        {
            Stopwatch total = Stopwatch.StartNew();
            _steps = Level.All.Length + (MakingReference ? 1 : 2);
            Console.WriteLine("SeedLab machine report " + Program.ToolVersion);
            Console.WriteLine("Checks that this PC reproduces SeedLab's world-generation arithmetic exactly, at");
            Console.WriteLine("every instruction-set level, and measures its speed. It reads nothing but CPU and");
            Console.WriteLine("Windows facts, installs nothing, and writes only seedlab-machine-report.txt here.");
            Console.WriteLine("It takes about 3 to 5 minutes. Please leave the PC alone until it says it is done.");
            Console.WriteLine();

            Console.CancelKeyPress += (_, e) =>
            {
                Children.KillCurrent();
                TryDeleteWork();
                Console.WriteLine();
                Console.WriteLine("Stopped. No report was written.");
            };

            PrepareWork();
            try
            {
                return MakingReference ? Reference(total) : Report(total);
            }
            finally
            {
                TryDeleteWork();
            }
        }

        private void Step(string what)
        {
            _step++;
            Console.WriteLine("[" + _step + "/" + _steps + "] " + what);
        }

        private static void Relay(string line) => Console.WriteLine("        " + line);

        // ---- the report ------------------------------------------------------------------------

        private int Report(Stopwatch total)
        {
            ReportData d = new ReportData();

            Step("checking the package and the .NET runtime it runs on");
            d.Manifest = Package.CheckManifest();
            d.ParentRuntime = Package.ProveRuntime();
            d.Facts = MachineFacts.Collect();
            Console.WriteLine("        measuring background CPU load for 3 s");
            double? load = Native.CpuBusyPercent(3000);
            d.BackgroundLoad = load;
            d.Reference = LoadReference(out string refProblem);
            d.ReferenceProblem = refProblem;

            RunLevels(d);

            Step("timing (" + _reps + " x " + _seconds.ToString(CultureInfo.InvariantCulture) + " s per configuration)");
            string tf = Path.Combine(Package.WorkDir, "timing.json");
            Children.Result t = Children.Run(
                new[] { "--child", "timing", "--out", tf,
                        "--seconds", _seconds.ToString(CultureInfo.InvariantCulture),
                        "--reps", _reps.ToString(CultureInfo.InvariantCulture) },
                Array.Empty<(string, string)>(), tf, TimeSpan.FromMinutes(30), Relay);
            d.Timing = t;

            d.TotalSeconds = total.Elapsed.TotalSeconds;
            ReportWriter w = new ReportWriter(d);
            string text = w.Build();
            File.WriteAllText(Package.ReportFile, text, new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine(w.ResultLine);
            Console.WriteLine("The report is in: seedlab-machine-report.txt (in this folder).");
            Console.WriteLine("Please send that file back. Nothing else is needed.");
            return w.Different == 0 ? 0 : 1;
        }

        private void RunLevels(ReportData d)
        {
            foreach (Level lv in Level.All)
            {
                Step("level '" + lv.Id + "' - " + lv.Title + (lv.Knobs.Length > 0 ? " (" + lv.KnobText() + ")" : ""));
                string f = Path.Combine(Package.WorkDir, "level-" + lv.Id + ".json");
                Children.Result r = Children.Run(new[] { "--child", "level", "--level", lv.Id, "--out", f },
                                                 lv.Knobs, f, TimeSpan.FromMinutes(45), Relay);
                d.Levels.Add((lv, r));
                Console.WriteLine("        done in " + r.Seconds.ToString("0", CultureInfo.InvariantCulture) + " s"
                                  + (r.Json == null ? " - the check process did not finish (exit code " + r.ExitCode + ")" : ""));
            }
        }

        private static JsonObject? LoadReference(out string problem)
        {
            problem = "";
            try
            {
                if (!File.Exists(Package.ReferenceFile))
                {
                    problem = "reference\\fingerprints.json is missing from the package";
                    return null;
                }

                JsonObject? o = JsonNode.Parse(File.ReadAllText(Package.ReferenceFile)) as JsonObject;
                if (o == null) problem = "reference\\fingerprints.json is not a JSON object";
                else if ((string?)o["definitionsHash"] != Fingerprints.DefinitionsHash())
                    problem = "reference\\fingerprints.json was made with different fingerprint definitions than this program";
                return o;
            }
            catch (Exception e)
            {
                problem = "reference\\fingerprints.json could not be read: " + e.GetType().Name;
                return null;
            }
        }

        // ---- the reference ---------------------------------------------------------------------

        private int Reference(Stopwatch total)
        {
            ReportData d = new ReportData { IsReferenceRun = true };
            Step("checking the package and the .NET runtime it runs on");
            d.Manifest = Package.CheckManifest();
            d.ParentRuntime = Package.ProveRuntime();
            d.Facts = MachineFacts.Collect();
            RunLevels(d);
            d.TotalSeconds = total.Elapsed.TotalSeconds;

            ReportWriter w = new ReportWriter(d);
            w.Build();   // evaluates every check except the comparison with a reference

            List<string> problems = new List<string>(w.ReferenceProblems());
            if (w.Different > 0) problems.Add(w.Different + " check(s) did not pass");
            if (problems.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("NO REFERENCE WRITTEN:");
                foreach (string p in problems) Console.WriteLine("  - " + p);
                foreach (string l in w.DifferentLines()) Console.WriteLine("  " + l);
                return 1;
            }

            JsonObject refJson = w.ReferenceJson();
            string outPath = Path.GetFullPath(_referenceOut!);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, Program.ToJson(refJson) + "\n", new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine("Reference written: every level passed and every level gave the same fingerprints.");
            Console.WriteLine("Fingerprints:");
            foreach (KeyValuePair<string, JsonNode?> kv in (JsonObject)refJson["fingerprints"]!)
                Console.WriteLine("  " + kv.Key.PadRight(20) + " " + kv.Value);
            return 0;
        }

        // ---- the work folder -------------------------------------------------------------------

        private static void PrepareWork()
        {
            TryDeleteWork();
            Directory.CreateDirectory(Package.WorkDir);
        }

        private static void TryDeleteWork()
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    if (Directory.Exists(Package.WorkDir)) Directory.Delete(Package.WorkDir, recursive: true);
                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep(250);
                }
            }
        }
    }
}
