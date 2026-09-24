using System;
using System.Runtime.CompilerServices;
using SeedLab.WorldGen.Unity;

namespace SeedLab.WorldGen
{
    /// <summary>
    /// DUtils (assembly_utils), transcribed statement for statement from decomp/DUtils.cs and checked
    /// against DUtils.il.txt (01-worldgen-core.md 7.1).
    ///
    /// The house rule of this file - and of the whole port - is: arithmetic in double, truncate to float
    /// exactly where the IL has a conv.r4, float literals widened to double. Do not "simplify" any of
    /// these bodies; several of them differ from the obvious implementation by one rounding, and that
    /// rounding is what the game shipped.
    /// </summary>
    public static class DUtils
    {
        /// <summary>
        /// DUtils.Length(float,float): squares and sums in DOUBLE, then (float)Math.Sqrt.
        /// Distinct from Vec2.Magnitude, which sums in float. GetBiome, GetBaseHeight, GetBiomeHeight,
        /// IsAshlands and the gap functions use this one; IsDeepnorth and FindLakes use the float one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Length(float x, float y) => (float)Math.Sqrt((double)x * (double)x + (double)y * (double)y);

        /// <summary>DUtils.Length(double,double) - used only by GetAshlandsHeight, which is double end to end.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Length(double x, double y) => Math.Sqrt(x * x + y * y);

        /// <summary>
        /// DUtils.BlendOverlay: BOTH branches are computed and then selected (decomp/DUtils.cs 16-24).
        /// Computing only the taken branch is observationally identical - there are no side effects - so
        /// this port evaluates lazily for speed; the selection test is the original's !(a &lt; 0.5).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double BlendOverlay(double a, double b)
        {
            if (!(a < 0.5)) return 1.0 - 2.0 * (1.0 - a) * (1.0 - b);
            return 2.0 * a * b;
        }

        /// <summary>
        /// DUtils.Lerp(float,float,float): SHORT-CIRCUITS at t &lt;= 0 and t &gt;= 1, returning a or b
        /// exactly; otherwise a*(1-t) + b*t evaluated in double and truncated once.
        /// Substituting Mathf.Lerp or a + (b-a)*t changes results (01-worldgen-core.md 6.5).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return (float)((double)a * (1.0 - (double)t) + (double)b * (double)t);
        }

        /// <summary>DUtils.Lerp(double,double,double) - same short circuits, no float truncation.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Lerp(double a, double b, double t)
        {
            if (t <= 0.0) return a;
            if (t >= 1.0) return b;
            return a * (1.0 - t) + b * t;
        }

        /// <summary>DUtils.LerpStep(float,float,float): double interior, ONE conv.r4 on the way out.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float LerpStep(float l, float h, float v)
            => (float)Clamp01(((double)v - (double)l) / ((double)h - (double)l));

        /// <summary>DUtils.LerpStep(double,double,double) - reached exactly once, in GetAshlandsHeight.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double LerpStep(double l, double h, double v) => Clamp01((v - l) / (h - l));

        /// <summary>
        /// DUtils.SmoothStep: note the conv.r4 in the MIDDLE - t is rounded to float before being cubed
        /// (decomp/DUtils.cs 60-64). Reached from GetBaseHeight's sea-channel term.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SmoothStep(float pMin, float pMax, float pX)
        {
            float t = (float)Clamp01(((double)pX - (double)pMin) / ((double)pMax - (double)pMin));
            return (float)((double)t * (double)t * (3.0 - 2.0 * (double)t));
        }

        /// <summary>
        /// DUtils.MathfLikeSmoothStep returns a FLOAT-ROUNDED double: its IL ends
        /// 'add; conv.r4; conv.r8; ret' (DUtils.MathfLikeSmoothStep / IL_0037-IL_003a,
        /// 01-worldgen-core.md 6.2). Dropping that rounding shifts every AshLands ridge and both gap
        /// functions. The polynomial's operation order is copied as written.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double MathfLikeSmoothStep(double from, double to, double t)
        {
            t = Clamp01(t);
            t = -2.0 * t * t * t + 3.0 * t * t;
            return (float)(to * t + from * (1.0 - t));
        }

        /// <summary>
        /// DUtils.Clamp01(double). The IL uses the unordered branch forms, so NaN passes straight through
        /// (both comparisons fail). Reproduced by testing in this order.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Clamp01(double v)
        {
            if (v > 1.0) return 1.0;
            if (v < 0.0) return 0.0;
            return v;
        }

        /// <summary>
        /// DUtils.Fbm(Vector3, int, float, float) - forwards to the Vector2 FLOAT overload using (p.x, p.z).
        /// Reached only from WorldGenerator.GetForestFactor.
        /// </summary>
        public static float Fbm(float px, float pz, int octaves, float lacunarity, float gain)
            => Fbm(new Vec2(px, pz), octaves, lacunarity, gain);

        /// <summary>
        /// DUtils.Fbm(Vector2, int, float, float) - FLOAT accumulator, and the coordinate scaling is a
        /// Vector2 multiply (per-component float), not a double multiply.
        /// </summary>
        public static float Fbm(Vec2 p, int octaves, float lacunarity, float gain)
        {
            float sum = 0f;
            float amp = 1f;
            Vec2 v = p;
            for (int i = 0; i < octaves; i++)
            {
                sum = (float)((double)sum + (double)amp * (double)PerlinNoise(v.x, v.y));
                amp = (float)((double)amp * (double)gain);
                v = v * lacunarity;
            }
            return sum;
        }

        /// <summary>
        /// DUtils.Fbm(Vector2, int, double, double) - DOUBLE accumulator and double coordinate scaling,
        /// but the Perlin result is still a float that gets widened. The Vector2 argument means the
        /// caller's coordinates were already truncated to float before this is entered.
        /// Used by GetDeepNorthHeight's mask and GetAshlandsHeight's lava, both octaves 3, lac 2, gain 0.5.
        /// </summary>
        public static double Fbm(Vec2 p, int octaves, double lacunarity, double gain)
        {
            double sum = 0.0;
            double amp = 1.0;
            double x = p.x;
            double y = p.y;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * (double)PerlinNoise(x, y);
                amp *= gain;
                x *= lacunarity;
                y *= lacunarity;
            }
            return sum;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Remap(double value, double inLow, double inHigh, double outLow, double outHigh)
            => Lerp(outLow, outHigh, InverseLerp(inLow, inHigh, value));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double InverseLerp(double a, double b, double value)
        {
            if (a == b) return 0.0;
            return Clamp01((value - a) / (b - a));
        }

        /// <summary>
        /// DUtils.PerlinNoise(double,double) = Mathf.PerlinNoise((float)x, (float)y). The two conv.r4
        /// casts are why every "coordinate * frequency" product in WorldGenerator is quantised back to
        /// float before the noise sees it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float PerlinNoise(double x, double y) => UnityPerlin.PerlinNoise((float)x, (float)y);

        /// <summary>
        /// DUtils.PerlinNoise(float,float). Reached only from GetDeepNorthHeightPregenerate's last two
        /// terms, which multiply their coordinates in float (01-worldgen-core.md 3.3).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float PerlinNoise(float x, float y) => UnityPerlin.PerlinNoise(x, y);
    }
}
