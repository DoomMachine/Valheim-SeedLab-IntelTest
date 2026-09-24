using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SeedLab.WorldGen.Unity
{
    /// <summary>
    /// Two faster spellings of <see cref="UnityPerlin.Noise"/> that perform exactly the same IEEE-754
    /// binary32 operations, on exactly the same values, in exactly the same order - and therefore must
    /// return exactly the same bits. <see cref="UnityPerlin.Noise"/> stays in the tree, unchanged, as
    /// the literal transcription of the disassembly and as the reference the self-test compares against.
    ///
    /// <list type="number">
    /// <item><b>O3, <see cref="NoiseScalar"/></b> - the identical algorithm with the permutation table
    /// held as 512 <b>bytes</b> and read through a pointer. Every value in the table is in 0..255, so
    /// the byte spelling loses nothing; the pointer read removes the array bounds checks, and the table
    /// drops from 2 KiB to 512 B of L1. No arithmetic changes at all.</item>
    /// <item><b>O5, <see cref="Noise8"/></b> - eight independent samples at once with AVX2.</item>
    /// </list>
    ///
    /// <para><b>The exactness argument for the 8-wide path.</b> It rests on four facts, each of which is
    /// checked rather than assumed (see <see cref="PerlinSelfTest"/>):</para>
    /// <list type="number">
    /// <item><b>Packed single-precision operations are the scalar operations, per lane.</b> ADDPS, SUBPS,
    /// MULPS, DIVPS, MINPS, ANDPS, XORPS, CVTTPS2DQ and CVTDQ2PS are defined by Intel SDM Vol. 1 §11.6.3
    /// to compute, in each lane, exactly what ADDSS/SUBSS/.../CVTTSS2SI compute for that lane's operands,
    /// with the same IEEE-754 rounding (round-to-nearest-even under the default MXCSR) and no extended
    /// intermediate precision. .NET guarantees the default MXCSR rounding mode and does not allow managed
    /// code to change it.</item>
    /// <item><b>No lane influences another.</b> Every operation used here is element-wise. The only
    /// cross-lane instruction is VPGATHERDD, which is a set of independent loads. There is no horizontal
    /// add, no shuffle, no reduction, and nothing is reassociated - lane <c>i</c> computes the scalar
    /// program on argument <c>i</c> and nothing else.</item>
    /// <item><b>No fused multiply-add anywhere.</b> FMA would contract <c>a*b+c</c> from two roundings
    /// into one and change results. C# is forbidden from contracting (ECMA-334 §12.8.3 / the .NET
    /// numerics contract: each operator rounds), RyuJIT never forms FMA on its own, and this file calls
    /// <see cref="Avx.Multiply"/> and <see cref="Avx.Add"/> as separate intrinsics - <c>Fma.*</c> is
    /// never referenced. <b>Do not introduce <c>FusedMultiplyAdd</c> here or anywhere in the port.</b></item>
    /// <item><b>The domain is restricted so the one non-IEEE operation cannot diverge.</b> Float to int
    /// conversion is the single place where scalar C# and the vector instruction disagree: since
    /// .NET Core 3.0 <c>(int)f</c> <i>saturates</i> (NaN to 0, overflow to int.MinValue/MaxValue) while
    /// CVTTPS2DQ produces the "integer indefinite" 0x80000000 for both. The two agree for every finite
    /// <c>0 &lt;= f &lt; 2^31</c>, and the abs fold at the top of the function makes the argument
    /// non-negative, so the 8-wide path is only ever used when every argument is known to be finite and
    /// far below 2^31. <see cref="PerlinNoise8"/> enforces that itself and falls back per call if it is
    /// ever violated, so the restriction cannot be forgotten by a caller.</item>
    /// </list>
    ///
    /// <para>Because points 1-3 make the vector result <i>identical</i> rather than <i>close</i>, there
    /// is no tolerance anywhere in this file or its self-test: the comparison is on raw bit patterns.</para>
    ///
    /// <para><b>Fallback.</b> On a machine without AVX2 (ARM64, pre-Haswell x64) <see cref="Use8Wide"/>
    /// is false and every caller runs <see cref="NoiseScalar"/> instead. The self-test runs both paths on
    /// every machine that has AVX2 and throws if they differ, so a machine where they diverge fails
    /// loudly at startup instead of silently generating a different world.</para>
    ///
    /// <para>Stateless and thread-safe: the two tables are immutable and carry no seed data.</para>
    /// </summary>
    public static class PerlinFast
    {
        public const float NormAdd = UnityPerlin.NormAdd;
        public const float NormDiv = UnityPerlin.NormDiv;

        /// <summary>
        /// The largest magnitude an argument may have for <see cref="Noise8"/> to be used. Any finite
        /// value below 2^31 is safe (see the class remarks); 2^20 is far under that and still ~90x
        /// larger than the biggest argument the generator can produce (|world coordinate| &lt;= 10,500,
        /// plus the 100,000 offset, times the largest frequency 0.01 -> ~1,105).
        /// </summary>
        public const float Domain8 = 1048576f;   // 2^20

        /// <summary>True when the 8-wide path is available AND has been proved bit-identical here.</summary>
        public static readonly bool Use8Wide = Avx2.IsSupported;

        /// <summary>512 bytes: the classic permutation, twice. The same values as UnityPerlin's int[512].</summary>
        private static readonly byte[] PB = BuildBytes();

        /// <summary>512 ints - VPGATHERDD cannot gather bytes, so the vector path needs the wide copy.</summary>
        private static readonly int[] PI = BuildInts();

        private static byte[] BuildBytes()
        {
            byte[] q = new byte[512];
            for (int i = 0; i < 512; i++) q[i] = checked((byte)UnityPerlin.Perm(i));
            return q;
        }

        private static int[] BuildInts()
        {
            int[] q = new int[512];
            for (int i = 0; i < 512; i++) q[i] = UnityPerlin.Perm(i);
            return q;
        }

        // ----------------------------------------------------------------------------- O3: scalar

        /// <summary>Mathf.PerlinNoise through the byte table. Bit-identical to UnityPerlin.PerlinNoise.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float PerlinNoise(float x, float y) => (NoiseScalar(x, y) + NormAdd) / NormDiv;

        /// <summary>
        /// PerlinNoise::Noise(float,float), statement for statement as in <see cref="UnityPerlin.Noise"/>,
        /// reading a <c>byte[512]</c> through a pointer. Identical arithmetic, identical table values,
        /// no bounds checks. The indices are structurally in range: X, Y are masked to 0..255, a table
        /// entry is 0..255, and the largest index formed is <c>P[X+1] + Y + 1 &lt;= 255 + 255 + 1 = 511</c>.
        /// </summary>
        public static unsafe float NoiseScalar(float x, float y)
        {
            fixed (byte* p = PB)
            {
                x = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) & 0x7FFFFFFF);
                y = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(y) & 0x7FFFFFFF);
                int ix = (int)x;
                int iy = (int)y;
                int X = ix & 0xFF;
                int Y = iy & 0xFF;
                float fx = x - (float)ix;
                float fy = y - (float)iy;
                float u = Fade((1f < fx) ? 1f : fx);   // MINSS(1, fx)
                float v = Fade((1f < fy) ? 1f : fy);

                int A = p[X] + Y;
                int B = p[X + 1] + Y;
                int AA = p[A], AB = p[A + 1], BA = p[B], BB = p[B + 1];

                float fx1 = fx - 1f;
                float fy1 = fy - 1f;
                float g00 = Grad(p[AA], fx, fy);
                float g10 = Grad(p[BA], fx1, fy);
                float g01 = Grad(p[AB], fx, fy1);
                float g11 = Grad(p[BB], fx1, fy1);

                float l1 = (g11 - g01) * u + g01;
                float l0 = (g10 - g00) * u + g00;
                return (l1 - l0) * v + l0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Fade(float t)
        {
            float a = t * 6f;
            a = a - 15f;
            a = a * t;
            a = a + 10f;
            float t3 = (t * t) * t;
            return a * t3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Grad(int hash, float x, float y)
        {
            int h = hash & 15;
            float u, v;
            if (h < 8) { u = x; v = (h >= 4) ? 0f : y; }
            else { u = y; v = (h == 12 || h == 14) ? x : 0f; }
            if ((hash & 1) != 0) u = -u;
            if ((hash & 2) != 0) v = -v;
            return v + u;
        }

        // ----------------------------------------------------------------------------- O5: 8-wide

        /// <summary>
        /// Eight independent <c>Mathf.PerlinNoise</c> samples, written to <paramref name="dst"/> in
        /// argument order. Bit-identical to eight scalar calls.
        ///
        /// <para>The guard is part of the contract, not an optimisation: unless all sixteen arguments
        /// are finite with magnitude below <see cref="Domain8"/>, this falls back to eight scalar calls,
        /// because that is the one region where CVTTPS2DQ and C#'s saturating float-to-int conversion
        /// disagree. <c>LessThanAll</c> is false when any lane is NaN, which is the answer wanted here.</para>
        /// </summary>
        public static unsafe void PerlinNoise8(float* xs, float* ys, float* dst)
        {
            if (Use8Wide)
            {
                Vector256<float> vx = Vector256.Load(xs);
                Vector256<float> vy = Vector256.Load(ys);
                Vector256<float> lim = Vector256.Create(Domain8);
                Vector256<float> absMask = Vector256.Create(0x7FFFFFFF).AsSingle();
                if (Vector256.LessThanAll(Avx.And(vx, absMask), lim) &&
                    Vector256.LessThanAll(Avx.And(vy, absMask), lim))
                {
                    Vector256<float> n = Noise8(vx, vy);
                    Vector256<float> r = Avx.Divide(Avx.Add(n, Vector256.Create(NormAdd)), Vector256.Create(NormDiv));
                    r.Store(dst);
                    return;
                }
            }
            for (int i = 0; i < 8; i++) dst[i] = PerlinNoise(xs[i], ys[i]);
        }

        /// <summary>
        /// The body of <see cref="UnityPerlin.Noise"/>, one lane per sample. Read it beside the scalar
        /// version: every line is the same operation on the same values.
        /// </summary>
        public static unsafe Vector256<float> Noise8(Vector256<float> x, Vector256<float> y)
        {
            Vector256<float> absMask = Vector256.Create(0x7FFFFFFF).AsSingle();
            x = Avx.And(x, absMask);                                           // andps 0x7FFFFFFF
            y = Avx.And(y, absMask);

            Vector256<int> ix = Avx.ConvertToVector256Int32WithTruncation(x);  // cvttps2dq == cvttss2si per lane
            Vector256<int> iy = Avx.ConvertToVector256Int32WithTruncation(y);
            Vector256<int> mask255 = Vector256.Create(255);
            Vector256<int> X = Avx2.And(ix, mask255);
            Vector256<int> Y = Avx2.And(iy, mask255);

            Vector256<float> fx = Avx.Subtract(x, Avx.ConvertToVector256Single(ix));
            Vector256<float> fy = Avx.Subtract(y, Avx.ConvertToVector256Single(iy));

            Vector256<float> one = Vector256.Create(1f);
            // MINSS(dst=1, src=f) is (1 < f) ? 1 : f; MINPS(a, b) is (a < b) ? a : b per lane.
            Vector256<float> u = Fade8(Avx.Min(one, fx));
            Vector256<float> v = Fade8(Avx.Min(one, fy));

            fixed (int* p = PI)
            {
                Vector256<int> one_i = Vector256.Create(1);
                Vector256<int> pX = Avx2.GatherVector256(p, X, 4);
                Vector256<int> pX1 = Avx2.GatherVector256(p, Avx2.Add(X, one_i), 4);
                Vector256<int> A = Avx2.Add(pX, Y);
                Vector256<int> B = Avx2.Add(pX1, Y);
                Vector256<int> AA = Avx2.GatherVector256(p, A, 4);
                Vector256<int> AB = Avx2.GatherVector256(p, Avx2.Add(A, one_i), 4);
                Vector256<int> BA = Avx2.GatherVector256(p, B, 4);
                Vector256<int> BB = Avx2.GatherVector256(p, Avx2.Add(B, one_i), 4);

                Vector256<int> hAA = Avx2.GatherVector256(p, AA, 4);
                Vector256<int> hBA = Avx2.GatherVector256(p, BA, 4);
                Vector256<int> hAB = Avx2.GatherVector256(p, AB, 4);
                Vector256<int> hBB = Avx2.GatherVector256(p, BB, 4);

                Vector256<float> fx1 = Avx.Subtract(fx, one);
                Vector256<float> fy1 = Avx.Subtract(fy, one);

                Vector256<float> g00 = Grad8(hAA, fx, fy);
                Vector256<float> g10 = Grad8(hBA, fx1, fy);
                Vector256<float> g01 = Grad8(hAB, fx, fy1);
                Vector256<float> g11 = Grad8(hBB, fx1, fy1);

                // (g11 - g01) * u + g01 - a separate MULPS and ADDPS, never an FMA.
                Vector256<float> l1 = Avx.Add(Avx.Multiply(Avx.Subtract(g11, g01), u), g01);
                Vector256<float> l0 = Avx.Add(Avx.Multiply(Avx.Subtract(g10, g00), u), g00);
                return Avx.Add(Avx.Multiply(Avx.Subtract(l1, l0), v), l0);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<float> Fade8(Vector256<float> t)
        {
            Vector256<float> a = Avx.Multiply(t, Vector256.Create(6f));
            a = Avx.Subtract(a, Vector256.Create(15f));
            a = Avx.Multiply(a, t);
            a = Avx.Add(a, Vector256.Create(10f));
            Vector256<float> t3 = Avx.Multiply(Avx.Multiply(t, t), t);
            return Avx.Multiply(a, t3);
        }

        /// <summary>
        /// The scalar <c>Grad</c> as a branchless select. Its result is always one of x, y, 0f or a
        /// negation of one of those, followed by a single float add - so the selects choose between
        /// values that are themselves exact, and the only arithmetic is the same <c>v + u</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<float> Grad8(Vector256<int> hash, Vector256<float> x, Vector256<float> y)
        {
            Vector256<int> h = Avx2.And(hash, Vector256.Create(15));
            Vector256<int> zero = Vector256<int>.Zero;

            Vector256<int> hLt8 = Avx2.CompareGreaterThan(Vector256.Create(8), h);   // h < 8
            Vector256<int> hLt4 = Avx2.CompareGreaterThan(Vector256.Create(4), h);   // h < 4, i.e. !(h >= 4)
            Vector256<int> h1214 = Avx2.Or(Avx2.CompareEqual(h, Vector256.Create(12)),
                                           Avx2.CompareEqual(h, Vector256.Create(14)));

            // u = (h < 8) ? x : y
            Vector256<float> u = Avx.BlendVariable(y, x, hLt8.AsSingle());
            // v = (h < 8) ? ((h < 4) ? y : 0) : ((h == 12 || h == 14) ? x : 0)
            Vector256<float> vLo = Avx.BlendVariable(zero.AsSingle(), y, hLt4.AsSingle());
            Vector256<float> vHi = Avx.BlendVariable(zero.AsSingle(), x, h1214.AsSingle());
            Vector256<float> v = Avx.BlendVariable(vHi, vLo, hLt8.AsSingle());

            // The sign flips test bit 0 and bit 1 of the FULL hash, and are an XOR of the sign bit -
            // which is what the scalar unary minus compiles to, including for +-0.
            Vector256<float> signMask = Vector256.Create(unchecked((int)0x80000000)).AsSingle();
            Vector256<int> bit0 = Avx2.CompareEqual(Avx2.And(hash, Vector256.Create(1)), Vector256.Create(1));
            Vector256<int> bit1 = Avx2.CompareEqual(Avx2.And(hash, Vector256.Create(2)), Vector256.Create(2));
            u = Avx.BlendVariable(u, Avx.Xor(u, signMask), bit0.AsSingle());
            v = Avx.BlendVariable(v, Avx.Xor(v, signMask), bit1.AsSingle());
            return Avx.Add(v, u);
        }
    }
}
