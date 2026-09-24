using System;

namespace SeedLab.WorldGen.Unity
{
    /// <summary>
    /// UnityEngine.Mathf.PerlinNoise, transcribed from UnityPlayer.dll 6000.0.75f1
    ///   PerlinNoise::Noise(float,float)           rva 0x0054B170
    ///   PerlinNoise::NoiseNormalized(float,float) rva 0x000AC7C0
    /// reached through Mono's internal-call registration table (index 2315), so the mapping from the
    /// managed name to this code is proven rather than inferred from proximity.
    ///
    /// Validated against real game output: 398,816 samples from the minimap cache of world
    /// 'asdasdasd' (seed -1772362158), 0 mismatches. Eight other plausible variants were rejected by
    /// the same data - see specs/03-unity-natives.md section 5.1.
    ///
    /// <para><b>Confirmed directly against the running game, 2026-09-23.</b> The dumper called
    /// <c>Mathf.PerlinNoise</c> inside Valheim 1.0.15 / Unity 6000.0.75f1 and wrote the float32 bit
    /// patterns to <c>goldens/natives-perlin.bin</c>. All <b>262,780 / 262,780</b> samples match this
    /// implementation bit for bit, over four blocks: D1-2d (24 - the abs fold on both axes, exact
    /// lattice points, the 256 period, 2^24 where the fraction is gone), D1-1d (24 - proving
    /// <c>PerlinNoise1D(x) == PerlinNoise(x, 0f)</c> rather than assuming it), D2-grid (262,144 - a
    /// dense 512x512 grid spanning negatives, which is what catches a fade-curve or fold error) and
    /// D3-real-arguments (588 - the exact arguments <c>GetBiome</c>'s four mask offsets and
    /// <c>GetBaseHeight</c>'s six octaves feed it, including the ~1e5-magnitude pairs where the float
    /// ULP is 0.0078). Replayed by <c>tests\SeedLab.Tests -- natives</c>; goldens in
    /// <c>groundtruth\natives\</c>. This was previously an indirect result only - the minimap cache
    /// shows the composed generator, not this function - so the transcription from the disassembly is
    /// now first-hand evidence, and variant P4 (the reciprocal multiply) stays rejected.</para>
    ///
    /// Stateless and thread-safe, like the original (the managed stub is [FreeFunction(IsThreadSafe = true)]).
    /// </summary>
    public static class UnityPerlin
    {
        public const float NormAdd = 0.69f;    // 0x3F30A3D7
        public const float NormDiv = 1.483f;   // 0x3FBDD2F2

        /// <summary>
        /// UnityEngine.Mathf.PerlinNoise(float, float).
        ///
        /// <para>O3. This forwards to <see cref="PerlinFast.NoiseScalar"/>, which is the same algorithm
        /// with the permutation table held as 512 bytes behind a pointer instead of 512 ints behind an
        /// array: identical arithmetic on identical values, no bounds checks, a quarter of the L1
        /// footprint. <see cref="Noise"/> below is untouched and stays the reference - it is what
        /// <see cref="PerlinSelfTest"/> compares against at startup, and what the 262,780 recorded game
        /// samples in <c>tests\SeedLab.Tests -- natives</c> are replayed through this entry point to
        /// re-prove on every run.</para>
        /// </summary>
        public static float PerlinNoise(float x, float y) => PerlinFast.PerlinNoise(x, y);

        /// <summary>
        /// Mathf.PerlinNoise built from the literal transcription <see cref="Noise"/>. The reference
        /// spelling: slower, and used by the self-test and by anything that wants the unoptimised path.
        /// </summary>
        public static float PerlinNoiseReference(float x, float y) => (Noise(x, y) + NormAdd) / NormDiv;

        /// <summary>UnityEngine.Mathf.PerlinNoise1D(float) - bit-identical to PerlinNoise(x, 0f).</summary>
        public static float PerlinNoise1D(float x) => PerlinNoise(x, 0f);

