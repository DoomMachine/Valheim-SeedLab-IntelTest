using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace SeedLab.MachineReport
{
    /// <summary>Everything the parent process gathered, before it is judged and written.</summary>
    public sealed class ReportData
    {
        public bool IsReferenceRun;
        public Package.ManifestResult Manifest = new Package.ManifestResult();
        public Package.RuntimeProof ParentRuntime = new Package.RuntimeProof();
        public JsonObject Facts = new JsonObject();
        public double? BackgroundLoad;
        public JsonObject? Reference;
        public string ReferenceProblem = "";
        public List<(Level Level, Children.Result Result)> Levels = new List<(Level, Children.Result)>();
        public Children.Result? Timing;
        public double TotalSeconds;
    }

    /// <summary>
    /// Judges every check (PASS or DIFFERENT) and writes the report: a plain summary a person can
    /// read, then one JSON block with all of the data for analysis. Every piece of text goes
    /// through <see cref="Package.Sanitize"/> on the way out.
    /// </summary>
    public sealed class ReportWriter
    {
        private sealed class Check
        {
            public string Group = "";
            public string Name = "";
            public bool Pass;
            public string Detail = "";
        }

        private readonly ReportData _d;
        private readonly List<Check> _checks = new List<Check>();
        private readonly StringBuilder _t = new StringBuilder();

        public ReportWriter(ReportData d) => _d = d;

        public int Different => _checks.Count(c => !c.Pass);
        public int Passed => _checks.Count(c => c.Pass);
        public string ResultLine { get; private set; } = "";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private void Add(string group, string name, bool pass, string detail)
            => _checks.Add(new Check { Group = group, Name = name, Pass = pass, Detail = Package.Sanitize(detail) });

        // =========================================================================================

        public string Build()
        {
            JudgePackage();
            foreach ((Level lv, Children.Result r) in _d.Levels) JudgeLevel(lv, r);

            int total = _checks.Count;
            ResultLine = Different == 0
                ? "RESULT: ALL " + total + " CHECKS PASS"
                : "RESULT: " + Different + " OF " + total + " CHECKS DIFFERENT (see the lines marked DIFFERENT)";

            L("SeedLab machine report");
            L("======================");
            L("Tool       SeedLab.MachineReport " + Program.ToolVersion + ", SeedLab source " + Program.SeedLabCommit
              + " (Valheim 1.0.15 world generation, reproduced offline)");
            L("Reference  " + ReferenceSummary());
            L("Run time   " + Minutes(_d.TotalSeconds));
            L("");
            L(ResultLine);
            L("");
            WriteMachine();
            WriteChecks();
            WriteTiming();
            L("What this file holds: hardware and Windows version facts and the results above. No user or");
            L("computer name, no serial numbers, no network details, no paths outside the package folder.");
            L("");
            L("-----BEGIN SEEDLAB MACHINE REPORT JSON-----");
            _t.Append(Program.ToJson(BuildJson())).Append('\n');
            L("-----END SEEDLAB MACHINE REPORT JSON-----");
            return Package.Sanitize(_t.ToString());
        }

        private void L(string s) => _t.Append(s).Append('\n');

        // ---- judging -----------------------------------------------------------------------------

        private void JudgePackage()
        {
            Package.ManifestResult m = _d.Manifest;
            string detail = !m.Present
                ? "package-files.sha256 is missing, so the files could not be checked"
                : m.Matching + " of " + m.Listed + " files match the build"
                  + (m.Missing.Count > 0 ? "; missing: " + string.Join(", ", m.Missing.Take(8)) : "")
                  + (m.Changed.Count > 0 ? "; changed: " + string.Join(", ", m.Changed.Take(8)) : "")
                  + (m.Unlisted.Count > 0 ? "; not from the build: " + string.Join(", ", m.Unlisted.Take(8)) : "");
            Add("Package", "package files intact", m.Ok, detail);

            Package.RuntimeProof p = _d.ParentRuntime;
            Add("Package", "bundled .NET runtime used", p.Bundled,
                ".NET " + p.Version + " from " + p.RuntimeDir + (p.Bundled ? " (inside this package)" : " - NOT the package's own runtime"));
        }

        private void JudgeLevel(Level lv, Children.Result r)
        {
            string g = "Level '" + lv.Id + "'";
            JsonObject? j = r.Json;
            if (j == null || r.ExitCode != 0)
            {
                string why = r.TimedOut ? "the check process took too long and was stopped"
                    : "the check process stopped with exit code " + r.ExitCode;
                string err = FirstLines(r.Stderr, 12);
                Add(g, "check process finished", false, why + (err.Length > 0 ? ": " + err : ""));
                return;
            }

            // The switches.
            string tier = (string?)j["tier"] ?? "?";
            if (lv.Knobs.Length > 0)
            {
                bool reached = Level.TierRank(tier) <= Level.TierRank(lv.MaxTier);
                Add(g, "switches took effect", reached,
                    lv.KnobText() + " -> this process uses " + TierName(tier)
                    + (reached ? "" : ", so the switch was ignored and this level was not reached"));
            }

            // The runtime.
            JsonObject? rt = j["runtime"] as JsonObject;
            bool bundled = rt != null && (bool?)rt["bundled"] == true;
            Add(g, "bundled .NET runtime used", bundled,
                ".NET " + (string?)rt?["version"] + " from " + (string?)rt?["runtimeDir"]);

            // Perlin startup self-test.
            JsonObject? ps = j["perlinSelfTest"] as JsonObject;
            Add(g, "Perlin startup self-test", ps != null && (bool?)ps["ok"] == true, (string?)ps?["text"] ?? "(no result)");

            // Machine self-test.
            JsonObject? mst = j["machineSelfTest"] as JsonObject;
            bool mstOk = mst != null && (bool?)mst["ok"] == true;
            StringBuilder md = new StringBuilder();
            if (mst?["suites"] is JsonArray suites)
            {
                foreach (JsonNode? s in suites)
                {
                    if (md.Length > 0) md.Append("; ");
                    int checks = (int?)s?["checks"] ?? 0, fails = (int?)s?["failures"] ?? 0;
                    md.Append((string?)s?["name"]).Append(' ').Append(N(checks - fails)).Append('/').Append(N(checks)).Append(" exact");
                    string ff = (string?)s?["firstFailure"] ?? "";
                    if (ff.Length > 0) md.Append(" (first difference: ").Append(ff).Append(')');
                }
            }

            if (mst != null && (bool?)mst["nativesSuiteRegistered"] != true)
                md.Append("; the natives suite was not found in the package's natives folder");
            if (!mstOk && mst != null) md.Append("; status ").Append((string?)mst["status"]);
            Add(g, "machine self-test", mstOk, md.ToString());

            // The native-function gate.
            JsonObject? nat = j["natives"] as JsonObject;
            if (nat == null || nat["checks"] is not JsonArray nchecks || nchecks.Count == 0)
            {
                Add(g, "native-function goldens", false, FirstLines((string?)nat?["output"] ?? "no result", 12));
            }
            else
            {
                Add(g, "natives: goldens read from the package", (bool?)nat["goldensFolderIsPackage"] == true,
                    "natives\\ in this package");
                foreach (JsonNode? c in nchecks)
                    Add(g, "natives: " + (string?)c?["name"], (bool?)c?["pass"] == true, (string?)c?["message"] ?? "");
                if ((int?)nat["exitCode"] != 0 && nchecks.All(c => (bool?)c?["pass"] == true))
                    Add(g, "natives: completed", false, "exit code " + (int?)nat["exitCode"]);
            }

            // Fingerprints.
            Dictionary<string, string> got = Fps(j);
            foreach (FingerprintDef d in Fingerprints.All)
            {
                got.TryGetValue(d.Id, out string? h);
                string mine = h ?? "";
                if (_d.IsReferenceRun)
                {
                    Add(g, "fingerprint " + d.Id, mine.Length == 64, mine.Length == 64 ? Short(mine) + "  " + d.Describe() : "not computed");
                    continue;
                }

                string want = RefFp(d.Id);
                if (_d.ReferenceProblem.Length > 0)
                    Add(g, "fingerprint " + d.Id, false, _d.ReferenceProblem);
                else if (mine.Length != 64)
                    Add(g, "fingerprint " + d.Id, false, "not computed: " + ErrorOf(j, d.Id));
                else
                    Add(g, "fingerprint " + d.Id, mine == want,
                        (mine == want ? Short(mine) + " = reference" : Short(mine) + ", reference " + Short(want)) + "  (" + d.Describe() + ")");
            }
        }

        private static Dictionary<string, string> Fps(JsonObject j)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (j["fingerprints"] is JsonArray a)
            {
                foreach (JsonNode? f in a)
                {
                    string id = (string?)f?["id"] ?? "";
                    string h = (string?)f?["sha256"] ?? "";
                    if (id.Length > 0) d[id] = h;
                }
            }

            return d;
        }

        private static string ErrorOf(JsonObject j, string id)
        {
            if (j["fingerprints"] is JsonArray a)
                foreach (JsonNode? f in a)
                    if ((string?)f?["id"] == id) return (string?)f?["error"] ?? "no value";
            return "no value";
        }

        private string RefFp(string id) => (string?)(_d.Reference?["fingerprints"] as JsonObject)?[id] ?? "";

        // ---- the reference run ------------------------------------------------------------------

        /// <summary>What stops a reference from being written: an unreached level, or levels that disagree.</summary>
        public IEnumerable<string> ReferenceProblems()
        {
            Dictionary<string, string>? first = null;
            HashSet<string> tiers = new HashSet<string>();
            foreach ((Level lv, Children.Result r) in _d.Levels)
            {
                if (r.Json == null) { yield return "level " + lv.Id + " did not finish"; continue; }
                string tier = (string?)r.Json["tier"] ?? "?";
                if (!tiers.Add(tier)) yield return "level " + lv.Id + " did not reach a new instruction-set tier (" + tier + ")";
                Dictionary<string, string> f = Fps(r.Json);
                if (first == null) { first = f; continue; }
                foreach (FingerprintDef d in Fingerprints.All)
                {
                    first.TryGetValue(d.Id, out string? a);
                    f.TryGetValue(d.Id, out string? b);
                    if (a == null || a != b) yield return "fingerprint " + d.Id + " differs between level " + _d.Levels[0].Level.Id + " and " + lv.Id;
                }
            }
        }

        public IEnumerable<string> DifferentLines() =>
            _checks.Where(c => !c.Pass).Select(c => "DIFFERENT  " + c.Group + ": " + c.Name + " - " + c.Detail);

        public JsonObject ReferenceJson()
        {
            JsonObject defs = new JsonObject();
            JsonArray defList = new JsonArray();
            foreach (FingerprintDef d in Fingerprints.All)
            {
                defList.Add(new JsonObject
                {
                    ["id"] = d.Id,
                    ["kind"] = d.Kind,
                    ["seed"] = d.Seed,
                    ["size"] = d.Size,
                    ["step"] = d.Step,
                    ["description"] = d.Describe(),
                    ["layout"] = d.Layout,
                });
            }

            JsonObject fps = new JsonObject();
            foreach (KeyValuePair<string, string> kv in Fps(_d.Levels[0].Result.Json!)) fps[kv.Key] = kv.Value;

            JsonArray levels = new JsonArray();
            foreach ((Level lv, Children.Result r) in _d.Levels)
            {
                levels.Add(new JsonObject
                {
                    ["id"] = lv.Id,
                    ["switches"] = lv.KnobText(),
                    ["tier"] = (string?)r.Json!["tier"],
                    ["instructionSets"] = OnList(r.Json!["isa"] as JsonObject),
                });
            }

            JsonObject? cpuid = _d.Levels[0].Result.Json!["cpuid"] as JsonObject;
            JsonObject? win = _d.Facts["windows"] as JsonObject;
            return new JsonObject
            {
                ["format"] = "seedlab-machine-report-reference 1",
                ["tool"] = Program.ToolVersion,
                ["seedlabCommit"] = Program.SeedLabCommit,
                ["what"] = "SHA-256 fingerprints of SeedLab's port of Valheim 1.0.15 world generation, identical at every "
                           + "instruction-set level on the machine below. A tester's machine is compared against these.",
                ["definitionsHash"] = Fingerprints.DefinitionsHash(),
                ["definitions"] = defList,
                ["fingerprints"] = fps,
                ["madeOn"] = new JsonObject
                {
                    ["cpu"] = (string?)_d.Facts["cpuName"],
                    ["vendor"] = (string?)cpuid?["vendor"],
                    ["family"] = (int?)cpuid?["family"],
                    ["model"] = (int?)cpuid?["model"],
                    ["stepping"] = (int?)cpuid?["stepping"],
                    ["logicalProcessors"] = (int?)_d.Facts["logicalProcessors"],
                    ["levels"] = levels,
                    ["windows"] = ((string?)win?["name"] ?? "") + " " + (string?)win?["release"] + ", build " + (string?)win?["build"],
                    ["ucrtbase"] = (string?)_d.Facts["ucrtbase"],
                    ["dotnet"] = _d.ParentRuntime.Version,
                },
            };
        }

        // ---- writing -----------------------------------------------------------------------------

        private string ReferenceSummary()
        {
            if (_d.IsReferenceRun) return "(this run makes the reference)";
            if (_d.Reference == null) return "(missing: " + _d.ReferenceProblem + ")";
            JsonObject? m = _d.Reference["madeOn"] as JsonObject;
            return "made on " + (string?)m?["cpu"] + ", at levels "
                   + string.Join(", ", ((m?["levels"] as JsonArray) ?? new JsonArray()).Select(x => (string?)x?["tier"]))
                   + (_d.ReferenceProblem.Length > 0 ? " - PROBLEM: " + _d.ReferenceProblem : "");
        }

        private JsonObject? AsFound => _d.Levels.Count > 0 ? _d.Levels[0].Result.Json : null;

        private void WriteMachine()
        {
            JsonObject f = _d.Facts;
            L("Machine");
            string cpu = (string?)f["cpuName"] ?? "?";
            int? mhz = (int?)f["cpuNominalMHz"];
            Row("CPU", cpu + (mhz.HasValue ? " (nominal " + mhz.Value + " MHz)" : ""));
            if (AsFound?["cpuid"] is JsonObject c)
            {
                int model = (int?)c["model"] ?? 0;
                Row("CPUID", (string?)c["vendor"] + ", family " + (int?)c["family"] + ", model " + model
                             + " (0x" + model.ToString("X2", Inv) + "), stepping " + (int?)c["stepping"]
                             + ", signature " + (string?)c["signature"]
                             + ((bool?)c["hybridFlag"] == true ? ", hybrid flag set" : ""));
            }

            if (f["cores"] is JsonObject cores)
            {
                string s = (int?)cores["physical"] + " physical, " + (int?)cores["logical"] + " logical";
                int smt = (int?)cores["withSmt"] ?? 0;
                if (smt > 0) s += " (Hyper-Threading/SMT on " + smt + " cores)";
                if (cores["byEfficiencyClass"] is JsonObject bc)
                {
                    List<(int cls, int n, int lg)> cl = bc.Select(kv => (int.Parse(kv.Key, Inv), (int?)kv.Value?["cores"] ?? 0, (int?)kv.Value?["logical"] ?? 0))
                        .OrderByDescending(x => x.Item1).ToList();
                    if (cl.Count == 2)
                        s += "; hybrid: " + cl[0].n + " performance cores (" + cl[0].lg + " logical) + " + cl[1].n
                             + " efficiency cores (" + cl[1].lg + " logical)";
                    else if (cl.Count > 2)
                        s += "; hybrid, " + cl.Count + " core classes: " + string.Join(", ", cl.Select(x => "class " + x.cls + " " + x.n + " cores"))
                             + " (higher class = faster)";
                    else s += "; one core type (not hybrid)";
                }

                Row("Cores", s);
            }
            else Row("Cores", (int?)f["logicalProcessors"] + " logical (physical count not available)");

            if (AsFound?["isa"] is JsonObject isa)
            {
                Row("Instruction sets", string.Join(" ", Isa.Headline.Where(n => (bool?)isa[n] == true)) + "  (as .NET sees them, no switches)");
                List<string> avx512 = isa.Where(kv => kv.Key.StartsWith("Avx512", StringComparison.Ordinal) && !kv.Key.Contains('.') && (bool?)kv.Value == true)
                    .Select(kv => kv.Key.Substring(6)).ToList();
                Row("AVX-512", avx512.Count > 0 ? "yes: " + string.Join(" ", avx512) : "no");
                List<string> avx10 = isa.Where(kv => kv.Key.StartsWith("Avx10", StringComparison.Ordinal) && !kv.Key.Contains('.') && (bool?)kv.Value == true)
                    .Select(kv => kv.Key).ToList();
                Row("AVX10", avx10.Count > 0 ? "yes: " + string.Join(" ", avx10) : "no");
            }

            if (AsFound?["vectors"] is JsonObject v)
            {
                Row("Vectors", "Vector128 " + Acc(v["vector128Accelerated"]) + ", Vector256 " + Acc(v["vector256Accelerated"])
                               + ", Vector512 " + Acc(v["vector512Accelerated"]) + "; Vector<T> is " + (int?)v["vectorTBytes"] + " bytes");
            }

            long? ram = (long?)f["ramBytes"];
            Row("Memory", ram.HasValue ? (ram.Value / 1073741824.0).ToString("0.0", Inv) + " GiB" : "(not available)");
            if (f["windows"] is JsonObject w)
            {
                Row("Windows", (string?)w["name"] + " " + (string?)w["release"] + ", build " + (string?)w["build"]
                               + " (" + (string?)w["osArchitecture"] + ")");
            }

            Row("C runtime", "ucrtbase.dll " + (string?)f["ucrtbase"]
                             + (_d.ParentRuntime.UcrtLoadedFrom.Length > 0 ? " (loaded from " + _d.ParentRuntime.UcrtLoadedFrom + ")" : ""));
            Row(".NET", _d.ParentRuntime.Version + " from " + _d.ParentRuntime.RuntimeDir
                        + (_d.ParentRuntime.Bundled ? " (the runtime bundled in this package)" : " (NOT the bundled runtime)"));
            if (f["power"] is JsonObject pw)
            {
                bool? ac = (bool?)pw["onMainsPower"], bat = (bool?)pw["hasBattery"];
                Row("Power", (ac == true ? "on mains power" : ac == false ? "ON BATTERY - timings will be low" : "mains power unknown")
                             + (bat == true ? "; Windows reports a battery (a laptop's, or a UPS)" : bat == false ? "; no battery" : ""));
            }

            if (!_d.IsReferenceRun)
                Row("Background load", _d.BackgroundLoad.HasValue
                    ? _d.BackgroundLoad.Value.ToString("0.0", Inv) + " % of the CPU busy in the 3 s before the checks"
                    : "(not measured)");
            JsonArray inh = f["inheritedDotnetSettings"] as JsonArray ?? new JsonArray();
            Row(".NET settings", inh.Count == 0 ? "none set on this machine"
                : string.Join(", ", inh.Select(x => (string?)x)) + " (set on this machine; removed for every check)");
            L("");
        }

        private void Row(string k, string v) => L("  " + k.PadRight(18) + v);

        private static string Acc(JsonNode? n) => (bool?)n == true ? "accelerated" : "not accelerated";

        private void WriteChecks()
        {
            L("Checks");
            string? group = null;
            foreach (Check c in _checks)
            {
                if (c.Group != group)
                {
                    group = c.Group;
                    string head = group;
                    Level? lv = Level.All.FirstOrDefault(x => "Level '" + x.Id + "'" == group);
                    if (lv != null)
                    {
                        head += " - " + lv.Title;
                        JsonObject? j = _d.Levels.FirstOrDefault(x => x.Level.Id == lv.Id).Result?.Json;
                        if (j != null)
                        {
                            head += "; uses " + TierName((string?)j["tier"] ?? "?");
                            string? same = SameAsEarlier(lv);
                            if (same != null) head += " - the same instruction sets as level '" + same + "' on this CPU";
                        }
                    }

                    L("  " + head);
                }

                L("    " + (c.Pass ? "PASS       " : "DIFFERENT  ") + c.Name + (c.Detail.Length > 0 ? " - " + c.Detail : ""));
            }

            L("");
        }

        /// <summary>The first earlier level whose child saw exactly the same instruction sets, or null.</summary>
        private string? SameAsEarlier(Level lv)
        {
            JsonObject? mine = _d.Levels.FirstOrDefault(x => x.Level.Id == lv.Id).Result?.Json?["isa"] as JsonObject;
            if (mine == null) return null;
            foreach ((Level other, Children.Result r) in _d.Levels)
            {
                if (other.Id == lv.Id) return null;
                if (r.Json?["isa"] is JsonObject theirs && JsonNode.DeepEquals(mine, theirs)) return other.Id;
            }

            return null;
        }

        private void WriteTiming()
        {
            if (_d.IsReferenceRun) return;
            L("Timing (median of the repeated measurements; min-max beside it)");
            JsonObject? t = _d.Timing?.Json;
            if (t == null)
            {
                L("  not measured: the timing process stopped (exit code " + (_d.Timing?.ExitCode ?? -1) + ") "
                  + FirstLines(_d.Timing?.Stderr ?? "", 6));
                L("");
                return;
            }

            L("  biome grid     = " + (string?)t["biomeGrid"]);
            L("  pre-generation = " + (string?)t["pregeneration"]);
            double? one = null;
            foreach (JsonNode? row in (t["rows"] as JsonArray) ?? new JsonArray())
            {
                string work = (string?)row?["work"] ?? "";
                int th = (int?)row?["threads"] ?? 0;
                double med = (double?)row?["seedsPerSecondMedian"] ?? 0;
                double lo = (double?)row?["seedsPerSecondMin"] ?? 0, hi = (double?)row?["seedsPerSecondMax"] ?? 0;
                if (th == 1) one = med;
                string scale = th > 1 && one.HasValue && one.Value > 0 ? ", " + (med / one.Value).ToString("0.0", Inv) + "x one thread" : "";
                string s = "  " + (work + ", " + th + (th == 1 ? " thread" : " threads")).PadRight(30)
                           + med.ToString("N1", Inv) + " seeds/s (" + lo.ToString("N1", Inv) + "-" + hi.ToString("N1", Inv) + ")";
                if (row?["pointsPerSecondMedian"] is JsonNode pts)
                    s += ", " + ((double?)pts ?? 0).ToString("N0", Inv) + " points/s";
                L(s + scale);
            }

            L("");
        }

        private JsonObject BuildJson()
        {
            JsonObject machine = JsonNode.Parse(_d.Facts.ToJsonString())!.AsObject();
            if (AsFound?["cpuid"] is JsonObject c) machine["cpuid"] = c.DeepClone();
            if (AsFound?["isa"] is JsonObject isa) machine["instructionSetsAsFound"] = isa.DeepClone();
            if (AsFound?["vectors"] is JsonObject v) machine["vectorsAsFound"] = v.DeepClone();
            if (_d.BackgroundLoad.HasValue) machine["backgroundLoadPercent"] = Math.Round(_d.BackgroundLoad.Value, 1);

            JsonArray levels = new JsonArray();
            foreach ((Level lv, Children.Result r) in _d.Levels)
            {
                JsonObject o = new JsonObject
                {
                    ["id"] = lv.Id,
                    ["title"] = lv.Title,
                    ["switches"] = lv.KnobText(),
                    ["exitCode"] = r.ExitCode,
                    ["timedOut"] = r.TimedOut,
                    ["seconds"] = Math.Round(r.Seconds, 1),
                    ["result"] = r.Json?.DeepClone(),
                };
                if (r.Json == null || r.ExitCode != 0) o["stderr"] = FirstLines(r.Stderr, 40);
                levels.Add(o);
            }

            JsonArray checks = new JsonArray();
            foreach (Check c2 in _checks)
                checks.Add(new JsonObject { ["group"] = c2.Group, ["name"] = c2.Name, ["status"] = c2.Pass ? "PASS" : "DIFFERENT", ["detail"] = c2.Detail });

            JsonObject man = new JsonObject
            {
                ["present"] = _d.Manifest.Present,
                ["listed"] = _d.Manifest.Listed,
                ["matching"] = _d.Manifest.Matching,
                ["missing"] = new JsonArray(_d.Manifest.Missing.Select(x => (JsonNode?)x).ToArray()),
                ["changed"] = new JsonArray(_d.Manifest.Changed.Select(x => (JsonNode?)x).ToArray()),
                ["notFromTheBuild"] = new JsonArray(_d.Manifest.Unlisted.Select(x => (JsonNode?)x).ToArray()),
            };

            return new JsonObject
            {
                ["format"] = "seedlab-machine-report 1",
                ["tool"] = Program.ToolVersion,
                ["seedlabCommit"] = Program.SeedLabCommit,
                ["result"] = new JsonObject { ["checks"] = _checks.Count, ["pass"] = Passed, ["different"] = Different },
                ["elapsedSeconds"] = Math.Round(_d.TotalSeconds, 1),
                ["machine"] = machine,
                ["package"] = new JsonObject { ["manifest"] = man, ["runtime"] = LevelRun.RuntimeJson(_d.ParentRuntime) },
                ["reference"] = new JsonObject
                {
                    ["problem"] = _d.ReferenceProblem,
                    ["madeOn"] = _d.Reference?["madeOn"]?.DeepClone(),
                    ["definitionsHash"] = Fingerprints.DefinitionsHash(),
                },
                ["levels"] = levels,
                ["timing"] = _d.Timing?.Json?.DeepClone() ?? new JsonObject { ["error"] = FirstLines(_d.Timing?.Stderr ?? "not run", 20) },
                ["checks"] = checks,
            };
        }

        // ---- small helpers -----------------------------------------------------------------------

        private static JsonArray OnList(JsonObject? isa)
        {
            JsonArray a = new JsonArray();
            if (isa == null) return a;
            foreach (string n in Isa.Headline) if ((bool?)isa[n] == true) a.Add(n);
            return a;
        }

        private static string TierName(string tier) => tier switch
        {
            "avx512" => "AVX-512",
            "avx2" => "AVX2 (no AVX-512)",
            "avx" => "AVX (no AVX2)",
            "sse2" => "SSE only (no AVX)",
            "scalar" => "no SIMD instruction sets at all",
            _ => tier,
        };

        private static string FirstLines(string s, int n)
        {
            string[] lines = (s ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" | ", lines.Take(n).Select(x => x.Trim()));
        }

        private static string Short(string h) => h.Length >= 16 ? h.Substring(0, 16) : h;

        private static string N(int n) => n.ToString("N0", Inv);

        private static string Minutes(double s) =>
            ((int)(s / 60)).ToString(Inv) + " min " + ((int)(s % 60)).ToString(Inv) + " s";
    }
}
