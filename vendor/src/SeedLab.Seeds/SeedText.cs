using System;

namespace SeedLab.Seeds
{
    /// <summary>
    /// The inverse of <see cref="StableHash"/>: turn an int32 world seed back into a seed text the
    /// user can type into the create-world box.
    ///
    /// Spec 06 section 6. Because <c>hash = E + K*O</c> over the two independent lanes, picking the
    /// odd lane forces the even one (<c>E = target - K*O</c>), and the lane step is invertible given
    /// the character, so a preimage is found by walking E backwards through the
    /// <see cref="LaneTables"/> level sets. No search over 2^32 and no 512 MB bitmap.
    ///
    /// Every text returned by this type has been re-hashed with the real
    /// <see cref="StableHash.Compute(ReadOnlySpan{char})"/> before it leaves the method. A seed text
    /// that does not open the world it was advertised to open is the one failure the user cannot
    /// detect until they have played it, so an unverified preimage is never handed back.
    ///
    /// Emitted texts are at most 10 characters. That is what <c>World.GenerateSeed</c> itself
    /// produces and the only length known to be safe: the create-world field's
    /// <c>characterLimit</c> is serialized prefab data and could not be read offline
    /// (spec 06 section 7.3, still Unverified).
    /// </summary>
    public static class SeedText
    {
        /// <summary>
        /// The longest text this type will ever emit. Spec 06 section 7.3: until the field's
        /// character limit is measured in game, 10 is the only length known to be typeable.
        /// </summary>
        public const int MaxEmittedLength = 10;

        /// <summary>
        /// The shortest seed text over <paramref name="alphabet"/> that hashes to
        /// <paramref name="seed"/>, or null if none exists at or below
        /// <paramref name="maxLength"/>.
        ///
        /// Spec 06 section 6.1, "producing the shortest possible text": for L = 1, 2, 3, ... with
        /// ce = ceil(L/2) and co = floor(L/2), enumerate ALL of E_co and test whether
        /// <c>target - K*o</c> is in E_ce. The first L that yields a hit is provably the shortest,
        /// because the enumeration of E_co is exhaustive. E_co is always the smaller of the two
        /// level sets, so this is also the cheaper direction.
        ///
        /// For A62 and A59 the loop always terminates at L &lt;= 7: spec 06 section 5.2 proves by
        /// exact sumset computation that all 4,294,967,296 int seeds are reachable at length 7,
        /// while 984,542,424 of them (22.92 %) have no text of length 6 or less. The typical answer
        /// is 6 characters (73.75 %), sometimes 7 (22.92 %).
        ///
        /// Note this never returns the empty string. The empty seed text also produces seed 0
        /// (<c>World..ctor</c> maps "" to 0, see <see cref="StableHash.SeedFromText"/>), but an
        /// empty create-world box reads as "random" to a user, so seed 0 is reported as a real text.
        /// </summary>
        /// <param name="seed">The int32 world seed to hit.</param>
        /// <param name="alphabet">Characters the text may use.</param>
        /// <param name="maxLength">Longest text to consider; capped at <see cref="MaxEmittedLength"/>.</param>
        public static string? Invert(int seed, SeedAlphabet alphabet, int maxLength = MaxEmittedLength)
        {
            if (alphabet is null)
            {
                throw new ArgumentNullException(nameof(alphabet));
            }

            if (maxLength < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "maxLength must be at least 1.");
            }

            LaneTables t = LaneTables.For(alphabet);
            uint target = unchecked((uint)seed);

            Span<char> even = stackalloc char[MaxEmittedLength];
            Span<char> odd = stackalloc char[MaxEmittedLength];
            Span<char> text = stackalloc char[MaxEmittedLength];

            // E_co must be enumerable, so co <= MaxLevel, i.e. L <= 2*MaxLevel + 1 = 9. E_5 has
            // 64,105,880 entries (spec 06 section 3.3) and is deliberately not materialised.
            int exhaustiveMax = Math.Min(Math.Min(maxLength, MaxEmittedLength), 2 * t.MaxLevel + 1);

            for (int length = 1; length <= exhaustiveMax; length++)
            {
                int ce = (length + 1) / 2;
                int co = length / 2;
                ReadOnlySpan<uint> odds = t.Level(co);

                for (int i = 0; i < odds.Length; i++)
                {
                    uint o = odds[i];
                    uint e = unchecked(target - LaneTables.Combiner * o);
                    if (!t.TryReconstructLane(e, ce, even))
                    {
                        continue;
                    }

                    // o came out of E_co, so this cannot fail; it is how we recover the characters.
                    if (!t.TryReconstructLane(o, co, odd))
                    {
                        continue;
                    }

                    Interleave(even, odd, text, length);
                    return Verified(seed, text.Slice(0, length));
                }
            }

            // Lengths 10 and up cannot be searched exhaustively with level-4 tables, so fall back to
            // the randomised fixed-length sampler. Unreachable for A62 and A59, where section 5.2
            // guarantees a hit at L <= 7; it exists so a custom alphabet degrades instead of lying.
            for (int length = exhaustiveMax + 1; length <= Math.Min(maxLength, MaxEmittedLength); length++)
            {
                string? s = InvertFixedLength(seed, alphabet, length);
                if (s is not null)
                {
                    return s;
                }
            }

            return null;
        }

