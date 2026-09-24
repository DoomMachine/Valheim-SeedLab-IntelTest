using System;

namespace SeedLab.Seeds
{
    /// <summary>
    /// The arithmetic of the seed space, for the CLI to quote and for the search planner to size its
    /// denominators from. Spec 06 sections 2, 3.3, 4 and 5.2.
    ///
    /// The one fact that reshapes the whole tool: there really are 853,058,371,866,181,866 seed
    /// TEXTS of 1..10 alphanumeric characters, but <c>World..ctor</c> crushes each one into a single
    /// int32 before anything else happens, and generation never sees the text again. So there are at
    /// most 2^32 = 4,294,967,296 distinct worlds, all of which actually occur, and on average
    /// 198,618,130 different texts open the very same world. A search enumerates the ints.
    /// </summary>
    public static class SeedSpace
    {
        /// <summary>
        /// 2^32. <c>World.m_seed</c> is one int, and <c>WorldGenerator</c> reads only that, the
        /// world-gen version and the menu flag (spec 06 section 4, closed by an exhaustive Cecil scan:
        /// <c>m_seedName</c> is referenced by twelve methods and none of them is in a generation path).
        /// </summary>
        public const ulong DistinctWorlds = 4294967296UL;

        /// <summary>
        /// The shortest length at which EVERY int32 is reachable, for both A62 and A59
        /// (spec 06 section 5.2, exact sumset computation reproduced by an independent reviewer).
        /// </summary>
        public const int ShortestUniversalLength = 7;

        /// <summary>|A|^length, exact.</summary>
        public static UInt128 TextCountOfLength(int alphabetSize, int length)
        {
            if (alphabetSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(alphabetSize), alphabetSize, "Alphabet must be non-empty.");
            }

            if (length < 0 || length > 20)
            {
                // 62^21 already needs 124 bits; refuse rather than wrap silently.
                throw new ArgumentOutOfRangeException(nameof(length), length, "length must be 0..20.");
            }

            UInt128 n = UInt128.One;
            for (int i = 0; i < length; i++)
            {
                n *= (UInt128)(uint)alphabetSize;
            }

            return n;
        }

        /// <summary>
        /// The number of seed texts of length 1..<paramref name="maxLength"/>, exact.
        /// For (62, 10) this is 853,058,371,866,181,866 and for (59, 10) it is
        /// 519,929,111,116,169,700 (spec 06 section 2).
        ///
        /// The game also accepts the EMPTY text - it maps to seed 0, not to a random world - so the
        /// count of texts the create-world box accepts is one larger.
        /// </summary>
        public static UInt128 TextCount(int alphabetSize, int maxLength)
        {
            UInt128 sum = UInt128.Zero;
            for (int length = 1; length <= maxLength; length++)
            {
                sum += TextCountOfLength(alphabetSize, length);
            }

            return sum;
        }

        /// <inheritdoc cref="TextCount(int,int)"/>
        public static UInt128 TextCount(SeedAlphabet alphabet, int maxLength = SeedText.MaxEmittedLength)
            => TextCount((alphabet ?? throw new ArgumentNullException(nameof(alphabet))).Count, maxLength);

        /// <summary>
        /// Seed texts per distinct world: <c>TextCount / 2^32</c>. 198,618,129.796 for (A62, 10) -
        /// the factor by which searching strings instead of ints would waste work.
        ///
        /// It is a mean, not a guarantee: at length 6 nearly a quarter of all ints have no preimage
        /// at all while the rest average 17.37 each (spec 06 section 4).
        /// </summary>
        public static double TextsPerWorld(SeedAlphabet alphabet, int maxLength = SeedText.MaxEmittedLength)
            => (double)TextCount(alphabet, maxLength) / DistinctWorlds;

        /// <summary>
        /// |E_n| computed live from the built tables - the number of distinct lane values reachable
        /// with exactly n characters. Only n &lt;= <see cref="LaneTables.MaxLevel"/>; use
        /// <see cref="PublishedLaneReachableCount"/> for n = 5, which is not materialised.
        /// </summary>
        public static int LaneReachableCount(SeedAlphabet alphabet, int n)
            => LaneTables.For(alphabet).LevelCount(n);

        /// <summary>
        /// |E_n| for n = 0..5 as computed in spec 06 section 3.3 and independently reproduced by the
        /// reviewer (section 11). n = 5 is here because materialising E_5 costs 64 M entries / 256 MB
        /// and the tool never needs it; n = 1..4 can and should be checked against
        /// <see cref="LaneReachableCount"/>.
        ///
        /// The lane collides hard: |E_5| is only 1.4926 % of 2^32 for A62. Do not assume the lanes
        /// are near-injective when sizing anything.
        /// </summary>
        public static long PublishedLaneReachableCount(SeedAlphabet alphabet, int n)
        {
            if (n < 0 || n > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(n), n, "Published lane counts cover n = 0..5.");
            }

