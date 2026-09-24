using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace SeedLab.WorldGen.Unity
{
    /// <summary>
    /// The startup proof that the three spellings of <c>Mathf.PerlinNoise</c> in this assembly return
    /// the same bits: the reference transcription <see cref="UnityPerlin.Noise"/>, the byte-table scalar
    /// <see cref="PerlinFast.NoiseScalar"/> (O3), and the AVX2 8-wide <see cref="PerlinFast.Noise8"/>
    /// (O5, only where the hardware has it).
    ///
    /// <para><b>It runs automatically</b>, from a module initialiser, before any code in this assembly
    /// can be used - so every tool that touches the port (vseed, the web server, the tests, the location
    /// lab) is covered without any of them having to remember. <b>It fails closed:</b> a divergence
    /// throws, and the throw surfaces as a TypeInitializationException naming this class, rather than a
    /// silently different world. <see cref="Report"/> gives the same result as text for a
    /// <c>selftest</c> command to print.</para>
    ///
    /// <para>It runs once per process and costs a few milliseconds - see the figure printed by
    /// <see cref="Report"/>, which is measured on the machine it runs on.</para>
    ///
    /// <para><b>The vector set</b> is fixed and hostile on purpose. It covers: the exact argument shapes
    /// <c>GetBaseHeight</c>'s six octaves and two sea-channel samples produce at the corners, edges and
    /// centre of the world, at the seven-offset extremes; the four <c>GetBiome</c> mask arguments;
    /// exact lattice points and the 256-period wrap, where <c>fx</c> is exactly 0 and the fade is at its
    /// endpoint; the ULP neighbours of those points on both sides, which is where a fade-curve or
    /// truncation difference shows up first; negatives and both zeros, which exercise the abs fold;
    /// magnitudes around 2^20 and 2^24 where the float lattice is coarser than 1; and a dense
    /// pseudo-random sweep. Each lane position is exercised by every case, because the cases are rotated
    /// through the eight lanes - a bug in one lane of <c>Grad8</c>'s blends could not hide.</para>
    /// </summary>
    public static class PerlinSelfTest
    {
        private static readonly object s_gate = new object();
        private static string? s_report;

        // CA2255 warns that [ModuleInitializer] is for application code. That warning is about libraries
        // that surprise their host with work at load time; here the surprise IS the point. This check is
        // the only thing standing between a machine whose float behaviour differs and a silently wrong
        // world, so it must not be something a caller can forget to invoke. It costs a few milliseconds
        // once per process.
#pragma warning disable CA2255
        [ModuleInitializer]
        internal static void RunAtStartup() => Ensure();
#pragma warning restore CA2255

        /// <summary>Runs the check once per process. Throws on divergence; returns silently otherwise.</summary>
        public static void Ensure()
        {
            if (s_report != null) return;
            lock (s_gate)
            {
                if (s_report == null) s_report = Run();
            }
        }

        /// <summary>The check's result as one line of text. Runs it if it has not run yet.</summary>
        public static string Report()
        {
            Ensure();
            return s_report!;
        }

        private static unsafe string Run()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            float[] cases = BuildCases();
            int n = cases.Length;
            long scalarChecked = 0, vectorChecked = 0;

            // --- 1. the byte-table scalar against the reference ---------------------------------------
            // Eight pairings of the case list with itself, chosen so that every value appears as both
            // the x and the y argument and is combined with values from every part of the set.
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < 8; k++)
                {
                    int j = k switch
                    {
                        0 => i,
                        1 => (i + 1) % n,
                        2 => n - 1 - i,
                        3 => (i * 7 + 3) % n,
                        4 => (i * 13 + 5) % n,
                        5 => (i * 31 + 17) % n,
                        6 => (i + n / 2) % n,
                        _ => (i * 3 + 1) % n,
                    };
                    float x = cases[i], y = cases[j];
                    float a = (UnityPerlin.Noise(x, y) + UnityPerlin.NormAdd) / UnityPerlin.NormDiv;
                    float b = PerlinFast.PerlinNoise(x, y);
                    scalarChecked++;
                    if (Bits(a) != Bits(b)) throw Diverged("O3 byte-table scalar", x, y, a, b);
                }
            }

            // --- 2. the 8-wide path against the same reference ---------------------------------------
            if (PerlinFast.Use8Wide)
            {
                float* xs = stackalloc float[8];
                float* ys = stackalloc float[8];
                float* got = stackalloc float[8];
                // Rotate the case list through all eight lanes, so every lane sees every case.
                for (int rot = 0; rot < 8; rot++)
                {
                    for (int start = 0; start < n; start++)
                    {
                        for (int k = 0; k < 8; k++)
                        {
                            int ix = (start + k * 7 + rot) % n;
                            int iy = (start * 3 + k * 5 + rot * 11) % n;
                            xs[(k + rot) & 7] = cases[ix];
                            ys[(k + rot) & 7] = cases[iy];
                        }
                        PerlinFast.PerlinNoise8(xs, ys, got);
                        for (int k = 0; k < 8; k++)
                        {
                            float want = (UnityPerlin.Noise(xs[k], ys[k]) + UnityPerlin.NormAdd) / UnityPerlin.NormDiv;
                            vectorChecked++;
                            if (Bits(want) != Bits(got[k]))
                                throw Diverged("O5 AVX2 8-wide (lane " + k + ")", xs[k], ys[k], want, got[k]);
                        }
                    }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("Perlin self-test PASS: ")
              .Append(scalarChecked.ToString("N0", CultureInfo.InvariantCulture))
              .Append(" byte-table scalar samples");
            if (PerlinFast.Use8Wide)
            {
                sb.Append(" and ")
                  .Append(vectorChecked.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" AVX2 8-wide samples");
            }
            sb.Append(" bit-identical to UnityPerlin.Noise; 8-wide path ")
              .Append(PerlinFast.Use8Wide ? "ENABLED (AVX2)" : "disabled (no AVX2 on this machine - scalar fallback in use)")
              .Append("; ")
              .Append(((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                       / System.Diagnostics.Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture))
              .Append(" ms");
            return sb.ToString();
        }

        private static InvalidOperationException Diverged(string which, float x, float y, float want, float got)
            => new InvalidOperationException(
                "SeedLab: the " + which + " Perlin path does not agree with the reference transcription on "
                + "this machine, so world generation here would NOT be bit-exact. "
                + "PerlinNoise(" + R(x) + ", " + R(y) + ") = " + R(want) + " (0x" + Bits(want).ToString("X8") + ")"
                + " reference, " + R(got) + " (0x" + Bits(got).ToString("X8") + ") fast. "
                + "This is a hard failure on purpose: refusing to run is correct, producing a different "
                + "world silently is not. Report the machine's CPU and .NET version.");

        private static string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        private static uint Bits(float f) => unchecked((uint)BitConverter.SingleToInt32Bits(f));

        /// <summary>
        /// The fixed argument set. Values only - the test pairs them up itself, so n values give n^2
        /// scalar pairs.
        /// </summary>
        private static float[] BuildCases()
        {
            System.Collections.Generic.List<float> v = new System.Collections.Generic.List<float>();

            // Zeros and small exact values, including -0 (the abs fold's edge).
            v.Add(0f); v.Add(-0f); v.Add(1f); v.Add(-1f); v.Add(0.5f); v.Add(-0.5f);
            v.Add(float.Epsilon); v.Add(-float.Epsilon);

            // Exact lattice points and the 256 period, where fx == 0 and the fade sits on an endpoint.
            foreach (float b in new float[] { 1f, 2f, 15f, 16f, 127f, 128f, 255f, 256f, 257f, 511f, 512f, 1024f })
            {
                v.Add(b); v.Add(-b);
                v.Add(Next(b, +1)); v.Add(Next(b, -1));       // the ULP either side
                v.Add(b + 0.5f); v.Add(b - 0.5f);
            }

            // The magnitudes GetBaseHeight's eight samples actually produce. The world coordinate runs
            // over +-10,500; the offsets are drawn in +-10,000; the frequencies are the six below.
            foreach (float w in new float[] { -10500f, -10000f, -4000f, -1f, 0f, 1f, 744f, 1000f, 4000f, 10000f, 10500f })
            {
                foreach (double f in new double[] { 0.0020000000949949026 * 0.5, 0.003000000026077032 * 0.5,
                                                    0.0020000000949949026, 0.003000000026077032,
                                                    0.004999999888241291, 0.009999999776482582,
                                                    0.0020000000949949026 * 0.25 })
                {
                    foreach (double off in new double[] { -10000.0, 0.0, 10000.0 })
                    {
                        double c = (double)w + 100000.0 + off;
                        v.Add((float)(c * f));
                        v.Add((float)(c * f + 0.12300000339746475));
                        v.Add((float)(c * f + 0.32100000977516174));
                    }
                }
            }

            // GetBiome's four mask arguments: (offset + w) truncated to float, times 0.001f.
            foreach (float w in new float[] { -10500f, -2000f, 0f, 2000f, 10500f })
            {
                foreach (float off in new float[] { -10000f, -1f, 0f, 1f, 10000f })
                {
                    v.Add((float)((double)(float)((double)off + (double)w) * 0.0010000000474974513));
                }
            }

            // Coarse-lattice magnitudes: at 2^20 the float step is 0.125, at 2^24 it is 1 (so the
            // fraction is gone entirely) - the two places a truncation bug would show first. Both are
            // below PerlinFast.Domain8 / above it respectively, which also exercises the 8-wide guard
            // and its scalar fallback.
            foreach (float b in new float[] { 65536f, 262144f, 1048575f, 1048576f, 16777216f, 16777217f })
            {
                v.Add(b); v.Add(-b); v.Add(Next(b, -1));
            }

            // Non-finite arguments: they cannot arise from a world coordinate, but the fallback must be
            // the one that handles them, and the set proves the guard sends them there.
            v.Add(float.NaN); v.Add(float.PositiveInfinity); v.Add(float.NegativeInfinity);

            // A deterministic sweep over the interesting band, to catch anything the structured cases miss.
            uint s = 0x9E3779B9u;
            for (int i = 0; i < 96; i++)
            {
                s = unchecked(s * 1664525u + 1013904223u);
                double t = (s >> 8) / (double)(1 << 24);           // [0, 1)
                v.Add((float)((t - 0.5) * 2600.0));                 // the GetBaseHeight band, +-1300
            }
            return v.ToArray();
        }

        /// <summary>The next representable float in the given direction, for the ULP-neighbour cases.</summary>
        private static float Next(float f, int dir)
        {
            int b = BitConverter.SingleToInt32Bits(f);
            return BitConverter.Int32BitsToSingle(f >= 0f ? b + dir : b - dir);
        }
    }
}