        /// <summary>PerlinNoise::Noise(float,float) - raw, measured range [-0.891581, 0.9995063].</summary>
        public static float Noise(float x, float y)
        {
            x = Abs(x);                        // andps 0x7FFFFFFF - NOT floor; see variant P7
            y = Abs(y);
            int ix = (int)x;                   // cvttss2si; operands are >= 0 so this is floor
            int iy = (int)y;
            int X = ix & 0xFF;
            int Y = iy & 0xFF;
            float fx = x - (float)ix;
            float fy = y - (float)iy;
            float u = Fade(MinSS(1f, fx));     // the clamp feeds the fade only
            float v = Fade(MinSS(1f, fy));

            int A = P[X] + Y;
            int B = P[X + 1] + Y;
            int AA = P[A], AB = P[A + 1], BA = P[B], BB = P[B + 1];

            float g00 = Grad(P[AA], fx, fy);
            float g10 = Grad(P[BA], fx - 1f, fy);
            float g01 = Grad(P[AB], fx, fy - 1f);
            float g11 = Grad(P[BB], fx - 1f, fy - 1f);

            float l1 = (g11 - g01) * u + g01;
            float l0 = (g10 - g00) * u + g00;
            return (l1 - l0) * v + l0;
        }

        // MINSS(dst, src) == (dst < src) ? dst : src - matters only for NaN inputs.
        private static float MinSS(float dst, float src) => (dst < src) ? dst : src;

        private static float Fade(float t)
        {
            float a = t * 6f;
            a = a - 15f;
            a = a * t;
            a = a + 10f;
            float t3 = (t * t) * t;
            return a * t3;
        }

        private static float Grad(int hash, float x, float y)
        {
            int h = hash & 15;
            float u, v;
            if (h < 8) { u = x; v = (h >= 4) ? 0f : y; }
            else { u = y; v = (h == 12 || h == 14) ? x : 0f; }
            if ((hash & 1) != 0) u = -u;       // xorps 0x80000000 on the FULL hash's bit 0
            if ((hash & 2) != 0) v = -v;
            return v + u;
        }

        private static float Abs(float f) =>
            BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(f) & 0x7FFFFFFF);

        // UnityPlayer.dll file offset 0x1BE0BC0 (rva 0x1BE1DC0) holds 512 int32 = this table twice.
        // Perm256 must be declared BEFORE P: C# runs static field initialisers in declaration order,
        // so the other way round Build() would read a null Perm256.
        private static readonly int[] Perm256 =
        {
            151, 160, 137,  91,  90,  15, 131,  13, 201,  95,  96,  53, 194, 233,   7, 225,
            140,  36, 103,  30,  69, 142,   8,  99,  37, 240,  21,  10,  23, 190,   6, 148,
            247, 120, 234,  75,   0,  26, 197,  62,  94, 252, 219, 203, 117,  35,  11,  32,
             57, 177,  33,  88, 237, 149,  56,  87, 174,  20, 125, 136, 171, 168,  68, 175,
             74, 165,  71, 134, 139,  48,  27, 166,  77, 146, 158, 231,  83, 111, 229, 122,
             60, 211, 133, 230, 220, 105,  92,  41,  55,  46, 245,  40, 244, 102, 143,  54,
             65,  25,  63, 161,   1, 216,  80,  73, 209,  76, 132, 187, 208,  89,  18, 169,
            200, 196, 135, 130, 116, 188, 159,  86, 164, 100, 109, 198, 173, 186,   3,  64,
             52, 217, 226, 250, 124, 123,   5, 202,  38, 147, 118, 126, 255,  82,  85, 212,
            207, 206,  59, 227,  47,  16,  58,  17, 182, 189,  28,  42, 223, 183, 170, 213,
            119, 248, 152,   2,  44, 154, 163,  70, 221, 153, 101, 155, 167,  43, 172,   9,
            129,  22,  39, 253,  19,  98, 108, 110,  79, 113, 224, 232, 178, 185, 112, 104,
            218, 246,  97, 228, 251,  34, 242, 193, 238, 210, 144,  12, 191, 179, 162, 241,
             81,  51, 145, 235, 249,  14, 239, 107,  49, 192, 214,  31, 181, 199, 106, 157,
            184,  84, 204, 176, 115, 121,  50,  45, 127,   4, 150, 254, 138, 236, 205,  93,
            222, 114,  67,  29,  24,  72, 243, 141, 128, 195,  78,  66, 215,  61, 156, 180,
        };

        private static readonly int[] P = Build();

        /// <summary>
        /// Entry i of the doubled permutation table. The single source of the table for the whole
        /// assembly: <see cref="PerlinFast"/> builds its byte and int copies from this, so the fast
        /// paths cannot drift from the reference by a mistyped digit.
        /// </summary>
        internal static int Perm(int i) => P[i];

        private static int[] Build()
        {
            int[] q = new int[512];
            for (int i = 0; i < 256; i++) { q[i] = Perm256[i]; q[i + 256] = Perm256[i]; }
            return q;
        }
    }
}
