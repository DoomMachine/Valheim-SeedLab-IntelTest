using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SeedLab.Runtime.Hardware;
using SeedLab.Runtime.Storage;

namespace SeedLab.Runtime.SelfTest
{
    /// <summary>
    /// The startup gate: on a machine or an architecture SeedLab has not verified, the bit-exact
    /// guarantee is an unproven claim, and this is what refuses to make it.
    ///
    /// <para>It runs the recorded golden vectors - its own embedded numeric suite, plus whatever
    /// generator-level suites the host registers - and FAILS CLOSED: anything but a clean pass throws
    /// from <see cref="SelfTestOutcome.EnsureUsable"/> with a message naming what to do. A pass is
    /// stamped in the cache root under a fingerprint of the machine, the runtime, the vectors and the
    /// registered suites, so it costs milliseconds once rather than on every command; changing any of
    /// those invalidates the stamp by construction.</para>
    /// </summary>
    public sealed class MachineSelfTest
    {
        private readonly CacheRoot? _cache;
        private readonly List<ISelfTestSuite> _suites = new List<ISelfTestSuite>();
        private readonly NumericVectors _numerics;

        public MachineSelfTest(CacheRoot? cache = null, NumericVectors? numerics = null)
        {
            _cache = cache;
            _numerics = numerics ?? NumericVectors.Embedded();
            _suites.Add(_numerics);
        }

        /// <summary>Architectures whose bit-exactness SeedLab's own gates have demonstrated.</summary>
        public static readonly Architecture[] VerifiedArchitectures = { Architecture.X64 };

        /// <summary>
        /// On an architecture not in <see cref="VerifiedArchitectures"/>, a pass of the numeric suite
        /// alone is NOT enough: it proves libm and float evaluation, not the generator. With this on
        /// (the default), such a machine needs a registered generator-level suite or the outcome is
        /// <see cref="SelfTestStatus.Unproven"/> and fails closed.
        /// </summary>
        public bool RequireGeneratorSuiteOnUnverifiedPlatform { get; set; } = true;

        /// <summary>Caller's explicit "I accept an unverified platform". Turns Unproven into a warning.</summary>
        public bool AcceptUnverifiedPlatform { get; set; }

        /// <summary>Turn the whole gate off. The outcome is then <see cref="SelfTestStatus.Skipped"/>.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>How long a stamp is trusted. Default: forever, since the fingerprint covers what matters.</summary>
        public TimeSpan? StampLifetime { get; set; }

        public IReadOnlyList<ISelfTestSuite> Suites => _suites;

        /// <summary>
        /// Adds a generator-level suite. The CLI registers the Perlin / Random / hash / height goldens
        /// from <c>groundtruth\natives</c>; the web host registers the same ones. Registering changes the
        /// fingerprint, so adding a suite re-runs the gate.
        /// </summary>
        public void Register(ISelfTestSuite suite)
        {
            if (suite == null) throw new ArgumentNullException(nameof(suite));
            foreach (ISelfTestSuite s in _suites)
                if (string.Equals(s.Name, suite.Name, StringComparison.Ordinal)) return;
            _suites.Add(suite);
        }

        /// <summary>True when some suite beyond the built-in numerics is registered.</summary>
        public bool HasGeneratorSuite => _suites.Count > 1;

        public static bool IsVerifiedArchitecture(Architecture a)
        {
            foreach (Architecture v in VerifiedArchitectures) if (v == a) return true;
            return false;
        }

        /// <summary>
        /// The identity a stamp is filed under: platform, runtime, ISA, vector set and suite list. Any
        /// change to any of them means the previous pass no longer says anything about this run.
        /// </summary>
        public string Fingerprint(HardwareInfo hw)
        {
            List<string> names = new List<string>();
            foreach (ISelfTestSuite s in _suites) names.Add(s.Name);
            names.Sort(StringComparer.Ordinal);

            string material = string.Join("|", new[]
            {
                hw.RuntimeIdentifier,
                hw.ProcessArchitecture.ToString(),
                hw.FrameworkDescription,
                hw.Features.Key,
                _numerics.VectorsHash,
                string.Join(",", names),
                typeof(MachineSelfTest).Assembly.GetName().Version?.ToString() ?? "0"
            });
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return Convert.ToHexString(h).Substring(0, 24).ToLowerInvariant();
        }