        /// <summary>
        /// A 10-character seed text in the game's own 59-character alphabet, drawn UNIFORMLY from the
        /// set of such texts that hash to <paramref name="seed"/>.
        ///
        /// Spec 06 section 6.1.1. Because <c>World.GenerateSeed</c> draws all ten positions uniformly
        /// and independently, a uniform draw from the preimage set is exactly the conditional
        /// distribution of a game-suggested seed given its hash - so the result is statistically
        /// indistinguishable from a seed the game would have offered. The greedy descent originally
        /// specified is NOT: its even positions measured chi-squared up to 130,232 against df 58,
        /// a 26x spread between the most and least common symbol.
        ///
        /// Every int32 is reachable at this length (section 5.2), so this always succeeds.
        /// </summary>
        /// <param name="rng">
        /// Pass a per-worker <see cref="Random"/> in a parallel search. The default
        /// <see cref="Random.Shared"/> is thread-safe but contended.
        /// </param>
        public static string GenerateGameStyle(int seed, Random? rng = null)
        {
            string? s = InvertFixedLength(seed, SeedAlphabet.GameAlphabet59, 10, rng);
            if (s is null)
            {
                throw new InvalidOperationException(
                    $"No 10-character A59 text found for seed {seed}, which spec 06 section 5.2 proves " +
                    "is impossible. Either the probe budget was hit or the lane tables are wrong.");
            }

            return s;
        }

        /// <summary>
        /// A seed text of exactly <paramref name="length"/> characters, drawn uniformly from the set
        /// of such texts that hash to <paramref name="seed"/>, or null if the probe budget ran out.
        ///
        /// Spec 06 section 6.1.1, generalised from 10 to any length 1..10:
        ///   draw floor(L/2) characters at random - any string is a valid preimage of its own lane -
        ///   which fixes O, and therefore fixes E = target - K*O;
        ///   let m = w_ce(E), the number of even-lane strings that reach E;
        ///   skip when m == 0, and otherwise accept with probability m / max(w_ce);
        ///   then descend the even lane picking each character with probability proportional to the
        ///   predecessor's multiplicity.
        ///
        /// Both halves are needed. The weighted descent alone still over-represents low-multiplicity
        /// even lanes by up to a factor of 81, because acceptance would not depend on m.
        ///
        /// Expected probes at L = 10, A59: 486 (2^32/|E_5| = 68.5 to find any preimage, times the
        /// 81/mean-w_5 cost of the rejection).
        /// </summary>
        public static string? InvertFixedLength(
            int seed,
            SeedAlphabet alphabet,
            int length,
            Random? rng = null,
            int maxProbes = 1_000_000)
        {
            if (alphabet is null)
            {
                throw new ArgumentNullException(nameof(alphabet));
            }

            if (length < 1 || length > MaxEmittedLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length), length, $"length must be 1..{MaxEmittedLength}.");
            }

            LaneTables t = LaneTables.For(alphabet);
            int ce = (length + 1) / 2;
            int co = length / 2;
            if (ce > t.MaxLevel + 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length), length, $"Even lane level {ce} exceeds the tables' {t.MaxLevel + 1}.");
            }

            rng ??= Random.Shared;
            uint target = unchecked((uint)seed);
            int bound = t.MaxLaneWeightBound(ce);
            ReadOnlySpan<uint> chars = t.Characters;

            Span<char> even = stackalloc char[MaxEmittedLength];
            Span<char> odd = stackalloc char[MaxEmittedLength];
            Span<char> text = stackalloc char[MaxEmittedLength];

            // With co == 0 the odd lane is the untouched 5381 and there is nothing to randomise, so
            // one attempt settles it. (This is the L == 1 case: GetStableHashCode never reaches
            // num2, spec 06 section 3.1.)
            int probes = co == 0 ? 1 : maxProbes;

            for (int probe = 0; probe < probes; probe++)
            {
                uint o = LaneTables.LaneSeed;
                for (int k = 0; k < co; k++)
                {
                    uint c = chars[rng.Next(chars.Length)];
                    odd[k] = (char)c;
                    o = unchecked(((o << 5) + o) ^ c);
                }

                uint e = unchecked(target - LaneTables.Combiner * o);
                int m = t.LaneWeight(e, ce);
                if (m == 0)
                {
                    continue;
                }

                // Accept with probability m / bound. Any sound upper bound keeps this exactly
                // uniform; a bound that is too large only costs probes.
                if (rng.Next(bound) >= m)
                {
                    continue;
                }

                if (!t.TryDescendWeighted(e, ce, even, rng))
                {
                    continue;
                }

                Interleave(even, odd, text, length);
                return Verified(seed, text.Slice(0, length));
            }

            return null;
        }

        /// <summary>
        /// The shortest text plus a 10-character game-style one, for reporting a search hit.
        /// </summary>
        public static (string Shortest, string GameStyle) Describe(int seed, Random? rng = null)
        {
            string shortest = Invert(seed, SeedAlphabet.Alnum62)
                ?? throw new InvalidOperationException($"No A62 text of length <= 10 for seed {seed}.");
            return (shortest, GenerateGameStyle(seed, rng));
        }

        /// <summary>
        /// text[0] = even[0], text[1] = odd[0], text[2] = even[1], ... - the interleaving
        /// <c>GetStableHashCode</c> undoes when it walks i += 2 through the string.
        /// </summary>
        private static void Interleave(ReadOnlySpan<char> even, ReadOnlySpan<char> odd, Span<char> text, int length)
        {
            for (int i = 0; i < length; i++)
            {
                text[i] = (i & 1) == 0 ? even[i >> 1] : odd[i >> 1];
            }
        }

        /// <summary>
        /// Re-hash before returning. Never hand back an unverified preimage: a wrong seed text looks
        /// exactly like a right one until the world is generated.
        /// </summary>
        private static string Verified(int seed, ReadOnlySpan<char> text)
        {
            int actual = StableHash.Compute(text);
            if (actual != seed)
            {
                throw new InvalidOperationException(
                    $"SeedLab.Seeds produced '{text.ToString()}' for seed {seed}, but it hashes to {actual}. " +
                    "The lane tables or the inverse step are wrong; refusing to return it.");
            }

            return text.ToString();
        }
    }
}
