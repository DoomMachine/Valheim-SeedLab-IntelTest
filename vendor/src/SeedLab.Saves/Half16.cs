using System;

namespace SeedLab.Saves
{
    /// <summary>
    /// IEEE-754 binary16 decoding for the minimap height cache.
    /// <para>
    /// <c>Utils.FloatsToCompressedHalfBuffer</c> (decompiled) stores
    /// <c>Mathf.FloatToHalf(px[i])</c> as a little-endian <c>ushort</c> per pixel. The decode is
    /// written out in full rather than delegated, because the spec's correction in 05-validation.md
    /// section 5.5 is load-bearing: <b>subnormal halves really do occur</b> - <c>asdasdasd</c> holds
    /// 304 distinct subnormal codes over 363 pixels plus one <c>0x8000</c> (<c>-0.0</c>), and
    /// <c>testworldclaude</c> 330 codes over 392 pixels plus two <c>-0.0</c>. A decoder that assumes
    /// normalised halves mis-scores those pixels, and one that compares decoded floats rather than
    /// bit patterns cannot see a <c>+0.0</c>/<c>-0.0</c> sign error at all.
    /// </para>
    /// <para>
    /// Only the decode direction exists, and that is deliberate. The encode direction is
    /// <c>Mathf.FloatToHalf</c>, a native <c>[FreeFunction(IsThreadSafe = true)] extern</c>;
    /// 05-validation.md section 1.3 lists its rounding mode as Unverified and guesses
    /// round-to-nearest-even, but it has since been <b>measured as ties-away-from-zero</b>
    /// (2026-09-22, against the game's own minimap height cache: of 163 disagreeing height pixels,
    /// 158 sat exactly on a half midpoint and all 158 matched the game once the tie was broken away
    /// from zero, taking the count 163 -&gt; 5). So .NET's <c>(Half)f</c> is <b>not</b> a correct
    /// encoder for this file, and a reader has no business encoding anyway - whoever needs to write
    /// halves should own that decision explicitly.
    /// </para>
    /// </summary>
    public static class Half16
    {
        /// <summary>The bit pattern for -400.0f, the value <c>GetBiomeHeight</c> returns outside the world edge.</summary>
        /// <remarks>
        /// <c>WorldGenerator.GetBiomeHeight</c> line 1032:
        /// <c>if (DUtils.Length(wx, wy) &gt; 10500f) return -2f * GetHeightMultiplier();</c> with
        /// <c>GetHeightMultiplier() =&gt; 200f</c>, so -400. -400 is exactly representable in binary16
        /// (sign 1, exponent 23, mantissa 576), and no other code decodes to it.
        /// </remarks>
        public const ushort NegativeFourHundred = 0xDE40;

        /// <summary>The value stored outside the world edge, in metres.</summary>
        public const float WorldEdgeHeight = -400f;

        /// <summary>
        /// Decodes one binary16 bit pattern: normals, subnormals, signed zero, infinities and NaN.
        /// </summary>
        public static float ToSingle(ushort bits)
        {
            int sign = (bits >> 15) & 0x1;
            int exponent = (bits >> 10) & 0x1F;
            int mantissa = bits & 0x3FF;

            int resultBits;
            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    // Signed zero. Preserved exactly: the caches contain 0x8000.
                    resultBits = sign << 31;
                }
                else
                {
                    // Subnormal: value = mantissa * 2^-24. Renormalise into a binary32 normal.
                    int shift = 0;
                    while ((mantissa & 0x400) == 0)
                    {
                        mantissa <<= 1;
                        shift++;
                    }
                    mantissa &= 0x3FF;
                    int exp32 = 127 - 15 - shift + 1;
                    resultBits = (sign << 31) | (exp32 << 23) | (mantissa << 13);
                }
            }
            else if (exponent == 0x1F)
            {
                // Infinity (mantissa 0) or NaN. Neither occurs in either ground-truth cache, but a
                // decoder that cannot represent them would silently turn one into a finite number.
                // The NaN payload is carried through verbatim (half bit 9, the quiet bit, lands on
                // float bit 22, also the quiet bit). This differs from the BCL's Half->float for the
                // 1 022 *signalling* NaN codes, which .NET quiets; see SelfTest.
                resultBits = (sign << 31) | (0xFF << 23) | (mantissa << 13);
            }
            else
            {
                resultBits = (sign << 31) | ((exponent - 15 + 127) << 23) | (mantissa << 13);
            }

            return BitConverter.Int32BitsToSingle(resultBits);
        }

        /// <summary>
        /// Checks this decoder against the BCL's own binary16 over all 65 536 codes, comparing raw
        /// bit patterns so that signed zeros and subnormals count. Returns the number of
        /// disagreements, which must be 0. Cheap enough to run as a start-up self-test.
        /// <para>
        /// NaN codes are compared as "both are NaN" rather than bit for bit. Measured: this decoder
        /// and <c>(float)BitConverter.UInt16BitsToHalf(bits)</c> agree bit for bit on all 63 490
        /// non-NaN codes and on the 1 024 quiet NaNs, and differ on the 1 022 <i>signalling</i> NaN
        /// codes (mantissa 1..511), which .NET quietens and this decoder passes through. IEEE-754
        /// leaves NaN payload propagation to the implementation, no cache pixel in either
        /// ground-truth world holds a NaN or infinity code, and <c>GetBiomeHeight</c> cannot produce
        /// one for finite coordinates - so the difference is noted rather than papered over.
        /// </para>
        /// </summary>
        public static int SelfTest() => SelfTest(out _);

        /// <summary>
        /// <see cref="SelfTest"/>, additionally reporting how many NaN codes differ only in payload.
        /// </summary>
        public static int SelfTest(out int nanPayloadDifferences)
        {
            int wrong = 0;
            int nanDiff = 0;
            for (int i = 0; i <= ushort.MaxValue; i++)
            {
                float mine = ToSingle((ushort)i);
                float bcl = (float)BitConverter.UInt16BitsToHalf((ushort)i);
                if (BitConverter.SingleToInt32Bits(mine) == BitConverter.SingleToInt32Bits(bcl)) continue;
                if (float.IsNaN(mine) && float.IsNaN(bcl)) nanDiff++;
                else wrong++;
            }
            nanPayloadDifferences = nanDiff;
            return wrong;
        }

        /// <summary>True for the 2 048 codes with exponent 31: the two infinities and every NaN.</summary>
        public static bool IsNonFinite(ushort bits) => ((bits >> 10) & 0x1F) == 0x1F;

        /// <summary>True for the 1 024 subnormal codes (exponent 0, mantissa non-zero), either sign.</summary>
        public static bool IsSubnormal(ushort bits) => ((bits >> 10) & 0x1F) == 0 && (bits & 0x3FF) != 0;
    }
}
