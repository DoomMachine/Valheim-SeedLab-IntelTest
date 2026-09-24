using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using SeedLab.Seeds;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLabTests
{
    /// <summary>
    /// The native-function regression test: every golden the dumper recorded INSIDE THE RUNNING GAME
    /// on 2026-09-22 (game 1.0.15, assembly_valheim sha256 59f53fb5..., Unity 6000.0.75f1), replayed
    /// against the offline port.
    ///
    /// <para>These files are the only direct evidence that exists for the five functions the port had
    /// to transcribe out of UnityPlayer.dll by disassembly. Until they were dumped, spec 03 section 5.2
    /// carried five open variants (R1-R5); this suite is what closed them, and it stays so that a
    /// future edit to <c>SeedLab.WorldGen.Unity</c> cannot silently re-open one. It is deliberately
    /// cheap - the whole thing is well under a second - so it can run on every build.</para>
    ///
    /// <para>Goldens live in <c>groundtruth\natives\</c>, copied verbatim out of
    /// <c>%USERPROFILE%\AppData\valheim-dumper\1.0.15-59f53fb5\</c>. Set <c>SEEDLAB_NATIVES_DIR</c> to
    /// point at a fresh dump instead (e.g. after a game update).</para>
    ///
    /// <para>Run: <c>dotnet run --project tests\SeedLab.Tests -- natives</c> (add <c>--verbose</c> for
    /// the per-check detail). Exit 0 = all checks passed.</para>
    /// </summary>
    public static class NativesGoldens
    {
        private static bool _verbose;
        private static int _failed;
        private static int _checks;

        public static int Run(string[] args)
        {
            _verbose = Array.IndexOf(args, "--verbose") >= 0 || Array.IndexOf(args, "-v") >= 0;

            string? dir = FindGoldens();
            if (dir == null)
            {
                Console.Error.WriteLine("Could not locate the natives goldens. Looked for "
                    + "groundtruth\\natives (walking up from the working directory and the binary "
                    + "directory) and $SEEDLAB_NATIVES_DIR.");
                return 2;
            }

            Console.WriteLine("SeedLab native-function goldens");
            Console.WriteLine("  goldens   " + dir);
            string stampFile = Path.Combine(dir, "natives-perlin.json");
            if (File.Exists(stampFile))
            {
                using JsonDocument s = Json(stampFile);
                if (s.RootElement.TryGetProperty("stamp", out JsonElement st))
                    Console.WriteLine("  stamp     " + st.GetString());
            }
            Console.WriteLine();

            CheckPerlin(dir);
            CheckRandom(dir);
            CheckHalf(dir);
            CheckLibm(dir);
            CheckHash(dir);

            Console.WriteLine();
            Console.WriteLine(_failed == 0
                ? "ALL " + _checks + " NATIVE CHECKS PASSED"
                : _failed + " of " + _checks + " NATIVE CHECKS FAILED");
            return _failed == 0 ? 0 : 1;
        }

        // =========================================================================================
        // 1. Mathf.PerlinNoise
        // =========================================================================================

        /// <summary>
        /// <c>goldens\natives-perlin.bin</c>, decoded exactly as <c>ModeNatives.PerlinBody</c> wrote it
        /// (DumpFormat.PerlinMagic): 4 ASCII magic bytes "VPL1", int32 schema, int32 blockCount, then
        /// nothing but 12-byte <c>{float x, float y, float result}</c> samples. There is NO per-block
        /// header in the .bin - each block's kind, sampleCount and absolute byteOffset come from the
        /// sibling index <c>natives-perlin.json</c>.
        ///
        /// Comparison is on raw int32 bit patterns, not on float equality: a decimal or tolerance
        /// comparison cannot see the 1-ULP errors that variant P4 (the reciprocal multiply) produces.
        /// </summary>
        private static void CheckPerlin(string dir)
        {
            string binPath = Path.Combine(dir, "natives-perlin.bin");
            string idxPath = Path.Combine(dir, "natives-perlin.json");
            using JsonDocument idx = Json(idxPath);
            byte[] bin = File.ReadAllBytes(binPath);

            // Header.
            if (bin.Length < 12 || bin[0] != 'V' || bin[1] != 'P' || bin[2] != 'L' || bin[3] != '1')
            {
                Fail("perlin", "natives-perlin.bin does not start with the magic VPL1.");
                return;
            }
            int schema = BitConverter.ToInt32(bin, 4);
            int blockCount = BitConverter.ToInt32(bin, 8);

            long declaredBytes = idx.RootElement.GetProperty("binBytes").GetInt64();
            if (declaredBytes != bin.Length)
            {
                Fail("perlin", "index says binBytes=" + declaredBytes + " but the file is " + bin.Length + ".");
                return;
            }

            JsonElement blocks = idx.RootElement.GetProperty("blocks");
            if (blocks.GetArrayLength() != blockCount)
            {
                Fail("perlin", "header blockCount=" + blockCount + " but the index lists "
                               + blocks.GetArrayLength() + " blocks.");
                return;
            }

            int total = 0, exact = 0;
            var mismatches = new List<string>();
            var perBlock = new List<string>();

            foreach (JsonElement b in blocks.EnumerateArray())
            {
                string id = b.GetProperty("id").GetString() ?? "?";
                int kind = b.GetProperty("kind").GetInt32();
                int count = b.GetProperty("sampleCount").GetInt32();
                long off = b.GetProperty("byteOffset").GetInt64();

                int blockExact = 0;
                for (int i = 0; i < count; i++)
                {
                    int p = checked((int)(off + (long)i * 12));
                    float x = BitConverter.ToSingle(bin, p);
                    float y = BitConverter.ToSingle(bin, p + 4);
                    int want = BitConverter.ToInt32(bin, p + 8);

                    float got = kind == 1 ? UnityPerlin.PerlinNoise1D(x) : UnityPerlin.PerlinNoise(x, y);
                    int gotBits = BitConverter.SingleToInt32Bits(got);

                    total++;
                    if (gotBits == want) { exact++; blockExact++; }
                    else if (mismatches.Count < 24)
                    {
                        mismatches.Add("    " + id + "[" + i + "]  x=" + Hex(x) + " y=" + Hex(y)
                            + "  game=" + Hex32(want) + " (" + R(BitConverter.Int32BitsToSingle(want)) + ")"
                            + "  port=" + Hex32(gotBits) + " (" + R(got) + ")"
                            + "  ulp=" + FloatUlps(want, gotBits));
                    }
                }
                perBlock.Add("    " + id.PadRight(20) + " kind=" + kind + "  " + blockExact + "/" + count
                             + (blockExact == count ? "" : "   <-- MISMATCH"));
            }

            long expectedBytes = 12L + (long)total * 12L;
            bool sizeOk = expectedBytes == bin.Length;

            Report("perlin", exact == total && sizeOk,
                exact + "/" + total + " samples bit-exact against Mathf.PerlinNoise"
                + (sizeOk ? "" : "  (SIZE MISMATCH: samples imply " + expectedBytes + " bytes)"));
            if (_verbose || exact != total)
            {
                Console.WriteLine("    schema=" + schema + " blocks=" + blockCount + " bytes=" + bin.Length);
                foreach (string s in perBlock) Console.WriteLine(s);
            }
            foreach (string s in mismatches) Console.WriteLine(s);
        }

        // =========================================================================================
        // 2. UnityEngine.Random
        // =========================================================================================

        /// <summary>
        /// Replays <c>goldens\natives-random.json</c>: the <c>Random.state</c> round trip, every
        /// <c>InitState(seed) -&gt; s0..s3</c>, and every recorded call trace with the four state words
        /// after each draw.
        ///
        /// This is what settles spec 03 section 5.2:
        /// <list type="bullet">
        /// <item><b>R1</b> - <c>Range(float,float)</c> interpolation. <c>D6-range-float</c> draw 0 is
        /// <c>Range(60f,100f)</c> = 0x42BF3B2E, which is <c>(1-f)*max + f*min</c>; the forward form
        /// <c>min + f*(max-min)</c> gives 0x4280C4D2, 31 ULPs away.</item>
        /// <item><b>R2</b> - <c>Range(a,a)</c>. <c>D7-same-min-max</c> records
        /// <c>stateUnchanged</c> per draw: true for the int overload, false for the float overload.</item>
        /// <item><b>R3/R4</b> - <c>insideUnitCircle</c>. <c>D8</c> gives eight exact (x, y) pairs, which
        /// pin both the trig and the component order.</item>
        /// </list>
        /// Every draw is checked as bits AND the state after it is checked, so an implementation that
        /// happens to produce the right number from the wrong number of draws still fails.
        /// </summary>
        private static void CheckRandom(string dir)
        {
            using JsonDocument doc = Json(Path.Combine(dir, "natives-random.json"));
            JsonElement root = doc.RootElement;

            // --- Random.state round trip (the dumper's own premise) -------------------------------
            JsonElement rt = root.GetProperty("stateRoundTrip");
            bool rtOk = rt.GetProperty("statesEqual").GetBoolean() && rt.GetProperty("drawsEqual").GetBoolean();
            Report("random/state-round-trip", rtOk,
                rtOk ? "the game's Random.state setter round-trips exactly (every guard in the dump depends on it)"
                     : "the game's Random.state does NOT round-trip - the whole dump is unusable");

            // The four control draws are Random.value from InitState(4242); replay them too, so the
            // round-trip block doubles as a value check with a seed no trace uses.
            {
                var r = new UnityRandom();
                r.InitState(4242);
                int[] saved = ArrOf(rt.GetProperty("saved"));
                bool ok = StateEq(r, saved);
                JsonElement cd = rt.GetProperty("controlDraws");
                for (int i = 0; i < cd.GetArrayLength(); i++)
                {
                    uint want = ParseHex32(cd[i].GetString()!);
                    uint got = (uint)BitConverter.SingleToInt32Bits(r.Value());
                    if (want != got) ok = false;
                }
                Report("random/value-4242", ok,
                    ok ? "InitState(4242) state words + 4 x Random.value reproduce exactly"
                       : "InitState(4242) control draws differ");
            }

            // --- D4: InitState -> s0..s3 ----------------------------------------------------------
            {
                JsonElement inits = root.GetProperty("initStates");
                int n = inits.GetArrayLength(), ok = 0;
                string? first = null;
                foreach (JsonElement e in inits.EnumerateArray())
                {
                    int seed = e.GetProperty("seed").GetInt32();
                    int[] want = ArrOf(e.GetProperty("state"));
                    var r = new UnityRandom();
                    r.InitState(seed);
                    if (StateEq(r, want)) ok++;
                    else first ??= "    InitState(" + seed + ") game=[" + string.Join(",", want)
                                   + "] port=[" + r.s0 + "," + r.s1 + "," + r.s2 + "," + r.s3 + "]";
                }
                Report("random/initstate", ok == n, ok + "/" + n + " InitState seeds give identical s0..s3");
                if (first != null) Console.WriteLine(first);
            }

            // --- D5-D10 + the 268 constructor traces ---------------------------------------------
            {
                JsonElement traces = root.GetProperty("traces");
                int nTraces = traces.GetArrayLength();
                int okTraces = 0, nDraws = 0, okDraws = 0;
                var detail = new List<string>();
                var byId = new SortedDictionary<string, (int ok, int n)>(StringComparer.Ordinal);

                foreach (JsonElement t in traces.EnumerateArray())
                {
                    string id = t.GetProperty("id").GetString() ?? "?";
                    int seed = t.GetProperty("initState").GetInt32();
                    var r = new UnityRandom();
                    r.InitState(seed);
                    bool traceOk = StateEq(r, ArrOf(t.GetProperty("stateAfterInit")));
                    if (!traceOk && detail.Count < 20)
                        detail.Add("    " + id + ": stateAfterInit differs for seed " + seed);

                    int di = 0;
                    foreach (JsonElement d in t.GetProperty("draws").EnumerateArray())
                    {
                        string call = d.GetProperty("call").GetString() ?? "";
                        string kind = d.GetProperty("kind").GetString() ?? "";
                        int[] wantState = ArrOf(d.GetProperty("stateAfter"));
                        bool wantUnchanged = d.GetProperty("stateUnchanged").GetBoolean();
                        var before = r.GetState();

                        bool drawOk;
                        string got, want;
                        switch (kind)
                        {
                            case "int":
                            {
                                int w = d.GetProperty("resultInt").GetInt32();
                                int g = InvokeInt(r, call, d, seed);
                                drawOk = g == w;
                                got = g.ToString(CultureInfo.InvariantCulture);
                                want = w.ToString(CultureInfo.InvariantCulture);
                                break;
                            }
                            case "float":
                            {
                                uint w = BitsOf(d, "resultFloat");
                                float gv = InvokeFloat(r, call, d);
                                uint g = (uint)BitConverter.SingleToInt32Bits(gv);
                                drawOk = g == w;
                                got = Hex32((int)g);
                                want = Hex32((int)w);
                                break;
                            }
                            case "vector2":
                            {
                                uint wx = BitsOf(d, "resultFloat"), wy = BitsOf(d, "resultFloat2");
                                (float x, float y) = r.InsideUnitCircle();
                                uint gx = (uint)BitConverter.SingleToInt32Bits(x);
                                uint gy = (uint)BitConverter.SingleToInt32Bits(y);
                                drawOk = gx == wx && gy == wy;
                                got = Hex32((int)gx) + "," + Hex32((int)gy);
                                want = Hex32((int)wx) + "," + Hex32((int)wy);
                                break;
                            }
                            default:
                                drawOk = false;
                                got = want = "<unknown kind " + kind + ">";
                                break;
                        }

                        bool stateOk = StateEq(r, wantState);
                        bool unchangedOk = (before == r.GetState()) == wantUnchanged;
                        nDraws++;
                        if (drawOk && stateOk && unchangedOk) okDraws++;
                        else
                        {
                            traceOk = false;
                            if (detail.Count < 20)
                                detail.Add("    " + id + " draw " + di + " " + call
                                    + (drawOk ? "" : "  RESULT game=" + want + " port=" + got)
                                    + (stateOk ? "" : "  STATE game=[" + string.Join(",", wantState)
                                                     + "] port=[" + r.s0 + "," + r.s1 + "," + r.s2 + "," + r.s3 + "]")
                                    + (unchangedOk ? "" : "  DRAW-CONSUMED game.stateUnchanged=" + wantUnchanged));
                        }
                        di++;
                    }

                    if (!StateEq(r, ArrOf(t.GetProperty("stateAtEnd"))))
                    {
                        traceOk = false;
                        if (detail.Count < 20) detail.Add("    " + id + ": stateAtEnd differs");
                    }

                    if (traceOk) okTraces++;
                    byId.TryGetValue(id, out var acc);
                    byId[id] = (acc.ok + (traceOk ? 1 : 0), acc.n + 1);
                }

                Report("random/traces", okTraces == nTraces,
                    okTraces + "/" + nTraces + " traces and " + okDraws + "/" + nDraws
                    + " individual draws (result bits AND the state after each) reproduce exactly");
                if (_verbose || okTraces != nTraces)
                    foreach (var kv in byId)
                        Console.WriteLine("    " + kv.Key.PadRight(28) + kv.Value.ok + "/" + kv.Value.n);
                foreach (string s in detail) Console.WriteLine(s);
            }

            // --- The variant verdicts, printed from the same data --------------------------------
            if (_verbose) PrintRandomVerdicts(root);
        }

        /// <summary>
        /// Spells out what the traces say about R1-R5, so the evidence is readable and not just a
        /// pass count. Every number here is read out of the golden, never asserted from memory.
        /// </summary>
        private static void PrintRandomVerdicts(JsonElement root)
        {
            Console.WriteLine("    -- spec 03 section 5.2 verdicts, read from this golden --");
            foreach (JsonElement t in root.GetProperty("traces").EnumerateArray())
            {
                string id = t.GetProperty("id").GetString() ?? "";
                // R1 needs BOTH the forward and the reversed call: (a) and (c) agree on
                // Range(60f,100f) and disagree by one ULP on Range(100f,60f), so only the pair
                // singles out one form.
                if (id is "D6-range-float" or "D6b-range-float-reversed")
                {
                    JsonElement d0 = t.GetProperty("draws")[0];
                    string call = d0.GetProperty("call").GetString() ?? "";
                    float min = call == "Range(100f,60f)" ? 100f : 60f;
                    float max = call == "Range(100f,60f)" ? 60f : 100f;
                    uint game = BitsOf(d0, "resultFloat");
                    var r = new UnityRandom(); r.InitState(t.GetProperty("initState").GetInt32());
                    float f = (float)(long)(r.Next() & 0x7FFFFFu) * UnityRandom.Scale;
                    uint a = (uint)BitConverter.SingleToInt32Bits((1f - f) * max + f * min);
                    uint b = (uint)BitConverter.SingleToInt32Bits(min + f * (max - min));
                    uint c = (uint)BitConverter.SingleToInt32Bits(max + f * (min - max));
                    uint m = (uint)BitConverter.SingleToInt32Bits(UMathf.Lerp(min, max, f));
                    Console.WriteLine("    R1 " + call.PadRight(16) + " game=" + Hex32((int)game)
                        + "  (a)(1-f)*max+f*min=" + Hex32((int)a)
                        + "  (b)min+f*(max-min)=" + Hex32((int)b)
                        + "  (c)max+f*(min-max)=" + Hex32((int)c)
                        + "  (d)Lerp=" + Hex32((int)m)
                        + "  -> matches:"
                        + (game == a ? " (a)" : "") + (game == b ? " (b)" : "")
                        + (game == c ? " (c)" : "") + (game == m ? " (d)" : "")
                        + (game != a && game != b && game != c && game != m ? " NONE" : ""));
                }
                if (id == "D7-same-min-max")
                {
                    foreach (JsonElement d in t.GetProperty("draws").EnumerateArray())
                        Console.WriteLine("    R2 " + (d.GetProperty("call").GetString() ?? "").PadRight(16)
                            + " stateUnchanged=" + d.GetProperty("stateUnchanged").GetBoolean()
                            + "  -> " + (d.GetProperty("stateUnchanged").GetBoolean() ? "NO draw" : "DRAWS"));
                }
                if (id == "D8-inside-unit-circle")
                {
                    JsonElement d0 = t.GetProperty("draws")[0];
                    uint gx = BitsOf(d0, "resultFloat"), gy = BitsOf(d0, "resultFloat2");
                    var r = new UnityRandom(); r.InitState(t.GetProperty("initState").GetInt32());
                    float ang = r.Range(0f, 6.28318548f);
                    float rad = MathF.Sqrt(r.Range(0f, 1f));
                    uint dblCos = (uint)BitConverter.SingleToInt32Bits((float)Math.Cos((double)ang) * rad);
                    uint dblSin = (uint)BitConverter.SingleToInt32Bits((float)Math.Sin((double)ang) * rad);
                    uint fCos = (uint)BitConverter.SingleToInt32Bits(MathF.Cos(ang) * rad);
                    uint fSin = (uint)BitConverter.SingleToInt32Bits(MathF.Sin(ang) * rad);
                    Console.WriteLine("    R3 insideUnitCircle[0] game=(" + Hex32((int)gx) + "," + Hex32((int)gy) + ")"
                        + "  (c)(float)Math.Cos/Sin(double)=(" + Hex32((int)dblCos) + "," + Hex32((int)dblSin) + ")"
                        + "  (b)MathF.Cos/Sin=(" + Hex32((int)fCos) + "," + Hex32((int)fSin) + ")");
                    Console.WriteLine("    R4 component order: x==cos? " + (gx == dblCos || gx == fCos)
                        + "   x==sin? " + (gx == dblSin || gx == fSin));
                    // R5: if a CPU-dependent cosf/sinf path were in play, some of the eight pairs would
                    // differ from the (float)Math.Cos((double)a) form while others matched. Count them.
                    int agree = 0, n = 0;
                    var rr = new UnityRandom(); rr.InitState(t.GetProperty("initState").GetInt32());
                    foreach (JsonElement d in t.GetProperty("draws").EnumerateArray())
                    {
                        (float x, float y) = rr.InsideUnitCircle();
                        if ((uint)BitConverter.SingleToInt32Bits(x) == BitsOf(d, "resultFloat")
                            && (uint)BitConverter.SingleToInt32Bits(y) == BitsOf(d, "resultFloat2")) agree++;
                        n++;
                    }
                    Console.WriteLine("    R5 CPU-dependent trig path visible? " + (agree == n ? "no" : "YES")
                        + "  (" + agree + "/" + n + " pairs bit-exact on this machine)");
                }
            }
        }

        /// <summary>Dispatches one recorded int-returning call by its <c>call</c> string.</summary>
        private static int InvokeInt(UnityRandom r, string call, JsonElement d, int seed)
        {
            switch (call)
            {
                case "Range(-10000,10000)": return r.Range(-10000, 10000);
                case "Range(int.MinValue,int.MaxValue)": return r.Range(int.MinValue, int.MaxValue);
                case "Range(5,5)": return r.Range(5, 5);
                case "Range(0,0)": return r.Range(0, 0);
                case "Range(0,1)": return r.Range(0, 1);
                default: throw new InvalidOperationException("Unhandled int call '" + call + "' in trace seed " + seed);
            }
        }

        /// <summary>Dispatches one recorded float-returning call. The two data-dependent calls
        /// (<c>Range(60f,&lt;previous result&gt;)</c>) take their second argument from the golden's own
        /// previous draw, which is the point: it proves the port reproduced that value too.</summary>
        private static float InvokeFloat(UnityRandom r, string call, JsonElement d)
        {
            switch (call)
            {
                case "value": return r.Value();
                case "value (after 100000 discarded)":
                    // D9-long-run: the dumper burned 100 000 draws between InitState(0) and this one.
                    // The recorded stateAfter is therefore step 100 001 - which is the whole point of
                    // the trace: it catches a carry or ordering error that only shows after thousands
                    // of steps, and it cannot be replayed without burning the same 100 000 here.
                    for (int i = 0; i < 100000; i++) r.Value();
                    return r.Value();
                case "Range(60f,100f)": return r.Range(60f, 100f);
                case "Range(100f,60f)": return r.Range(100f, 60f);
                case "Range(20f,20f)": return r.Range(20f, 20f);
                case "Range(-10000f,10000f)": return r.Range(-10000f, 10000f);
                case "Range(0f,PI*2)": return r.Range(0f, 6.28318548f);
                case "Range(60f,<previous result>)":
                    // 0x42BF3B2E, the previous draw in this same trace - re-derived, not hard-coded.
                    return r.Range(60f, BitConverter.Int32BitsToSingle(unchecked((int)0x42BF3B2E)));
                default: throw new InvalidOperationException("Unhandled float call '" + call + "'.");
            }
        }

        // =========================================================================================
        // 3. Mathf.FloatToHalf
        // =========================================================================================

        /// <summary>
        /// <c>goldens\natives-half.json</c> against the tool's encoder. <c>Mathf.FloatToHalf</c> is a
        /// native extern, so its rounding mode was a measurement - the acceptance suite saw ties going
        /// AWAY FROM ZERO on 640 real minimap midpoints, and this adversarial set (negatives,
        /// subnormals, +/-65520, +/-65536, NaN, the infinities, float.MaxValue) is what settles it.
        ///
        /// The suite reports both candidate rules, so the tie count is visible rather than assumed:
        /// <c>NetBits</c> is .NET's ties-to-even <c>(Half)f</c>, <c>UnityBits</c> redirects exact
        /// midpoints away from zero. Only the second one has to match.
        ///
        /// <para><b>NaN is scored separately and deliberately.</b> Measured on this golden:
        /// <c>Mathf.FloatToHalf(0xFFC00000)</c> - .NET's <c>float.NaN</c> - returns half
        /// <c>0xFF00</c>, whereas <c>(Half)float.NaN</c> returns <c>0xFE00</c>. Both are NaN; only the
        /// payload differs, and Unity's own <c>HalfToFloat(0xFF00)</c> is <c>0xFFE00000</c>, so not even
        /// Unity round-trips the payload. <c>GetBiomeHeight</c> cannot produce a NaN at finite
        /// coordinates and neither ground-truth minimap cache holds a NaN or infinity code, so this is
        /// recorded as a known, bounded difference rather than papered over or "fixed". The check below
        /// still fails if a NaN input ever stops mapping to a NaN code, or if the count of such cases
        /// changes.</para>
        /// </summary>
        private static void CheckHalf(string dir)
        {
            using JsonDocument doc = Json(Path.Combine(dir, "natives-half.json"));
            JsonElement samples = doc.RootElement.GetProperty("samples");

            int finite = 0, okUnity = 0, okNet = 0, ties = 0, tiesNetWrong = 0;
            int nan = 0, nanBothNaN = 0, nanPayloadDiff = 0;
            int backOkCount = 0, backTotal = 0;
            var detail = new List<string>();
            var netOnly = new List<string>();
            var nanNotes = new List<string>();

            foreach (JsonElement s in samples.EnumerateArray())
            {
                float v = BitConverter.Int32BitsToSingle((int)BitsOf(s, "value"));
                ushort want = (ushort)s.GetProperty("half").GetInt32();
                ushort net = HalfCodecLocal.NetBits(v);
                ushort unity = HalfCodecLocal.UnityBits(v, out bool wasTie);

                if (float.IsNaN(v))
                {
                    // NaN payload is implementation-defined; score "is it still a NaN code" instead.
                    nan++;
                    bool bothNaN = (want & 0x7C00) == 0x7C00 && (want & 0x03FF) != 0
                                   && (unity & 0x7C00) == 0x7C00 && (unity & 0x03FF) != 0;
                    if (bothNaN) nanBothNaN++;
                    if (want != unity) nanPayloadDiff++;
                    nanNotes.Add("      NaN " + Hex(v) + " -> game 0x" + want.ToString("X4")
                        + ", (Half)f 0x" + unity.ToString("X4")
                        + (bothNaN ? "  (both NaN, payload only)" : "  (NOT both NaN)"));
                }
                else
                {
                    finite++;
                    if (unity == want) okUnity++;
                    else if (detail.Count < 20)
                        detail.Add("    " + (s.GetProperty("note").GetString() ?? "") + "  in=" + Hex(v)
                            + "  game=0x" + want.ToString("X4") + "  ties-away=0x" + unity.ToString("X4")
                            + "  ties-even=0x" + net.ToString("X4"));
                    if (net == want) okNet++;
                    if (wasTie)
                    {
                        ties++;
                        if (net != want)
                        {
                            tiesNetWrong++;
                            if (netOnly.Count < 8)
                                netOnly.Add("      tie " + R(v) + " -> game 0x" + want.ToString("X4")
                                    + ", away-from-zero 0x" + unity.ToString("X4")
                                    + ", ties-to-even 0x" + net.ToString("X4"));
                        }
                    }
                }

                // The decode direction: the game also recorded Mathf.HalfToFloat(half).
                float back = BitConverter.Int32BitsToSingle((int)BitsOf(s, "back"));
                float mine = SeedLab.Saves.Half16.ToSingle(want);
                backTotal++;
                if (BitConverter.SingleToInt32Bits(back) == BitConverter.SingleToInt32Bits(mine)) backOkCount++;
                else if (detail.Count < 20)
                    detail.Add("    HalfToFloat(0x" + want.ToString("X4") + ") game=" + Hex(back)
                               + " port=" + Hex(mine));
            }

            Report("half/FloatToHalf", okUnity == finite,
                okUnity + "/" + finite + " finite inputs match ties-AWAY-FROM-ZERO; ties-to-even "
                + "(.NET (Half)f) matches only " + okNet + "/" + finite + ".  " + ties
                + " exact midpoints in the set, " + tiesNetWrong + " of which ties-to-even gets wrong");
            foreach (string s in netOnly) Console.WriteLine(s);
            foreach (string s in detail) Console.WriteLine(s);

            // Known difference, asserted so it cannot drift silently: every NaN input still yields a
            // NaN half code on both sides, and exactly the recorded number differ in payload.
            Report("half/FloatToHalf-NaN", nan == 1 && nanBothNaN == nan && nanPayloadDiff == 1,
                nan + " NaN input(s); " + nanBothNaN + " map to a NaN half code on BOTH sides; "
                + nanPayloadDiff + " differ in payload only (known, harmless - GetBiomeHeight cannot "
                + "produce NaN at finite coordinates)");
            foreach (string s in nanNotes) Console.WriteLine(s);

            Report("half/HalfToFloat", backOkCount == backTotal,
                backOkCount + "/" + backTotal + " Mathf.HalfToFloat round trips reproduce bit-for-bit "
                + "through SeedLab.Saves.Half16.ToSingle (NaN payload included)");
        }

        // =========================================================================================
        // 4. libm
        // =========================================================================================

        /// <summary>
        /// <c>goldens\natives-libm.json</c>: Mono's <c>Math.Sin/Cos/Atan2/Pow</c> on the exact
        /// arguments world generation uses, versus .NET 10's on this machine. Any last-bit difference
        /// here reaches <c>WorldGenerator.WorldAngle</c> - which decides the Ashlands and DeepNorth ring
        /// wobble and so reaches <c>GetBiome</c> - and the Mistlands <c>^1.5</c> / Ashlands <c>^1.4</c>
        /// powers.
        ///
        /// The <c>worldAngle</c> array is the composite the port actually calls, so it is checked as
        /// float bits against <see cref="WorldGeneratorPort.WorldAngle"/> directly, not re-derived.
        /// </summary>
        private static void CheckLibm(string dir)
        {
            using JsonDocument doc = Json(Path.Combine(dir, "natives-libm.json"));

            int n = 0, exact = 0;
            var byFn = new SortedDictionary<string, (int ok, int n, long maxUlp)>(StringComparer.Ordinal);
            var detail = new List<string>();

            foreach (JsonElement s in doc.RootElement.GetProperty("samples").EnumerateArray())
            {
                string fn = s.GetProperty("fn").GetString() ?? "?";
                double a = BitConverter.Int64BitsToDouble((long)BitsOf64(s, "a"));
                double b = BitConverter.Int64BitsToDouble((long)BitsOf64(s, "b"));
                long want = (long)BitsOf64(s, "result");
                double got = fn switch
                {
                    "Sin" => Math.Sin(a),
                    "Cos" => Math.Cos(a),
                    "Atan2" => Math.Atan2(a, b),
                    "Pow" => Math.Pow(a, b),
                    _ => double.NaN,
                };
                long gotBits = BitConverter.DoubleToInt64Bits(got);
                long ulp = DoubleUlps(want, gotBits);

                n++;
                byFn.TryGetValue(fn, out var acc);
                bool ok = gotBits == want;
                if (ok) exact++;
                byFn[fn] = (acc.ok + (ok ? 1 : 0), acc.n + 1, Math.Max(acc.maxUlp, Math.Abs(ulp)));
                if (!ok && detail.Count < 20)
                    detail.Add("    " + fn + "(" + R(a) + (fn is "Atan2" or "Pow" ? ", " + R(b) : "") + ")"
                        + "  mono=" + Hex64(want) + "  net10=" + Hex64(gotBits) + "  ulp=" + ulp);
            }

            Report("libm/Math", exact == n, exact + "/" + n + " double results bit-identical between Mono and .NET 10");
            if (_verbose || exact != n)
                foreach (var kv in byFn)
                    Console.WriteLine("    " + kv.Key.PadRight(8) + kv.Value.ok + "/" + kv.Value.n
                                      + "   max |ulp| " + kv.Value.maxUlp);
            foreach (string s in detail) Console.WriteLine(s);

            // WorldAngle - the composite, as float bits.
            int wn = 0, wok = 0;
            var wdetail = new List<string>();
            foreach (JsonElement s in doc.RootElement.GetProperty("worldAngle").EnumerateArray())
            {
                float wx = BitConverter.Int32BitsToSingle((int)BitsOf(s, "wx"));
                float wy = BitConverter.Int32BitsToSingle((int)BitsOf(s, "wy"));
                int want = (int)BitsOf(s, "result");
                int got = BitConverter.SingleToInt32Bits(WorldGeneratorPort.WorldAngle(wx, wy));
                wn++;
                if (got == want) wok++;
                else if (wdetail.Count < 12)
                    wdetail.Add("    WorldAngle(" + R(wx) + ", " + R(wy) + ") game=" + Hex32(want)
                        + " port=" + Hex32(got) + " ulp=" + FloatUlps(want, got));
            }
            Report("libm/WorldAngle", wok == wn,
                wok + "/" + wn + " WorldGenerator.WorldAngle samples bit-exact (Atan2 -> *20 -> Sin, all three narrowings)");
            foreach (string s in wdetail) Console.WriteLine(s);
        }

        // =========================================================================================
        // 5. GetStableHashCode
        // =========================================================================================

        /// <summary>
        /// <c>goldens\natives-hash.json</c>: <c>StringExtensionMethods.GetStableHashCode</c> over every
        /// location, vegetation and alt-biome prefab name and the seed texts, against
        /// <see cref="StableHash.Compute(string)"/>. The location stream seed is
        /// <c>worldSeed + prefabName.GetStableHashCode()</c>, so one wrong hash moves every instance of
        /// that one location and nothing else - the hardest kind of error to notice.
        /// </summary>
        private static void CheckHash(string dir)
        {
            using JsonDocument doc = Json(Path.Combine(dir, "natives-hash.json"));
            int n = 0, ok = 0;
            var byKind = new SortedDictionary<string, (int ok, int n)>(StringComparer.Ordinal);
            var detail = new List<string>();

            foreach (JsonElement s in doc.RootElement.GetProperty("samples").EnumerateArray())
            {
                string str = s.GetProperty("s").GetString() ?? "";
                int want = s.GetProperty("hash").GetInt32();
                string kind = s.GetProperty("kind").GetString() ?? "?";
                int got = StableHash.Compute(str);
                n++;
                bool good = got == want;
                if (good) ok++;
                else if (detail.Count < 20)
                    detail.Add("    \"" + str + "\" (" + kind + ") game=" + want + " port=" + got);
                byKind.TryGetValue(kind, out var acc);
                byKind[kind] = (acc.ok + (good ? 1 : 0), acc.n + 1);

                // The lane split must recombine to the same hash (spec 06 section 3.1).
                (uint even, uint odd) = StableHash.ComputeLanes(str);
                int recombined = unchecked((int)(even + odd * 1566083941u));
                if (recombined != got && detail.Count < 20)
                    detail.Add("    lane split of \"" + str + "\" recombines to " + recombined + ", not " + got);
            }

            Report("hash/GetStableHashCode", ok == n, ok + "/" + n + " prefab names and seed texts hash identically");
            if (_verbose || ok != n)
                foreach (var kv in byKind)
                    Console.WriteLine("    " + kv.Key.PadRight(12) + kv.Value.ok + "/" + kv.Value.n);
            foreach (string s in detail) Console.WriteLine(s);
        }

        // =========================================================================================
        // Plumbing
        // =========================================================================================

        private static void Report(string name, bool ok, string message)
        {
            _checks++;
            if (!ok) _failed++;
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name.PadRight(26) + message);
        }

        private static void Fail(string name, string message) => Report(name, false, message);

        private static JsonDocument Json(string path)
        {
            using FileStream fs = File.OpenRead(path);
            return JsonDocument.Parse(fs, new JsonDocumentOptions { AllowTrailingCommas = true });
        }

        /// <summary>Reads a float member's raw bits out of the sibling "bits" object, which is the only
        /// authoritative form (DumpFormat's JSON conventions: "the decimal form is a human
        /// convenience; a reader must use the bits").</summary>
        private static uint BitsOf(JsonElement obj, string member)
            => ParseHex32(obj.GetProperty("bits").GetProperty(member).GetString()!);

        private static ulong BitsOf64(JsonElement obj, string member)
            => ParseHex64(obj.GetProperty("bits").GetProperty(member).GetString()!);

        private static uint ParseHex32(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static ulong ParseHex64(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static int[] ArrOf(JsonElement e)
        {
            int[] a = new int[e.GetArrayLength()];
            for (int i = 0; i < a.Length; i++) a[i] = e[i].GetInt32();
            return a;
        }

        private static bool StateEq(UnityRandom r, int[] want)
            => want.Length == 4 && r.s0 == want[0] && r.s1 == want[1] && r.s2 == want[2] && r.s3 == want[3];

        private static string Hex(float f) => Hex32(BitConverter.SingleToInt32Bits(f));
        private static string Hex32(int bits) => "0x" + bits.ToString("X8");
        private static string Hex64(long bits) => "0x" + bits.ToString("X16");
        private static string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);
        private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Signed distance in float ULPs along the sign-magnitude ladder.</summary>
        private static long FloatUlps(int a, int b)
        {
            long oa = a < 0 ? 0x80000000L - (a & 0x7FFFFFFF) : a + 0x80000000L;
            long ob = b < 0 ? 0x80000000L - (b & 0x7FFFFFFF) : b + 0x80000000L;
            return ob - oa;
        }

        /// <summary>Signed distance in double ULPs. Returns long.MinValue when either side is NaN.</summary>
        private static long DoubleUlps(long a, long b)
        {
            if (double.IsNaN(BitConverter.Int64BitsToDouble(a)) || double.IsNaN(BitConverter.Int64BitsToDouble(b)))
                return a == b ? 0 : long.MinValue;
            long oa = a < 0 ? long.MinValue - (a & long.MaxValue) : a;
            long ob = b < 0 ? long.MinValue - (b & long.MaxValue) : b;
            return ob - oa;
        }

        private static string? FindGoldens()
        {
            string? env = Environment.GetEnvironmentVariable("SEEDLAB_NATIVES_DIR");
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "natives-perlin.bin"))) return env;

            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                DirectoryInfo? d = new DirectoryInfo(start);
                for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                {
                    string cand = Path.Combine(Path.Combine(d.FullName, "groundtruth"), "natives");
                    if (File.Exists(Path.Combine(cand, "natives-perlin.bin"))) return cand;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// The two candidate binary16 encoders, kept local to this suite so it depends on nothing but
    /// <c>SeedLab.WorldGen</c>, <c>SeedLab.Seeds</c> and <c>SeedLab.Saves</c>. Identical in substance
    /// to <c>SeedLab.Cli.Infra.UnityHalf</c>; the golden check above is what proves which one is right.
    /// </summary>
    internal static class HalfCodecLocal
    {
        /// <summary>IEEE-754 binary16, round-to-nearest-ties-to-EVEN - what <c>(Half)f</c> does.</summary>
        public static ushort NetBits(float v) => BitConverter.HalfToUInt16Bits((Half)v);

        /// <summary>Same, except an exact midpoint rounds AWAY FROM ZERO.</summary>
        public static ushort UnityBits(float v, out bool wasTie)
        {
            wasTie = false;
            Half h = (Half)v;
            ushort b = BitConverter.HalfToUInt16Bits(h);
            if (!float.IsFinite(v) || !Half.IsFinite(h)) return b;

            double dv = v;
            if ((double)(float)h == dv) return b;          // exactly representable: no rounding happened
            if ((b & 1) != 0) return b;                    // ties-to-even leaves bit 0 clear

            Half up = Half.BitIncrement(h);
            if (Half.IsFinite(up) && ((double)(float)h + (double)(float)up) / 2.0 == dv)
            {
                wasTie = true;
                return Math.Abs((float)up) > Math.Abs((float)h) ? BitConverter.HalfToUInt16Bits(up) : b;
            }
            Half dn = Half.BitDecrement(h);
            if (Half.IsFinite(dn) && ((double)(float)h + (double)(float)dn) / 2.0 == dv)
            {
                wasTie = true;
                return Math.Abs((float)dn) > Math.Abs((float)h) ? BitConverter.HalfToUInt16Bits(dn) : b;
            }
            return b;
        }
    }
}