            long[] counts = PublishedLaneCounts(alphabet);
            return counts[n];
        }

        private static long[] PublishedLaneCounts(SeedAlphabet alphabet)
        {
            if (ReferenceEquals(alphabet, SeedAlphabet.Alnum62))
            {
                return new long[] { 1, 62, 2_097, 66_014, 2_058_466, 64_105_880 };
            }

            if (ReferenceEquals(alphabet, SeedAlphabet.GameAlphabet59))
            {
                return new long[] { 1, 59, 2_048, 64_726, 2_016_367, 62_658_885 };
            }

            throw new ArgumentException(
                $"Spec 06 only publishes lane counts for A62 and A59, not '{alphabet.Name}'. " +
                "Compute them with LaneReachableCount (n <= 4).", nameof(alphabet));
        }

        /// <summary>
        /// How many of the 2^32 int seeds have SOME text of length 1..<paramref name="maxLength"/>,
        /// from the exact sumset computation of spec 06 section 5.2.
        ///
        /// A62: 142,962,629 at &lt;= 5 (3.3286 %), 3,310,424,872 at &lt;= 6 (77.0768 %), and all
        /// 4,294,967,296 from 7 upwards. A59: 136,877,472, 3,213,389,948, then complete.
        ///
        /// Completeness at 7 does not imply completeness at 8, 9 or 10 - the level sets are not
        /// nested, so each was computed separately and each came out complete.
        /// </summary>
        public static long ReachableSeedsUpToLength(SeedAlphabet alphabet, int maxLength)
        {
            if (maxLength < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "maxLength must be at least 1.");
            }

            if (maxLength >= ShortestUniversalLength)
            {
                return (long)DistinctWorlds;
            }

            bool a62 = ReferenceEquals(alphabet, SeedAlphabet.Alnum62);
            bool a59 = ReferenceEquals(alphabet, SeedAlphabet.GameAlphabet59);
            if (!a62 && !a59)
            {
                throw new ArgumentException(
                    $"Spec 06 only publishes coverage for A62 and A59, not '{alphabet.Name}'.", nameof(alphabet));
            }

            if (maxLength == 6)
            {
                return a62 ? 3_310_424_872L : 3_213_389_948L;
            }

            if (maxLength == 5)
            {
                return a62 ? 142_962_629L : 136_877_472L;
            }

            throw new ArgumentOutOfRangeException(
                nameof(maxLength), maxLength,
                "Spec 06 section 5.2 publishes unions for <= 5, <= 6 and >= 7 only.");
        }

        /// <summary>
        /// int seeds with NO text of length 1..<paramref name="maxLength"/>.
        /// 984,542,424 for A62 at 6 - including seed 0 itself, the menu world's seed, which is
        /// reachable only from the empty text or from 7 characters up.
        /// </summary>
        public static long UnreachableSeedsUpToLength(SeedAlphabet alphabet, int maxLength)
            => (long)DistinctWorlds - ReachableSeedsUpToLength(alphabet, maxLength);

        /// <summary>
        /// Probability that a uniformly random int32 seed's SHORTEST text is at most
        /// <paramref name="length"/> characters, straight from the exact unions above.
        /// A62: 3.3286 % at 5, 77.0768 % at 6, 100 % at 7.
        /// </summary>
        public static double ShortestLengthAtMostProbability(SeedAlphabet alphabet, int length)
            => (double)ReachableSeedsUpToLength(alphabet, length) / DistinctWorlds;

        /// <summary>
        /// Probability that a uniformly random int32 seed's SHORTEST text is exactly
        /// <paramref name="length"/> characters. Published only for 6 and 7, because spec 06
        /// section 5.2 gives exact unions for &lt;= 5, &lt;= 6 and &gt;= 7 and nothing finer below 5
        /// (lengths 1..4 together are 0.102 %, inside the &lt;= 5 figure).
        ///
        /// A62: 73.748 % at 6, 22.923 % at 7. Spec 06 section 6.2 measured 744 and 223 over 1,000
        /// random targets against exactly these.
        /// </summary>
        public static double ShortestLengthExactProbability(SeedAlphabet alphabet, int length)
        {
            if (length is not (6 or ShortestUniversalLength))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length), length,
                    "Exact shortest-length probabilities are published for 6 and 7 only; use " +
                    "ShortestLengthAtMostProbability(alphabet, 5) for everything below.");
            }

            long upto = ReachableSeedsUpToLength(alphabet, length);
            long below = ReachableSeedsUpToLength(alphabet, length - 1);
            return (double)(upto - below) / DistinctWorlds;
        }

        /// <summary>
        /// Exactly how many texts of length <paramref name="length"/> over
        /// <paramref name="alphabet"/> hash to <paramref name="seed"/>.
        ///
        /// Spec 06 section 6.3: the count is <c>sum over o in E_co of w_ce(target - K*o) * w_co(o)</c>.
        /// Enumerating E_co is exhaustive, so this is exact, not an estimate - the length-6 answer is
        /// 0 for 23.88 % of all ints and averages 17.37 over the rest. Costs one pass over E_co
        /// (66,014 values at length 6 or 7), so it is a reporting tool, not a hot-loop call.
        ///
        /// Limited to lengths whose odd lane is materialised (1..2*MaxLevel = 1..8).
        /// </summary>
        public static UInt128 PreimageCount(int seed, SeedAlphabet alphabet, int length)
        {
            LaneTables t = LaneTables.For(alphabet);
            if (length < 1 || length > 2 * t.MaxLevel)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length), length,
                    $"Exact preimage counts need E_floor(L/2), so length must be 1..{2 * t.MaxLevel}.");
            }

            int ce = (length + 1) / 2;
            int co = length / 2;
            uint target = unchecked((uint)seed);
            UInt128 total = UInt128.Zero;
            ReadOnlySpan<uint> odds = t.Level(co);
            for (int i = 0; i < odds.Length; i++)
            {
                uint o = odds[i];
                int we = t.LaneWeight(unchecked(target - LaneTables.Combiner * o), ce);
                if (we != 0)
                {
                    total += (UInt128)(uint)(we * t.LaneWeight(o, co));
                }
            }

            return total;
        }

        /// <summary>
        /// Twelve int seeds that spec 06 section 5.2 confirms have NO A62 text of length 6 or less,
        /// each with a verified 7-character witness. Useful as a regression fixture.
        /// </summary>
        public static ReadOnlySpan<int> KnownUnreachableAtSixAlnum62
            => new[] { 0, 1, 2, 3, 32, 33, 34, 35, 44, 64, 65, 66 };
    }
}
