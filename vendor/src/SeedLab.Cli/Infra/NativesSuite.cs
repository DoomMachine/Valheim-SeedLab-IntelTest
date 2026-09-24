using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using SeedLab.Runtime.SelfTest;
using SeedLab.Seeds;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// The generator-level golden suite <c>MachineSelfTest</c> asks for, wrapping the recorded natives
    /// goldens in <c>groundtruth\natives</c>.
    ///
    /// <para><b>Why it has to exist here.</b> <c>SeedLab.Runtime</c> ships one suite of its own - libm
    /// and float evaluation - and that is deliberately all it can prove, because the project has no
    /// dependency on the generator. On x64 that is enough, because SeedLab's own gates have been run
    /// on x64. On any other architecture the numeric suite passing means only "this machine's libm
    /// agrees"; nothing has shown that the PORT reproduces the game, so
    /// <see cref="SelfTestStatus.Unproven"/> is the honest answer and it fails closed. A machine stuck
    /// there could not run <c>vseed</c> at all. Registering this suite is the way out: it replays the
    /// values the GAME ITSELF produced - Mathf.PerlinNoise samples, Math.Sin/Cos/Atan2/Pow results
    /// recorded under Mono, WorldGenerator.WorldAngle and every prefab name's GetStableHashCode - and
    /// a machine that reproduces them bit for bit has proved the thing the status doubted.</para>
    ///
    /// <para>It is registered ONLY when the goldens are actually on disk. A suite that threw "file not
    /// found" would be counted as a FAILURE by <c>MachineSelfTest</c>, which would fail-close a
    /// perfectly good x64 install that happens to ship without <c>groundtruth\</c>. Absent goldens are
    /// the caller's problem to report (see <see cref="CliRuntime"/>), not a divergence.</para>
    ///
    /// <para>The same four bodies of goldens are checked exhaustively by
    /// <c>tests\SeedLab.Tests -- natives</c>, which prints per-block detail and ULP distances. This is
    /// the same data read for a different purpose: a yes/no gate with one first-failure line, fast
    /// enough to run before a command rather than as a test pass.</para>
    /// </summary>
    public sealed class NativesGoldenSuite : ISelfTestSuite
    {
        private readonly string _dir;

        private NativesGoldenSuite(string dir) => _dir = dir;

        /// <summary>The four files this suite replays. All four must be present for it to be usable.</summary>
        public static readonly string[] RequiredFiles =
        {
            "natives-perlin.bin", "natives-perlin.json", "natives-libm.json", "natives-hash.json"
        };

        /// <summary>
        /// <c>groundtruth\natives</c>, found the way <see cref="Verified.FindGroundTruth"/> finds the
        /// rest of the ground truth, or null when it is not beside this build.
        /// </summary>
        public static string? FindDirectory()
        {
            string? gt = Verified.FindGroundTruth();
            if (gt != null)
            {
                string cand = Path.Combine(gt, "natives");
                if (HasAll(cand)) return cand;
            }

            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                DirectoryInfo? d = new DirectoryInfo(start);
                for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                {
                    string cand = Path.Combine(d.FullName, "groundtruth", "natives");
                    if (HasAll(cand)) return cand;
                }
            }

            return null;
        }

        private static bool HasAll(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                foreach (string f in RequiredFiles)
                {
                    if (!File.Exists(Path.Combine(dir, f))) return false;
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The suite, or null when the goldens are not on this machine.</summary>
        public static NativesGoldenSuite? TryCreate()
        {
            string? dir = FindDirectory();
            return dir == null ? null : new NativesGoldenSuite(dir);
        }

        /// <summary>
        /// Stable, and it goes into the self-test fingerprint - so adding this suite invalidates a
        /// stamp written without it, which is exactly right: that stamp proved less.
        /// </summary>
        public string Name => "seedlab/natives";

        public string Describes =>
            "Mathf.PerlinNoise, Mono's Sin/Cos/Atan2/Pow, WorldGenerator.WorldAngle and "
            + "GetStableHashCode, replayed against values the game itself produced. A divergence here "
            + "moves biome boundaries and location placement.";

        public string Directory_ => _dir;

        public SelfTestSuiteResult Run(CancellationToken cancel)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int checks = 0, failures = 0;
            string first = "";

            void Note(string line)
            {
                failures++;
                if (first.Length == 0) first = line;
            }

            // ---- 1. Mathf.PerlinNoise, the single largest body of recorded values -----------------
            try
            {
                byte[] bin = File.ReadAllBytes(Path.Combine(_dir, "natives-perlin.bin"));
                using JsonDocument idx = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "natives-perlin.json")));

                if (bin.Length < 12 || bin[0] != 'V' || bin[1] != 'P' || bin[2] != 'L' || bin[3] != '1')
                {
                    checks++;
                    Note("natives-perlin.bin does not start with the magic VPL1 - the goldens are damaged");
                }
                else
                {
                    foreach (JsonElement b in idx.RootElement.GetProperty("blocks").EnumerateArray())
                    {
                        cancel.ThrowIfCancellationRequested();
                        string id = b.GetProperty("id").GetString() ?? "?";
                        int kind = b.GetProperty("kind").GetInt32();
                        int count = b.GetProperty("sampleCount").GetInt32();
                        long off = b.GetProperty("byteOffset").GetInt64();

                        for (int i = 0; i < count; i++)
                        {
                            int p = checked((int)(off + (long)i * 12));
                            float x = BitConverter.ToSingle(bin, p);
                            float y = BitConverter.ToSingle(bin, p + 4);
                            int want = BitConverter.ToInt32(bin, p + 8);
                            float got = kind == 1 ? UnityPerlin.PerlinNoise1D(x) : UnityPerlin.PerlinNoise(x, y);
                            int gotBits = BitConverter.SingleToInt32Bits(got);
                            checks++;
                            if (gotBits != want)
                            {
                                Note("PerlinNoise " + id + "[" + i.ToString(CultureInfo.InvariantCulture) + "] ("
                                     + R(x) + ", " + R(y) + "): the game recorded " + Hex32(want)
                                     + ", this machine computes " + Hex32(gotBits));
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                checks++;
                Note("the Perlin goldens could not be read: " + ex.GetType().Name + ": " + ex.Message);
            }

            // ---- 2. libm, as Mono produced it, plus the WorldAngle composite ----------------------
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "natives-libm.json")));
                foreach (JsonElement s in doc.RootElement.GetProperty("samples").EnumerateArray())
                {
                    double a = BitConverter.Int64BitsToDouble(unchecked((long)Bits64(s, "a")));
                    double b = BitConverter.Int64BitsToDouble(unchecked((long)Bits64(s, "b")));
                    long want = unchecked((long)Bits64(s, "result"));
                    string fn = s.GetProperty("fn").GetString() ?? "?";
                    double got = fn switch
                    {
                        "Sin" => Math.Sin(a),
                        "Cos" => Math.Cos(a),
                        "Atan2" => Math.Atan2(a, b),
                        "Pow" => Math.Pow(a, b),
                        _ => double.NaN,
                    };
                    long gotBits = BitConverter.DoubleToInt64Bits(got);
                    checks++;
                    if (gotBits != want)
                    {
                        Note("Math." + fn + "(" + R(a) + (fn is "Atan2" or "Pow" ? ", " + R(b) : "") + "): Mono gave "
                             + Hex64(want) + ", this runtime gives " + Hex64(gotBits));
                    }
                }

                foreach (JsonElement s in doc.RootElement.GetProperty("worldAngle").EnumerateArray())
                {
                    float wx = BitConverter.Int32BitsToSingle(unchecked((int)Bits32(s, "wx")));
                    float wy = BitConverter.Int32BitsToSingle(unchecked((int)Bits32(s, "wy")));
                    int want = unchecked((int)Bits32(s, "result"));
                    int got = BitConverter.SingleToInt32Bits(WorldGeneratorPort.WorldAngle(wx, wy));
                    checks++;
                    if (got != want)
                    {
                        Note("WorldGenerator.WorldAngle(" + R(wx) + ", " + R(wy) + "): the game recorded "
                             + Hex32(want) + ", this machine computes " + Hex32(got));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                checks++;
                Note("the libm goldens could not be read: " + ex.GetType().Name + ": " + ex.Message);
            }

            // ---- 3. GetStableHashCode over every prefab name and seed text ------------------------
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "natives-hash.json")));
                foreach (JsonElement s in doc.RootElement.GetProperty("samples").EnumerateArray())
                {
                    string str = s.GetProperty("s").GetString() ?? "";
                    int want = s.GetProperty("hash").GetInt32();
                    int got = StableHash.Compute(str);
                    checks++;
                    if (got != want)
                    {
                        Note("GetStableHashCode(\"" + str + "\"): the game recorded " + want
                             + ", this machine computes " + got);
                    }

                    // The even/odd lane split is what the vectorised path uses; if it stops
                    // recombining to the scalar hash, one location type's whole RNG stream moves.
                    (uint even, uint odd) = StableHash.ComputeLanes(str);
                    int recombined = unchecked((int)(even + odd * 1566083941u));
                    checks++;
                    if (recombined != got)
                    {
                        Note("the lane split of \"" + str + "\" recombines to " + recombined + ", not " + got);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                checks++;
                Note("the hash goldens could not be read: " + ex.GetType().Name + ": " + ex.Message);
            }

            sw.Stop();
            return new SelfTestSuiteResult(Name, checks, failures, first, sw.Elapsed);
        }

        // The goldens carry both a decimal and a "bits" form; the bits are authoritative (DumpFormat:
        // "the decimal form is a human convenience; a reader must use the bits").
        private static ulong Bits64(JsonElement o, string member)
            => ParseHex(o.GetProperty("bits").GetProperty(member).GetString());

        private static uint Bits32(JsonElement o, string member)
            => (uint)ParseHex(o.GetProperty("bits").GetProperty(member).GetString());

        private static ulong ParseHex(string? s)
        {
            string t = (s ?? "0").Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            return ulong.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static string Hex32(int bits) => "0x" + bits.ToString("X8", CultureInfo.InvariantCulture);
        private static string Hex64(long bits) => "0x" + bits.ToString("X16", CultureInfo.InvariantCulture);
        private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    }
}
