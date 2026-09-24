using System;

namespace SeedLab.WorldGen.Unity
{
    /// <summary>
    /// UnityEngine.Random, transcribed from UnityPlayer.dll 6000.0.75f1 through Mono's internal-call
    /// registration table (InitState 0x000B0CA0, RandomRangeInt 0x00054900, Range(float,float)
    /// 0x000B0CF0, get_value 0x000B0D80, GetRandomUnitCircle 0x000B0E70).
    ///
    /// <para><b>Measured against the running game, 2026-09-23.</b> The dumper plugin recorded
    /// <c>goldens/natives-random.json</c> inside Valheim 1.0.15 / Unity 6000.0.75f1 and every call
    /// below was replayed against it - result BITS and the four state words after each draw, so an
    /// implementation that returns the right number from the wrong number of draws still fails.
    /// Result: <b>276/276 traces, 1980/1980 draws, 268/268 InitState seeds</b>, all exact
    /// (<c>tests\SeedLab.Tests -- natives</c>, goldens in <c>groundtruth\natives\</c>).
    /// That closes spec 03 section 5.2 items R1, R2 and R4, and the draw count of
    /// <c>insideUnitCircle</c>. R3/R5 are narrowed but NOT closed - see
    /// <see cref="InsideUnitCircle"/>.</para>
    ///
    /// <para>Also proven by that dump rather than assumed: the game's native <c>Random.state</c>
    /// setter round-trips exactly (<c>stateRoundTrip.statesEqual</c> and <c>drawsEqual</c> both true),
    /// which is the premise every guard in the dumper - and <c>WorldGenerator..ctor</c> itself -
    /// rests on.</para>
    ///
    /// The InitState recurrence, the xorshift128 step and Range(int,int) were already confirmed
    /// against real game output before the dump: the seven world-generation draws for seed
    /// -1772362158 were recovered from the game's own minimap cache and match this implementation
    /// (specs/03-unity-natives.md section 6.1). The dump then re-confirmed the same seven-draw
    /// constructor sequence - <c>offset0..3</c>, <c>riverSeed</c>, <c>streamSeed</c>, <c>offset4</c>
    /// LAST - on 268 separate seeds.
    ///
    /// The game has ONE global instance and it is not thread-safe; this type is an instance so each
    /// search worker owns its own generator. Never make it static.
    /// </summary>
    public sealed class UnityRandom
    {
        public int s0, s1, s2, s3;                       // == UnityEngine.Random.State field order

        /// <summary>Random.InitState(int). The Random.seed setter is the same native function.</summary>
        public void InitState(int seed)
        {
            unchecked
            {
                s0 = seed;
                s1 = s0 * 1812433253 + 1;                // 0x6C078965
                s2 = s1 * 1812433253 + 1;
                s3 = s2 * 1812433253 + 1;
            }
        }

        /// <summary>Random.state get - a raw copy of the four ints, nothing else.</summary>
        public (int, int, int, int) GetState() => (s0, s1, s2, s3);

        /// <summary>Random.state set.</summary>
        public void SetState((int, int, int, int) st) => (s0, s1, s2, s3) = st;

        /// <summary>One xorshift128 step (shifts 11, 8, 19); returns the new s3.</summary>
        public uint Next()
        {
            unchecked
            {
                uint x0 = (uint)s0, x1 = (uint)s1, x2 = (uint)s2, x3 = (uint)s3;
                uint t = (x0 << 11) ^ x0;
                s0 = (int)x1; s1 = (int)x2; s2 = (int)x3;
                uint w = x3 ^ t ^ (((x3 >> 11) ^ t) >> 8);
                s3 = (int)w;
                return w;
            }
        }

        public const float Scale = 1.1920930376163765E-07f;   // 0x34000001 == 1f/8388607f

        /// <summary>
        /// Random.value (rva 0x000B0D80).
        /// <para>Settled by the dump: <c>D5-value</c> (64 draws from InitState(-1772362158)),
        /// <c>D9-long-run</c> (the value and the state after 100 000 discarded draws from
        /// InitState(0)) and the four control draws of <c>stateRoundTrip</c> all reproduce bit for
        /// bit, which pins the <c>&amp; 0x7FFFFF</c> mask and the <c>1f/8388607f</c> scale together.
        /// The long run is what rules out a carry or ordering error that only appears after thousands
        /// of steps.</para>
        /// </summary>
        public float Value() => (float)(long)(Next() & 0x7FFFFFu) * Scale;

