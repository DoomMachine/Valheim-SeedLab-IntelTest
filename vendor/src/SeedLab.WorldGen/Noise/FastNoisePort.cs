using System;
using System.Runtime.CompilerServices;

namespace SeedLab.WorldGen.Noise
{
    /// <summary>
    /// The reachable slice of the game's FastNoise (assembly_utils), transcribed from
    /// decomp/FastNoise.cs. Only three entry points are reached from WorldGenerator, all of them from
    /// GetAshlandsHeight (01-worldgen-core.md 7.2, confirmed by grepping the WorldGenerator IL for
    /// 'FastNoise::' - IL_0162, IL_0306, IL_03a0):
    ///
    ///   GetCellular(double,double)       cellular, Euclidean distance function, Distance return type
    ///   GetSimplexFractal(double,double) simplex FBM, 2 octaves
    ///
    /// The game's FastNoise is a DOUBLE-precision C# port - Float2 holds doubles - so upstream
    /// FastNoise/FastNoiseLite (float, different tables) must NOT be substituted.
    ///
    /// Settings, from WorldGenerator..ctor (IL_00d2-IL_010e):
    ///   new FastNoise(worldSeed); SetNoiseType(Cellular); SetCellularDistanceFunction(Euclidean);
    ///   SetCellularReturnType(Distance); SetFractalOctaves(2);   then SetSeed(0) on EVERY ctor.
    /// Because SetSeed(0) always runs before any use, this noise is SEED-INDEPENDENT: the AshLands
    /// cellular and simplex patterns are identical in every world. m_noiseGen is a private static in the
    /// game; here it is a shared immutable singleton, which is safe precisely because it carries no seed.
    ///
    /// Stateless after construction and therefore thread-safe.
    /// </summary>
    public sealed class FastNoisePort
    {
        // --- settings, fixed to what WorldGenerator configures -------------------------------------
        private readonly int m_seed;                 // SetSeed(0), always
        private readonly double m_frequency = 0.01;  // field default, never set by the game
        private readonly int m_octaves = 2;          // SetFractalOctaves(2)
        private readonly double m_lacunarity = 2.0;  // field default
        private readonly double m_gain = 0.5;        // field default
        private readonly double m_fractalBounding;   // CalculateFractalBounding() => 1/1.5

        /// <summary>
        /// FastNoise.m_cellularJitter is declared 'private float m_cellularJitter = 0.45f'
        /// (decomp/FastNoise.cs 114) and is widened at every use inside SingleCellular. The effective
        /// constant is therefore (double)0.45f = 0.449999988079071044921875, whose shortest round-trip
        /// form is 0.44999998807907104. Writing the cast rather than the digits is deliberate: the
        /// literal 0.449999988079071 is a DIFFERENT double, one ulp low, and shifts every cell centre
        /// (01-worldgen-core.md 1.4, corrected by the reviewer).
        /// </summary>
        private const double CellularJitter = (double)0.45f;

        /// <summary>The instance WorldGenerator uses: seed 0, octaves 2, everything else defaulted.</summary>
        public static readonly FastNoisePort WorldGen = new FastNoisePort(0);

        public FastNoisePort(int seed)
        {
            m_seed = seed;
            // FastNoise.CalculateFractalBounding (decomp/FastNoise.cs 864-874), run as written.
            double num = m_gain;
            double num2 = 1.0;
            for (int i = 1; i < m_octaves; i++)
            {
                num2 += num;
                num *= m_gain;
            }
            m_fractalBounding = 1.0 / num2;
        }

        // --- helpers (decomp/FastNoise.cs 820-948) --------------------------------------------------

        /// <summary>FastNoise.FastFloor.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastFloor(double f) => (f >= 0.0) ? (int)f : (int)f - 1;

        /// <summary>FastNoise.FastRound.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastRound(double f) => (f >= 0.0) ? (int)(f + 0.5) : (int)(f - 0.5);

        /// <summary>
        /// FastNoise.Hash2D. All of it is 32-bit signed and MUST be unchecked: n*n*n*60493 overflows on
        /// almost every input, and 'n >> 13' is an arithmetic shift.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Hash2D(int seed, int x, int y)
        {
            unchecked
            {
                int n = seed;
                n ^= 1619 * x;
                n ^= 31337 * y;
                n = n * n * n * 60493;
                return (n >> 13) ^ n;
            }
        }

        /// <summary>FastNoise.GradCoord2D - the same hash inlined, indexing GRAD_2D with n &amp; 7.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GradCoord2D(int seed, int x, int y, double xd, double yd)
        {
            unchecked
            {
                int n = seed;
                n ^= 1619 * x;
                n ^= 31337 * y;
                n = n * n * n * 60493;
                n = (n >> 13) ^ n;
                int g = (n & 7) << 1;
                double[] grad = FastNoiseTables.GRAD_2D;
                return xd * grad[g] + yd * grad[g + 1];
            }
        }

