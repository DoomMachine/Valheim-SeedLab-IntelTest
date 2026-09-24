using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace SeedLab.Seeds
{
    /// <summary>
    /// The prebuilt lane level sets that make <c>GetStableHashCode</c> invertible.
    ///
    /// Spec 06 section 3.1: one lane step is <c>h = (33*h) ^ c</c> from <c>h0 = 5381</c>, and
    /// <c>hash(s) = E + K*O</c> where E is the even lane over s[0], s[2], ... and O the odd lane over
    /// s[1], s[3], ... So a preimage is a meet-in-the-middle over the two lanes, not a brute force.
    ///
    /// <c>E_n</c> is the set of lane values reachable with exactly n characters. It collides heavily -
    /// |E_4| is 2,058,466 of a possible 14,776,336 for A62 (section 3.3) - which is exactly what makes
    /// the tables small enough to hold. Levels 1..4 are stored; membership in E_5 is decided by the
    /// backtracking in <see cref="TryReconstructLane"/> using E_4, so no 512 MB bitmap is needed
    /// (section 6.1).
    ///
    /// Alongside each level this holds w_n, the multiplicity (how many n-character strings reach that
    /// lane value), which the uniform 10-character sampler of section 6.1.1 needs.
    ///
    /// Thread safety: a table set is a pure function of its alphabet and is immutable once built, so
    /// the process-wide cache below carries no seed data and every search worker may share it
    /// (spec 08 section 4.4 lists the lane-4 table as explicitly shareable read-only).
    /// </summary>
    public sealed class LaneTables
    {
        /// <summary>Both lanes start at 5381 (<c>int num = 5381</c>).</summary>
        public const uint LaneSeed = 5381u;

        /// <summary>33^-1 mod 2^32; see <see cref="StableHash.LaneMultiplierInverse"/>.</summary>
        public const uint LaneMultiplierInverse = StableHash.LaneMultiplierInverse;

        /// <summary>The odd lane's multiplier in <c>num + num2 * 1566083941</c>.</summary>
        public const uint Combiner = unchecked((uint)StableHash.LaneCombiner);

        /// <summary>
        /// Levels 1..4 are stored. E_5 would be 64,105,880 entries (A62) = 256 MB, and is never
        /// needed: section 6.1's backtracking decides E_5 membership from E_4.
        /// </summary>
        public const int DefaultMaxLevel = 4;

        // Knuth's 2^32/phi. The filter takes the HIGH bits of the product: lane values have strongly
        // constrained low bits (the section 3.4 invariant fixes bits 0..4 given the characters), so
        // indexing a filter by v's own low bits would clump badly.
        private const uint FilterMix = 2654435761u;

        private static readonly ConcurrentDictionary<string, Lazy<LaneTables>> s_cache =
            new ConcurrentDictionary<string, Lazy<LaneTables>>();

        private readonly uint[] _chars;           // the alphabet, widened for XOR
        private readonly uint[][] _values;        // [n] = E_n, sorted ascending, deduped. [0] unused.
        private readonly ushort[][] _weights;     // [n][i] = w_n(_values[n][i])
        private readonly ulong[][] _filters;      // [n] = membership prefilter bitset over E_n
        private readonly int[] _filterShift;      // [n] = 32 - log2(filter bit count)
        private readonly int[] _maxWeight;        // [n] = max w_n

        private LaneTables(SeedAlphabet alphabet, int maxLevel)
        {
            Alphabet = alphabet;
            MaxLevel = maxLevel;

            _chars = new uint[alphabet.Count];
            for (int i = 0; i < alphabet.Count; i++)
            {
                _chars[i] = alphabet.Characters[i];
            }

            _values = new uint[maxLevel + 1][];
            _weights = new ushort[maxLevel + 1][];
            _filters = new ulong[maxLevel + 1][];
            _filterShift = new int[maxLevel + 1];
            _maxWeight = new int[maxLevel + 1];

            // E_0 = {5381} with w_0 = 1: the lane before any character has been consumed.
            _values[0] = new[] { LaneSeed };
            _weights[0] = new ushort[] { 1 };
            _maxWeight[0] = 1;
            _filters[0] = Array.Empty<ulong>();

            long bytes = 0;
            for (int n = 1; n <= maxLevel; n++)
            {
                BuildLevel(n);
                bytes += (long)_values[n].Length * sizeof(uint)
                       + (long)_weights[n].Length * sizeof(ushort)
                       + (long)_filters[n].Length * sizeof(ulong);
            }

            MemoryBytes = bytes;
        }

        public SeedAlphabet Alphabet { get; }

        /// <summary>Highest n for which E_n is materialised (4).</summary>
        public int MaxLevel { get; }

        /// <summary>Bytes held by the value, weight and filter arrays of levels 1..MaxLevel.</summary>
        public long MemoryBytes { get; }

        /// <summary>
        /// The table set for an alphabet, built on first use and then shared. Construction is
        /// single-threaded and takes well under a second; concurrent callers block on the same
        /// <see cref="Lazy{T}"/> rather than building twice.
        /// </summary>
        public static LaneTables For(SeedAlphabet alphabet)
        {
            if (alphabet is null)
            {
                throw new ArgumentNullException(nameof(alphabet));
            }

            return s_cache.GetOrAdd(
                alphabet.Key,
                _ => new Lazy<LaneTables>(
                    () => new LaneTables(alphabet, DefaultMaxLevel),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        /// <summary>True when the tables for this alphabet have already been built.</summary>
        public static bool IsBuilt(SeedAlphabet alphabet)
            => s_cache.TryGetValue(alphabet.Key, out Lazy<LaneTables>? lazy) && lazy.IsValueCreated;

        /// <summary>|E_n|, the number of distinct lane values reachable with exactly n characters.</summary>
        public int LevelCount(int n) => Level(n).Length;

        /// <summary>E_n, sorted ascending. n must be 0..<see cref="MaxLevel"/>.</summary>
        public ReadOnlySpan<uint> Level(int n)
        {
            if ((uint)n > (uint)MaxLevel)
            {
                throw new ArgumentOutOfRangeException(nameof(n), n, $"Only levels 0..{MaxLevel} are materialised.");
            }

            return _values[n];
        }

        /// <summary>max w_n over E_n. Spec 06 section 3.3 measured this as exactly 3^(n-1).</summary>
        public int MaxLaneWeight(int n)
        {
            if ((uint)n > (uint)MaxLevel)
            {
                throw new ArgumentOutOfRangeException(nameof(n), n, $"Only levels 0..{MaxLevel} are materialised.");
            }

            return _maxWeight[n];
        }

        /// <summary>
        /// A sound upper bound on w_n, for the rejection step of the uniform sampler. Exact for
        /// n &lt;= <see cref="MaxLevel"/>; for n == MaxLevel + 1 it is the alphabet's verified
        /// constant when it has one (81 for A62/A59) and otherwise <c>Count * max w_MaxLevel</c>,
        /// which is sound - w_5(v) is a sum of at most |A| terms each at most max w_4 - but loose,
        /// so the sampler stays uniform and only gets slower.
        /// </summary>
        public int MaxLaneWeightBound(int n)
        {
            if (n <= MaxLevel)
            {
                return MaxLaneWeight(n);
            }

            if (n == MaxLevel + 1)
            {
                return Alphabet.VerifiedMaxLaneWeight5 ?? checked(_chars.Length * _maxWeight[MaxLevel]);
            }

            throw new ArgumentOutOfRangeException(
                nameof(n), n, $"No weight bound is available above level {MaxLevel + 1}.");
        }

        /// <summary>A one-line memory report, for the CLI and for the startup log.</summary>
        public string DescribeMemory()
        {
            var sb = new StringBuilder();
            sb.Append(Alphabet.Name).Append(": ");
            for (int n = 1; n <= MaxLevel; n++)
            {
                if (n > 1)
                {
                    sb.Append(" + ");
                }

                sb.Append("E").Append(n).Append('=').Append(_values[n].Length.ToString("N0"));
            }

            sb.Append(" entries, ").Append((MemoryBytes / 1048576.0).ToString("F2")).Append(" MiB (values ")
              .Append((ValueBytes() / 1048576.0).ToString("F2")).Append(" + weights ")
              .Append((WeightBytes() / 1048576.0).ToString("F2")).Append(" + filters ")
              .Append((FilterBytes() / 1048576.0).ToString("F2")).Append(')');
            return sb.ToString();
        }

        public long ValueBytes()
        {
            long b = 0;
            for (int n = 1; n <= MaxLevel; n++)
            {
                b += (long)_values[n].Length * sizeof(uint);
            }

            return b;
        }

        public long WeightBytes()
        {
            long b = 0;
            for (int n = 1; n <= MaxLevel; n++)
            {
                b += (long)_weights[n].Length * sizeof(ushort);
            }

            return b;
        }

        public long FilterBytes()
        {
            long b = 0;
            for (int n = 1; n <= MaxLevel; n++)
            {
                b += (long)_filters[n].Length * sizeof(ulong);
            }

            return b;
        }

        internal ReadOnlySpan<uint> Characters => _chars;

        /// <summary>Is <paramref name="v"/> in E_n? Only for n &lt;= <see cref="MaxLevel"/>.</summary>
        internal bool ContainsLevel(int n, uint v)
        {
            if (n == 0)
            {
                return v == LaneSeed;
            }

            return IndexOfLevel(n, v) >= 0;
        }

        /// <summary>
        /// Index of <paramref name="v"/> in E_n, or -1. The bitset prefilter rejects roughly 94 % of
        /// misses in one cache line, which is what keeps the shortest-text search - whose cost is
        /// dominated by fully scanning the lengths that FAIL - well under a millisecond.
        /// </summary>
        internal int IndexOfLevel(int n, uint v)
        {
            if (n == 0)
            {
                return v == LaneSeed ? 0 : -1;
            }

            int idx = (int)(unchecked(v * FilterMix) >> _filterShift[n]);
            if ((_filters[n][idx >> 6] & (1UL << (idx & 63))) == 0UL)
            {
                return -1;
            }

            uint[] a = _values[n];
            int lo = 0;
            int hi = a.Length - 1;
            while (lo <= hi)
            {
                int mid = (int)(((uint)lo + (uint)hi) >> 1);
                uint m = a[mid];
                if (m == v)
                {
                    return mid;
                }

                if (m < v)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return -1;
        }

        /// <summary>
        /// w_n(v): how many n-character strings over this alphabet end the lane at v.
        /// Defined for n &lt;= MaxLevel + 1; at MaxLevel + 1 it is computed on the fly as
        /// <c>sum over c of w_MaxLevel((v ^ c) * 33^-1)</c> (spec 06 section 9: w_5 is never stored).
        /// </summary>
        public int LaneWeight(uint v, int n)
        {
            if (n == 0)
            {
                return v == LaneSeed ? 1 : 0;
            }

            if (n <= MaxLevel)
            {
                int i = IndexOfLevel(n, v);
                return i < 0 ? 0 : _weights[n][i];
            }

            if (n == MaxLevel + 1)
            {
                int m = 0;
                ushort[] w = _weights[MaxLevel];
                for (int j = 0; j < _chars.Length; j++)
                {
                    uint h = unchecked((v ^ _chars[j]) * LaneMultiplierInverse);
                    int i = IndexOfLevel(MaxLevel, h);
                    if (i >= 0)
                    {
                        m += w[i];
                    }
                }

                return m;
            }

            throw new ArgumentOutOfRangeException(
                nameof(n), n, $"Lane weights are only available up to level {MaxLevel + 1}.");
        }

        /// <summary>
        /// Walk a lane value back to 5381, writing the n characters that produce it into
        /// <paramref name="dest"/> (dest[k] is the (k+1)-th character of the lane).
        ///
        /// Spec 06 section 6.1. The lane step is invertible given the character:
        /// <c>v = (33*h) ^ c</c> implies <c>h = (v ^ c) * 1041204193</c>, so at each level we try
        /// every character and keep the branch whose predecessor is a member of E_{n-1}. Below
        /// <see cref="MaxLevel"/> membership is decided by table lookup, which means a hit can never
        /// fail later and the descent never actually backtracks; above it, the loop itself IS the
        /// membership test for E_{MaxLevel+1}.
        ///
        /// GREEDY: it returns the first branch, scanning the alphabet in order, so the characters it
        /// picks are NOT uniformly distributed (spec 06 section 6.1.1 measured chi-squared up to
        /// 130,232 against df 58 at even positions). That is fine for "the shortest text that works",
        /// which is not claimed to look game-generated; anything presented as a game-style seed must
        /// go through <see cref="TryDescendWeighted"/> instead.
        /// </summary>
        internal bool TryReconstructLane(uint v, int n, Span<char> dest)
        {
            if (n == 0)
            {
                return v == LaneSeed;
            }

            if (n <= MaxLevel && !ContainsLevel(n, v))
            {
                return false;
            }

            for (int j = 0; j < _chars.Length; j++)
            {
                uint h = unchecked((v ^ _chars[j]) * LaneMultiplierInverse);
                if (n - 1 <= MaxLevel && !ContainsLevel(n - 1, h))
                {
                    continue;
                }

                if (TryReconstructLane(h, n - 1, dest))
                {
                    dest[n - 1] = (char)_chars[j];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Like <see cref="TryReconstructLane"/>, but at each level picks the character with
        /// probability proportional to w_{n-1}(predecessor), so the n characters it writes are a
        /// UNIFORM draw from the set of n-character strings whose lane ends at v.
        ///
        /// Spec 06 section 6.1.1. Together with the caller's w_n(v)/max-w_n rejection this makes the
        /// whole 10-character text a uniform draw from the target's preimage set, which is exactly
        /// the conditional distribution of a <c>World.GenerateSeed</c> output given its hash.
        /// The weighted descent ALONE is not enough - it still leaves chi-squared around 8,000-12,000,
        /// because acceptance is biased towards low-multiplicity even lanes by up to a factor of 81.
        /// </summary>
        internal bool TryDescendWeighted(uint v, int n, Span<char> dest, Random rng)
        {
            for (int level = n; level >= 1; level--)
            {
                int total = LaneWeight(v, level);
                if (total <= 0)
                {
                    return false;
                }

                int r = rng.Next(total);
                bool picked = false;
                for (int j = 0; j < _chars.Length; j++)
                {
                    uint h = unchecked((v ^ _chars[j]) * LaneMultiplierInverse);
                    int w = LaneWeight(h, level - 1);
                    if (w == 0)
                    {
                        continue;
                    }

                    if (r < w)
                    {
                        dest[level - 1] = (char)_chars[j];
                        v = h;
                        picked = true;
                        break;
                    }

                    r -= w;
                }

                if (!picked)
                {
                    // Only reachable if LaneWeight disagreed with the per-character sum, i.e. a bug.
                    return false;
                }
            }

            return v == LaneSeed;
        }

        /// <summary>
        /// Build E_n and w_n from E_{n-1}.
        ///
        /// Every (predecessor, character) pair is packed into one ulong as
        /// <c>(value &lt;&lt; 32) | weightOfPredecessor</c> and the whole array is sorted once;
        /// sorting by the packed word sorts by value, so a single run-length pass then gives the
        /// deduped values and their summed multiplicities. That avoids 4.1 M binary searches into
        /// the level being built (level 4 for A62 is 66,014 x 62 = 4,092,868 candidates).
        /// </summary>
        private void BuildLevel(int n)
        {
            uint[] prev = _values[n - 1];
            ushort[] prevW = _weights[n - 1];
            int alpha = _chars.Length;

            long total = (long)prev.Length * alpha;
            if (total > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Lane level {n} for alphabet '{Alphabet.Name}' would need {total:N0} candidates.");
            }

            ulong[] buf = new ulong[(int)total];
            int k = 0;
            for (int i = 0; i < prev.Length; i++)
            {
                // (h << 5) + h is 33 * h mod 2^32 - the lane step of GetStableHashCode.
                uint step = unchecked((prev[i] << 5) + prev[i]);
                ulong w = prevW[i];
                for (int j = 0; j < alpha; j++)
                {
                    buf[k++] = ((ulong)(step ^ _chars[j]) << 32) | w;
                }
            }

            Array.Sort(buf);

            uint[] values = new uint[buf.Length];
            ushort[] weights = new ushort[buf.Length];
            int distinct = 0;
            int maxW = 0;
            int p = 0;
            while (p < buf.Length)
            {
                uint v = (uint)(buf[p] >> 32);
                long sum = 0;
                int q = p;
                while (q < buf.Length && (uint)(buf[q] >> 32) == v)
                {
                    sum += (uint)buf[q];
                    q++;
                }

                if (sum > ushort.MaxValue)
                {
                    // Cannot happen for A62/A59: max w_4 = 27 (spec 06 section 3.3). A much larger
                    // alphabet could overflow, and a silently truncated weight would break the
                    // uniform sampler in a way no correctness test would catch, so refuse loudly.
                    throw new InvalidOperationException(
                        $"Lane multiplicity {sum:N0} at level {n} for alphabet '{Alphabet.Name}' " +
                        "does not fit in a ushort; widen LaneTables._weights before using this alphabet.");
                }

                values[distinct] = v;
                weights[distinct] = (ushort)sum;
                if (sum > maxW)
                {
                    maxW = (int)sum;
                }

                distinct++;
                p = q;
            }

            Array.Resize(ref values, distinct);
            Array.Resize(ref weights, distinct);
            _values[n] = values;
            _weights[n] = weights;
            _maxWeight[n] = maxW;

            // Roughly 16 bits per entry keeps the false-positive rate near 6 % while staying small:
            // 4 MiB for E_4 (A62), 128 KiB for E_3.
            int bitsLog = 10;
            while ((1L << bitsLog) < 16L * distinct && bitsLog < 26)
            {
                bitsLog++;
            }

            _filterShift[n] = 32 - bitsLog;
            ulong[] filter = new ulong[1 << (bitsLog - 6)];
            for (int i = 0; i < values.Length; i++)
            {
                int idx = (int)(unchecked(values[i] * FilterMix) >> _filterShift[n]);
                filter[idx >> 6] |= 1UL << (idx & 63);
            }

            _filters[n] = filter;
        }
    }
}