        /// <summary>
        /// Random.Range(float, float) (rva 0x000B0CF0) - ALWAYS consumes one draw.
        ///
        /// <para><b>Spec 03 R1 is CLOSED: the form is (1-f)*max + f*min.</b> Golden
        /// <c>natives-random.json</c>, trace <c>D6-range-float</c> draw 0:
        /// <c>Range(60f, 100f)</c> from InitState(744350289) is <b>0x42BF3B2E</b>, while the forward
        /// form <c>min + f*(max-min)</c> and <c>Mathf.Lerp</c> both give 0x4280C4D2 - 31 ULPs away,
        /// not one. Trace <c>D6b-range-float-reversed</c> then separates this form from the third
        /// candidate <c>max + f*(min-max)</c>, which agrees on the forward call and differs by one
        /// ULP on the reversed one: the game's <c>Range(100f, 60f)</c> is <b>0x4280C4D3</b>, this
        /// form gives 0x4280C4D3, <c>max + f*(min-max)</c> gives 0x4280C4D2.</para>
        ///
        /// <para><b>Spec 03 R2 is CLOSED for this overload: it always draws.</b>
        /// <c>D7-same-min-max</c> records <c>Range(20f, 20f)</c> with
        /// <c>stateUnchanged = false</c>.</para>
        ///
        /// <para>This is NATIVE code (UnityPlayer.dll), not managed IL, so the float chain below is
        /// literal: mulss/addss on float32, three roundings. Do not "correct" it to the R8-on-the-stack
        /// form that the managed Vector2/WorldGenerator members need - that rule is a property of Mono's
        /// JIT and stops at the interop boundary.</para>
        ///
        /// <para><b>Range(a, a) does not always return a.</b> <c>(1f - f) * a + f * a</c> is two
        /// separately rounded products, not an identity. Measured over the 2^23 possible draw values,
        /// 2.50 % of them make Range(20f, 20f) return 20.000002f (one ULP at 20) instead of 20f; only
        /// those two values ever occur. On the real worlds that is 10,205 of 392,809 stream-radius points
        /// (asdasdasd) and 9,302 of 376,841 (testworldclaude).
        /// 01-worldgen-core.md 4.5 says the value "is 20 regardless" - it is not, and that line should
        /// be corrected. Numerically it is harmless (a 1-ULP radius moves the height by &lt;= 1e-7 m),
        /// but <b>never write <c>rp.w == 20f</c> to tell a stream point from a river point</b>: it
        /// silently misses about one stream point in 40.</para>
        /// </summary>
        public float Range(float minInclusive, float maxInclusive)
        {
            float f = (float)(long)(Next() & 0x7FFFFFu) * Scale;
            return (1f - f) * maxInclusive + f * minInclusive;      // note: reversed, as in the binary
        }

        /// <summary>
        /// Random.Range(int, int) (rva 0x00054900) - consumes a draw ONLY when min == max is false.
        /// <para><b>Spec 03 R2 is CLOSED for this overload: min == max does NOT draw.</b> No
        /// world-generation call site passes min == max, so nothing in the game could ever prove it
        /// and it rested on the disassembly alone. Golden <c>D7-same-min-max</c> measures it directly:
        /// <c>Range(5,5)</c> and <c>Range(0,0)</c> both report <c>stateUnchanged = true</c>, while the
        /// contrasting <c>Range(0,1)</c> in the same trace reports false.</para>
        /// <para>The mapping itself, including the full <c>int.MinValue..int.MaxValue</c> range that
        /// draws <c>m_riverSeed</c> and <c>m_streamSeed</c>, is confirmed by <c>D10-range-int</c>,
        /// <c>D10b-range-int-full</c> and the 268 <c>worldgen-ctor</c> traces.</para>
        /// </summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            unchecked
            {
                if (minInclusive < maxExclusive)
                    return (int)((uint)minInclusive + (Next() % (uint)(maxExclusive - minInclusive)));
                if (minInclusive > maxExclusive)
                    return (int)((uint)minInclusive - (Next() % (uint)(minInclusive - maxExclusive)));
                return minInclusive;
            }
        }

        /// <summary>
        /// Random.insideUnitCircle (rva 0x000B0E70) - exactly 2 draws, no rejection sampling.
        ///
        /// <para><b>Settled by golden <c>D8-inside-unit-circle</c></b> (8 pairs from InitState(12345)),
        /// all 8 bit-exact in both components:</para>
        /// <list type="bullet">
        /// <item><b>Draw count:</b> the recorded state advances by exactly two xorshift steps per
        /// call, so there is no rejection sampling and no third draw.</item>
        /// <item><b>R4 - component order: CLOSED, x = cos and y = sin.</b> Swapping them misses all
        /// 16 components (e.g. draw 0 is x=0xBE8AB290, y=0x3E286F63).</item>
        /// <item><b>Radius:</b> <c>sqrt(Range(0f,1f))</c> with the second draw, confirmed by the same
        /// 8 pairs.</item>
        /// </list>
        ///
        /// <para><b>R3 and R5 are NARROWED, NOT CLOSED - do not mark them settled.</b> The two
        /// remaining trig candidates, <c>(float)Math.Cos((double)a)</c> (below) and
        /// <c>MathF.Cos(a)</c> (which is what the binary's own <c>cosf</c> would most nearly be),
        /// <b>agree with each other on all 8 recorded angles</b>, so D8 confirms this line but does
        /// not discriminate the two. Measured over every angle <c>Range(0f, 2*pi)</c> can produce
        /// (all 2^23 draw values): the two forms differ on <b>21,694 / 8,388,608 = 0.2586 %</b> of
        /// angles, always by exactly 1 ULP; the chance that 8 arbitrary draws all miss that set is
        /// 0.98, and all 8 of D8's did. Closing R3 needs either ~2000 recorded draws (P(miss) drops
        /// to 0.006) or an end-to-end match against
        /// <c>goldens/locationinstances-0480A34C.json</c>, since <c>GetTerrainDelta</c> is the only
        /// consumer and draws 10 of these per candidate point.</para>
        ///
        /// <para>Blast radius of the residual: <c>insideUnitCircle</c> reaches nothing but
        /// <c>GetTerrainDelta</c>, so it cannot move a biome or a minimap height - only whether a
        /// candidate location point passes its terrain-delta filter, and only for the 0.26 % of
        /// angles where a 1-ULP offset could tip that comparison.</para>
        /// </summary>
        public (float x, float y) InsideUnitCircle()
        {
            float a = Range(0f, 6.28318548f);            // 2*pi as float, 0x40C90FDB
            float t = Range(0f, 1f);
            float r = MathF.Sqrt(t);                     // sqrtss, IEEE-exact
            // R3: confirmed bit-exact on D8's 8 pairs, but MathF.Cos/Sin match those 8 too - see above.
            return ((float)Math.Cos((double)a) * r,
                    (float)Math.Sin((double)a) * r);
        }
    }
}
