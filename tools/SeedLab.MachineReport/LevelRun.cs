using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using SeedLab.Cli.Infra;
using SeedLab.Runtime.Hardware;
using SeedLab.Runtime.SelfTest;
using SeedLab.WorldGen.Unity;

namespace SeedLab.MachineReport
{
    /// <summary>
    /// The work one child process does at one instruction-set level: say which instruction sets and
    /// which runtime it really got, then run every check SeedLab has that needs no game - the machine
    /// self-test, the native-function goldens and the world fingerprints.
    /// </summary>
    public static class LevelRun
    {
        public static JsonObject Run(string levelId, Action<string> progress)
        {
            Stopwatch total = Stopwatch.StartNew();
            JsonObject o = new JsonObject { ["level"] = levelId };
            JsonArray errors = new JsonArray();
            int threads = Math.Max(1, Environment.ProcessorCount);
            o["threads"] = threads;

            // ---- what this process got -------------------------------------------------------------
            SortedDictionary<string, bool> isa = Isa.All();
            o["tier"] = Isa.Tier(isa);
            o["isa"] = Isa.ToJson(isa);
            o["vectors"] = Isa.Vectors();
            JsonObject? cpuid = Isa.Cpuid();
            if (cpuid != null) o["cpuid"] = cpuid;
            o["runtime"] = RuntimeJson(Package.ProveRuntime());
            o["settingsSeen"] = MachineFacts.InheritedSettings();

            // ---- 1. the Perlin startup self-test (runs when SeedLab.WorldGen loads) ------------------
            progress("Perlin startup self-test");
            try
            {
                string line = PerlinSelfTest.Report();
                o["perlinSelfTest"] = new JsonObject { ["ok"] = true, ["text"] = Package.Sanitize(line) };
            }
            catch (Exception e)
            {
                o["perlinSelfTest"] = new JsonObject { ["ok"] = false, ["text"] = Package.Sanitize(Describe(e)) };
            }

            // ---- 2. SeedLab's machine self-test, with the natives suite vseed registers --------------
            progress("machine self-test (recorded libm and float vectors, and the game's own values)");
            try
            {
                MachineSelfTest st = new MachineSelfTest(cache: null);   // no cache: nothing is written
                NativesGoldenSuite? nat = NativesGoldenSuite.TryCreate();
                bool natHere = nat != null && Package.IsInside(nat.Directory_, Package.NativesDir)
                               && Package.IsInside(Package.NativesDir, nat.Directory_);
                if (nat != null && natHere) st.Register(nat);
                SelfTestOutcome r = st.Verify(HardwareProbe.Probe(), force: true);
                JsonArray suites = new JsonArray();
                foreach (SelfTestSuiteResult s in r.Results)
                {
                    suites.Add(new JsonObject
                    {
                        ["name"] = s.Name,
                        ["checks"] = s.Checks,
                        ["failures"] = s.Failures,
                        ["passed"] = s.Passed,
                        ["firstFailure"] = Package.Sanitize(s.FirstFailure),
                        ["ms"] = Math.Round(s.Elapsed.TotalMilliseconds, 1),
                    });
                }

                o["machineSelfTest"] = new JsonObject
                {
                    ["status"] = r.Status.ToString(),
                    ["ok"] = r.Status == SelfTestStatus.Passed && natHere,
                    ["nativesSuiteRegistered"] = natHere,
                    ["nativesDir"] = nat == null ? "(not found)" : Package.Show(nat.Directory_),
                    ["suites"] = suites,
                    ["message"] = Package.Sanitize(r.Message),
                };
            }
            catch (Exception e)
            {
                o["machineSelfTest"] = new JsonObject { ["ok"] = false, ["status"] = "Crashed", ["message"] = Package.Sanitize(Describe(e)) };
            }

            // ---- 3. the 11-check native-function gate from SeedLab's tests ---------------------------
            progress("native-function goldens (Perlin, Random, FloatToHalf, libm, hashes)");
            o["natives"] = RunNativesGoldens();

            // ---- 4. world fingerprints --------------------------------------------------------------
            JsonArray fps = new JsonArray();
            foreach (FingerprintDef d in Fingerprints.All)
            {
                progress("fingerprint " + d.Id + ": " + d.Describe());
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    string h = Fingerprints.Compute(d, threads);
                    fps.Add(new JsonObject { ["id"] = d.Id, ["sha256"] = h, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2) });
                }
                catch (Exception e)
                {
                    fps.Add(new JsonObject { ["id"] = d.Id, ["sha256"] = "", ["error"] = Package.Sanitize(Describe(e)) });
                }
            }