        // --- cellular ------------------------------------------------------------------------------

        /// <summary>
        /// FastNoise.GetCellular(double,double) (decomp 2385-2395). The return type is Distance (2), so
        /// the (uint)returnType &lt;= 2 test takes SingleCellular. Note the frequency multiply happens
        /// here, on top of the caller's own frequency factor.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double GetCellular(double x, double y)
        {
            x *= m_frequency;
            y *= m_frequency;
            return SingleCellular(x, y);
        }

        /// <summary>
        /// FastNoise.SingleCellular(double,double), Euclidean branch (the 'default' case), return type
        /// Distance (decomp 2397-2481). Returns the SQUARED euclidean distance to the nearest jittered
        /// cell centre - there is no sqrt.
        ///
        /// The scan is i (x) OUTER, j (y) INNER, and ties are resolved by '&lt;' in favour of the first
        /// cell visited; reordering the loops would change the answer on exact ties.
        /// </summary>
        private double SingleCellular(double x, double y)
        {
            int xr = FastRound(x);
            int yr = FastRound(y);
            double best = 999999.0;
            double[] cell = FastNoiseTables.CELL_2D;
            int seed = m_seed;

            for (int i = xr - 1; i <= xr + 1; i++)
            {
                for (int j = yr - 1; j <= yr + 1; j++)
                {
                    int c = (Hash2D(seed, i, j) & 0xFF) << 1;
                    double dx = (double)i - x + cell[c] * CellularJitter;
                    double dy = (double)j - y + cell[c + 1] * CellularJitter;
                    double d = dx * dx + dy * dy;
                    if (d < best) best = d;
                    // The game also records the winning cell coordinates; with CellularReturnType.Distance
                    // they are never read, so they are omitted here.
                }
            }
            return best;
        }

        // --- simplex fractal -----------------------------------------------------------------------

        /// <summary>
        /// FastNoise.GetSimplexFractal(double,double) with m_fractalType == FBM (the field default)
        /// (decomp 1768-1794).
        /// </summary>
        public double GetSimplexFractal(double x, double y)
        {
            x *= m_frequency;
            y *= m_frequency;
            // SingleSimplexFractalFBM: note '++num' - octave 2 uses seed+1, not a re-hashed seed.
            int seed = m_seed;
            double sum = SingleSimplex(seed, x, y);
            double amp = 1.0;
            for (int i = 1; i < m_octaves; i++)
            {
                x *= m_lacunarity;
                y *= m_lacunarity;
                amp *= m_gain;
                sum += SingleSimplex(++seed, x, y) * amp;
            }
            return sum * m_fractalBounding;
        }

        /// <summary>
        /// FastNoise.SingleSimplex(int,double,double) - classic 2D simplex (decomp 1831-1891), copied
        /// with its exact constants F2 = 0.3660254037844386, G2 = 0.21132486540518713 and
        /// 2*G2 = 0.42264973081037427 (these are genuine doubles, not widened floats).
        /// </summary>
        private static double SingleSimplex(int seed, double x, double y)
        {
            double t = (x + y) * 0.3660254037844386;
            int i = FastFloor(x + t);
            int j = FastFloor(y + t);
            t = (double)(i + j) * 0.21132486540518713;
            double X0 = (double)i - t;
            double Y0 = (double)j - t;
            double x0 = x - X0;
            double y0 = y - Y0;

            int i1, j1;
            if (x0 > y0) { i1 = 1; j1 = 0; }
            else { i1 = 0; j1 = 1; }

            double x1 = x0 - (double)i1 + 0.21132486540518713;
            double y1 = y0 - (double)j1 + 0.21132486540518713;
            double x2 = x0 - 1.0 + 0.42264973081037427;
            double y2 = y0 - 1.0 + 0.42264973081037427;

            double n0, n1, n2;

            t = 0.5 - x0 * x0 - y0 * y0;
            if (t < 0.0) n0 = 0.0;
            else { t *= t; n0 = t * t * GradCoord2D(seed, i, j, x0, y0); }

            t = 0.5 - x1 * x1 - y1 * y1;
            if (t < 0.0) n1 = 0.0;
            else { t *= t; n1 = t * t * GradCoord2D(seed, i + i1, j + j1, x1, y1); }

            t = 0.5 - x2 * x2 - y2 * y2;
            if (t < 0.0) n2 = 0.0;
            else { t *= t; n2 = t * t * GradCoord2D(seed, i + 1, j + 1, x2, y2); }

            return 50.0 * (n0 + n1 + n2);
        }
    }
}
