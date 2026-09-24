using System;
using System.Runtime.CompilerServices;

namespace SeedLab.WorldGen.Unity
{
    /// <summary>
    /// UnityEngine.Vector2, transcribed from the IL of the shipped UnityEngine.CoreModule
    /// (scratchpad/probe/Vector2.il.txt), not from ILSpy's C#.
    ///
    /// <para><b>The x*x + y*y sums are accumulated in DOUBLE, not float.</b> In every one of
    /// get_magnitude, get_sqrMagnitude, Distance, SqrMagnitude and op_Equality the two `mul`s and the
    /// `add` execute on the evaluation stack with no intervening conv.r4, and Unity's Mono keeps every
    /// FP stack slot at R8 - it narrows only at a conv.r4 or a store into a float32 location. So the
    /// products are exact (a float32 square is exact in double) and the sum is rounded once, at the
    /// final conv.r4 / stloc, instead of three times. Writing these as float chains rounds the two
    /// products and the sum, which is what the C# source LOOKS like and is wrong on ~1 sample in 40.
    ///
    /// A consequence worth knowing: under these semantics Vector2.magnitude and DUtils.Length(float,
    /// float) are numerically IDENTICAL (DUtils.Length just spells the conv.r8 out explicitly,
    /// DUtils.il.txt IL_0001-IL_000a). 01-worldgen-core.md 6.6/6.7 claims they differ; they do not.
    /// The sub in Distance/op_Equality IS narrowed, because it stores to a float32 local
    /// (Distance IL_000e 'stloc.0', IL_001c 'stloc.1').</para>
    /// </summary>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public readonly float x;
        public readonly float y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vec2(float x, float y) { this.x = x; this.y = y; }

        public static readonly Vec2 Zero = new Vec2(0f, 0f);