            o["fingerprints"] = fps;
            o["definitionsHash"] = Fingerprints.DefinitionsHash();
            o["errors"] = errors;
            o["seconds"] = Math.Round(total.Elapsed.TotalSeconds, 1);
            return o;
        }

        private static JsonObject RunNativesGoldens()
        {
            JsonObject o = new JsonObject();
            TextWriter old = Console.Out;
            StringWriter capture = new StringWriter();
            int code;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                // The gate reads its folder from SEEDLAB_NATIVES_DIR first. Set for this process only.
                Environment.SetEnvironmentVariable("SEEDLAB_NATIVES_DIR", Package.NativesDir);
                Console.SetOut(capture);
                code = SeedLabTests.NativesGoldens.Run(new[] { "natives" });
            }
            catch (Exception e)
            {
                code = -1;
                capture.WriteLine("  FAIL  crashed                   " + Describe(e));
            }
            finally
            {
                Console.SetOut(old);
                Environment.SetEnvironmentVariable("SEEDLAB_NATIVES_DIR", null);
            }

            string text = Package.Sanitize(capture.ToString());
            JsonArray checks = new JsonArray();
            bool dirOk = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("  goldens   ", StringComparison.Ordinal))
                    dirOk = line.Trim() == "goldens   <package>\\natives" || line.Trim() == "goldens   <package>/natives";
                bool pass = line.StartsWith("  PASS  ", StringComparison.Ordinal);
                bool fail = line.StartsWith("  FAIL  ", StringComparison.Ordinal);
                if (!pass && !fail) continue;
                string rest = line.Substring(8);
                string name = rest.Length >= 26 ? rest.Substring(0, 26).Trim() : rest.Trim();
                string msg = rest.Length > 26 ? rest.Substring(26).Trim() : "";
                checks.Add(new JsonObject { ["name"] = name, ["pass"] = pass, ["message"] = msg });
            }

            o["exitCode"] = code;
            o["goldensFolderIsPackage"] = dirOk;
            o["checks"] = checks;
            o["ok"] = code == 0 && dirOk && checks.Count > 0;
            o["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2);
            if (code != 0) o["output"] = text.Length > 6000 ? text.Substring(0, 6000) : text;
            return o;
        }

        public static JsonObject RuntimeJson(Package.RuntimeProof p)
        {
            JsonArray mods = new JsonArray();
            foreach ((string m, bool inside, string where) in p.Modules)
                mods.Add(new JsonObject { ["module"] = m, ["insidePackage"] = inside, ["folder"] = where });
            return new JsonObject
            {
                ["version"] = p.Version,
                ["framework"] = p.Framework,
                ["runtimeDir"] = p.RuntimeDir,
                ["bundled"] = p.Bundled,
                ["modules"] = mods,
                ["ucrtbaseLoadedFrom"] = p.UcrtLoadedFrom,
            };
        }

        public static string Describe(Exception e)
        {
            Exception x = e;
            while ((x is TypeInitializationException || x is System.Reflection.TargetInvocationException) && x.InnerException != null)
                x = x.InnerException;
            string s = x.GetType().Name + ": " + x.Message;
            return s.Length > 2000 ? s.Substring(0, 2000) : s;
        }
    }
}