        /// <summary>
        /// Runs the gate (or reads a valid stamp). <paramref name="force"/> ignores the stamp.
        /// Never throws for a failure - call <see cref="SelfTestOutcome.EnsureUsable"/> for that, so the
        /// caller can print the outcome first.
        /// </summary>
        public SelfTestOutcome Verify(HardwareInfo? hardware = null, bool force = false,
                                      CancellationToken cancel = default)
        {
            HardwareInfo hw = hardware ?? HardwareProbe.Probe();
            string fp = Fingerprint(hw);
            string platform = hw.RuntimeIdentifier + " / " + hw.ProcessArchitecture + " / " + hw.FrameworkDescription;

            if (!Enabled)
            {
                return new SelfTestOutcome(SelfTestStatus.Skipped, fp, platform,
                    Array.Empty<SelfTestSuiteResult>(),
                    "the machine self-test was turned off: SeedLab's bit-exactness is UNVERIFIED on this run",
                    TimeSpan.Zero);
            }

            if (!force && ReadStamp(fp))
            {
                return new SelfTestOutcome(SelfTestStatus.PassedCached, fp, platform,
                    Array.Empty<SelfTestSuiteResult>(),
                    "self-test: this machine, runtime and vector set passed earlier (stamp " + fp + ")",
                    TimeSpan.Zero);
            }

            Stopwatch sw = Stopwatch.StartNew();
            List<SelfTestSuiteResult> results = new List<SelfTestSuiteResult>();
            foreach (ISelfTestSuite s in _suites)
            {
                SelfTestSuiteResult r;
                try { r = s.Run(cancel); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    r = new SelfTestSuiteResult(s.Name, 1, 1,
                        "the suite threw " + ex.GetType().Name + ": " + ex.Message, TimeSpan.Zero);
                }
                results.Add(r);
            }
            sw.Stop();

            List<string> failed = new List<string>();
            int checks = 0;
            foreach (SelfTestSuiteResult r in results)
            {
                checks += r.Checks;
                if (!r.Passed) failed.Add(r.ToString());
            }

            if (failed.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("SELF-TEST FAILED on ").Append(platform).Append(".\n");
                sb.Append("SeedLab reproduces Valheim's world generation bit for bit; on this machine it does not.\n");
                foreach (string f in failed) sb.Append("  ").Append(f).Append('\n');
                sb.Append("Any seed, biome or location this build reports here could be wrong.\n");
                sb.Append("What to do:\n");
                sb.Append("  - do not trust results from this machine; re-run the query on a verified x64 machine;\n");
                sb.Append("  - report the failure with this line, the failing case above and the runtime version,\n");
                sb.Append("    so the vector can be re-recorded or the port fixed for this platform;\n");
                sb.Append("  - only if you accept unverified output: pass --skip-self-test, which says so on every result.");
                return new SelfTestOutcome(SelfTestStatus.Failed, fp, platform, results, sb.ToString(), sw.Elapsed);
            }

            bool verifiedArch = IsVerifiedArchitecture(hw.ProcessArchitecture);
            if (!verifiedArch && RequireGeneratorSuiteOnUnverifiedPlatform && !HasGeneratorSuite
                && !AcceptUnverifiedPlatform)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("SELF-TEST UNPROVEN on ").Append(platform).Append(".\n");
                sb.Append("The numeric suite passed (").Append(checks).Append(" exact), so this machine's libm and ");
                sb.Append("float evaluation match the ones SeedLab was verified on.\n");
                sb.Append("But SeedLab's gates have only ever been run on x64, and no generator-level golden suite ");
                sb.Append("was registered, so nothing here proves the generator itself.\n");
                sb.Append("What to do:\n");
                sb.Append("  - run the full gates on this machine (the acceptance tests, the location gate, the ");
                sb.Append("natives goldens and GoldenCheck) and report the result;\n");
                sb.Append("  - or run the query on x64;\n");
                sb.Append("  - or pass --accept-unverified-platform to proceed, knowing the bit-exact claim is untested here.");
                return new SelfTestOutcome(SelfTestStatus.Unproven, fp, platform, results, sb.ToString(), sw.Elapsed);
            }

            WriteStamp(fp, hw, results);

            string note = verifiedArch
                ? "self-test: " + checks + " recorded values reproduced exactly in " + Bytes.Duration(sw.Elapsed)
                : "self-test: " + checks + " recorded values reproduced exactly on an architecture SeedLab has not "
                  + "otherwise verified (" + hw.ProcessArchitecture + ")";
            return new SelfTestOutcome(SelfTestStatus.Passed, fp, platform, results, note, sw.Elapsed);
        }

        // ---- the stamp ------------------------------------------------------------------------

        private string? StampPath(string fingerprint) =>
            _cache == null ? null : Path.Combine(_cache.SelfTest, "passed-" + fingerprint + ".txt");

        private bool ReadStamp(string fingerprint)
        {
            string? p = StampPath(fingerprint);
            if (p == null || !File.Exists(p)) return false;
            try
            {
                if (StampLifetime.HasValue)
                {
                    DateTime written = File.GetLastWriteTimeUtc(p);
                    if (DateTime.UtcNow - written > StampLifetime.Value) return false;
                }
                string text = File.ReadAllText(p);
                return text.Contains("result=pass", StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void WriteStamp(string fingerprint, HardwareInfo hw, IReadOnlyList<SelfTestSuiteResult> results)
        {
            string? p = StampPath(fingerprint);
            if (p == null) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("result=pass\n");
                sb.Append("fingerprint=").Append(fingerprint).Append('\n');
                sb.Append("when=").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("platform=").Append(hw.RuntimeIdentifier).Append(' ').Append(hw.ProcessArchitecture).Append('\n');
                sb.Append("runtime=").Append(hw.FrameworkDescription).Append('\n');
                sb.Append("isa=").Append(hw.Features.Key).Append('\n');
                sb.Append("vectors=").Append(_numerics.VectorsHash).Append('\n');
                foreach (SelfTestSuiteResult r in results)
                    sb.Append("suite=").Append(r.Name).Append(' ').Append(r.Checks).Append('\n');
                DurableWrite.Text(p, sb.ToString());
            }
            catch (Exception)
            {
                // A machine that cannot write the stamp simply re-runs the gate next time.
            }
        }
    }
}