        /// <summary>
        /// Vector2.get_magnitude (IL_000d 'mul', IL_001a 'mul', IL_001b 'add', IL_001c 'conv.r8',
        /// Math.Sqrt, IL_0022 'conv.r4'). The conv.r8 sits AFTER the sum, so nothing narrows the
        /// products or the sum - they are computed at R8 and rounded once by the trailing conv.r4.
        /// </summary>
        public float Magnitude
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (float)Math.Sqrt((double)x * (double)x + (double)y * (double)y); }
        }

        /// <summary>
        /// Vector2.get_sqrMagnitude (IL_000d/IL_001a 'mul', IL_001b 'add', IL_001c 'stloc.0').
        /// No conv at all: the single narrowing is the store into the float32 local.
        /// </summary>
        public float SqrMagnitudeSelf
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return (float)((double)x * (double)x + (double)y * (double)y); }
        }

        /// <summary>
        /// Vector2.Distance(a, b). The two subtractions ARE narrowed (IL_000e 'stloc.0',
        /// IL_001c 'stloc.1' - float32 locals), then IL_001f/IL_0022 'mul', IL_0023 'add',
        /// IL_0024 'conv.r8', Math.Sqrt, IL_002a 'conv.r4' accumulate at R8.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Distance(Vec2 a, Vec2 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            return (float)Math.Sqrt((double)dx * (double)dx + (double)dy * (double)dy);
        }

        /// <summary>
        /// Vector2.SqrMagnitude(a) (IL_000d/IL_001a 'mul', IL_001b 'add', IL_001c 'stloc.0') - one
        /// narrowing, at the store. Feeds GetWeight's per-point distance test.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SqrMagnitude(Vec2 a)
            => (float)((double)a.x * (double)a.x + (double)a.y * (double)a.y);

        /// <summary>
        /// Vector2.Normalize: divides by the magnitude only when it exceeds 1e-05f, otherwise returns
        /// zero (decomp/UnityEngine.Vector2.cs 224-235). Note the divide is a per-component FLOAT divide,
        /// not a multiply by the reciprocal.
        /// </summary>
        public Vec2 Normalized
        {
            get
            {
                float m = Magnitude;
                if (m > 1E-05f) return new Vec2(x / m, y / m);
                return Zero;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.x + b.x, a.y + b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.x - b.x, a.y - b.y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec2 operator *(Vec2 a, float d) => new Vec2(a.x * d, a.y * d);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec2 operator *(float d, Vec2 a) => new Vec2(a.x * d, a.y * d);

        /// <summary>
        /// Vector2.op_Equality is an EPSILON test, not exact equality:
        /// (dx*dx + dy*dy) &lt; 9.9999994E-11f (decomp/UnityEngine.Vector2.cs 476-481).
        /// FindClosest, FindRandomRiverEnd and both HaveRiver overloads depend on it, so it decides which
        /// lakes may be linked by a river. Using exact float equality here builds a different river graph
        /// (01-worldgen-core.md 6.8).
        ///
        /// Same R8 accumulation as Distance: IL_000e/IL_001c 'stloc' narrow the two subtractions, then
        /// IL_001f/IL_0022 'mul', IL_0023 'add' and IL_0029 'clt' run at R8 against the widened float32
        /// constant (IL_0024 'ldc.r4 9.9999994396249292E-11').
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Vec2 lhs, Vec2 rhs)
        {
            float dx = lhs.x - rhs.x;
            float dy = lhs.y - rhs.y;
            return (double)dx * (double)dx + (double)dy * (double)dy < (double)9.9999994E-11f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Vec2 lhs, Vec2 rhs) => !(lhs == rhs);

        /// <summary>Uses the same epsilon test as operator == so the two never disagree.</summary>
        public bool Equals(Vec2 other) => this == other;

        public override bool Equals(object? obj) => obj is Vec2 v && this == v;

        // Deliberately NOT consistent with the epsilon Equals: Vec2 is never used as a dictionary key in
        // the port (Vector2i is), and Unity's own GetHashCode is equally inconsistent.
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2);

        public override string ToString() => "(" + x + ", " + y + ")";
    }

    /// <summary>
    /// Vector2i (assembly_utils) - two ints, used as the river-grid dictionary key.
    /// </summary>
    public readonly struct Vec2i : IEquatable<Vec2i>
    {
        public readonly int x;
        public readonly int y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vec2i(int x, int y) { this.x = x; this.y = y; }

        public bool Equals(Vec2i other) => x == other.x && y == other.y;
        public override bool Equals(object? obj) => obj is Vec2i v && Equals(v);
        public override int GetHashCode() => unchecked(x * 73856093 ^ y * 19349663);
        public static bool operator ==(Vec2i a, Vec2i b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vec2i a, Vec2i b) => !(a == b);
        public override string ToString() => "(" + x + ", " + y + ")";
    }

    /// <summary>
    /// UnityEngine.Color as the generator uses it: four floats, default Color.black = (0,0,0,1).
    /// Only GetMistlandsHeight (a), GetDeepNorthHeight (g) and GetAshlandsHeight (a) ever overwrite it
    /// (01-worldgen-core.md 3.6).
    /// </summary>
    public readonly struct ColorRGBA
    {
        public readonly float r, g, b, a;
        public ColorRGBA(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        /// <summary>Color.black = (0, 0, 0, 1).</summary>
        public static readonly ColorRGBA Black = new ColorRGBA(0f, 0f, 0f, 1f);
        public override string ToString() => "RGBA(" + r + ", " + g + ", " + b + ", " + a + ")";
    }

    /// <summary>
    /// The UnityEngine.Mathf members the generator reaches. Each one is a thin wrapper over System.Math
    /// in the real assembly, and the double round-trip in Sin/Cos/Ceil/FloorToInt/CeilToInt is real
    /// (01-worldgen-core.md 6.12, Mathf.il.txt).
    ///
    /// <para><b>The Mono-versus-.NET libm question (spec 03 item D12) is settled: there is no
    /// difference.</b> Golden <c>goldens/natives-libm.json</c> holds Mono's own
    /// <c>Math.Sin/Cos/Atan2/Pow</c> results, computed inside Valheim 1.0.15 on the exact arguments
    /// world generation uses. Replayed against .NET 10 on 2026-09-23: <b>93/93 double results
    /// bit-identical, max |ULP| 0</b> (Atan2 49/49 over the 7x7 world grid, Sin 10/10, Cos 10/10,
    /// Pow 24/24 covering the Mistlands ^1.5 and Ashlands ^1.4 exponents), and the composite
    /// <c>WorldGenerator.WorldAngle</c> <b>49/49 bit-exact</b> as float.</para>
    ///
    /// <para>Bound on what a last-bit libm difference could ever have cost, measured rather than
    /// guessed: <c>WorldAngle</c> reaches <c>GetBiome</c> only through the Ashlands and DeepNorth ring
    /// predicates, and perturbing <c>WorldAngle</c> by +/-1 ULP at every one of the
    /// <b>4,194,304</b> minimap pixel centres flips <c>IsAshlands</c> or <c>IsDeepnorth</c> at
    /// <b>0</b> of them - the wobble is multiplied by 100 but the ring comparison is never that
    /// close. So the residual risk here is zero on the minimap, not merely small.</para>
    /// </summary>
    public static class UMathf
    {
        /// <summary>Mathf.Abs(float) == Math.Abs(float) (Mathf.Abs / IL_0002).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Abs(float f) => Math.Abs(f);

        /// <summary>Mathf.Sin(float) == (float)Math.Sin((double)f).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sin(float f) => (float)Math.Sin((double)f);

        /// <summary>Mathf.Cos(float) == (float)Math.Cos((double)f).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Cos(float f) => (float)Math.Cos((double)f);

        /// <summary>Mathf.Ceil(float) == (float)Math.Ceiling((double)f).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Ceil(float f) => (float)Math.Ceiling((double)f);

        /// <summary>Mathf.FloorToInt(float) == (int)Math.Floor((double)f).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FloorToInt(float f) => (int)Math.Floor((double)f);

        /// <summary>Mathf.CeilToInt(float) == (int)Math.Ceiling((double)f).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CeilToInt(float f) => (int)Math.Ceiling((double)f);

        /// <summary>Mathf.RoundToInt(float) == (int)Math.Round((double)f) - banker's rounding, as in Unity.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int RoundToInt(float f) => (int)Math.Round((double)f);

        /// <summary>Mathf.Clamp(float,float,float) - the plain three-way compare, no NaN handling.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Max(float a, float b) => (a > b) ? a : b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Min(int a, int b) => (a < b) ? a : b;

        /// <summary>Mathf.Clamp01(float) - float ternary. Not to be confused with DUtils.Clamp01(double).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        /// <summary>Mathf.Lerp(a, b, t) == a + (b - a) * Clamp01(t), all float. NOT DUtils.Lerp.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        /// <summary>Mathf.SmoothStep - Unity's own, kept distinct from DUtils.SmoothStep and MathfLikeSmoothStep.</summary>
        public static float SmoothStep(float from, float to, float t)
        {
            t = Clamp01(t);
            t = -2f * t * t * t + 3f * t * t;
            return to * t + from * (1f - t);
        }
    }

    /// <summary>
    /// The two Utils (assembly_utils) members that differ from their DUtils namesakes.
    /// Utils.LerpStep is ALL-FLOAT and is reached exactly once in the whole generator: the 10490 m world
    /// edge inside GetBaseHeight (GetBaseHeight / IL_04b3 'call System.Single Utils::LerpStep').
    /// Every other LerpStep in WorldGenerator is DUtils.LerpStep, which computes in double
    /// (01-worldgen-core.md 6.4).
    /// </summary>
    public static class UtilsMath
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp01(float v)
        {
            if (v > 1f) return 1f;
            if (v < 0f) return 0f;
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float LerpStep(float l, float h, float v) => Clamp01((v - l) / (h - l));
    }
}
