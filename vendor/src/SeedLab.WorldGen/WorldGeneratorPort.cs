using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using SeedLab.WorldGen.Noise;
using SeedLab.WorldGen.Unity;

namespace SeedLab.WorldGen
{
    /// <summary>
    /// Heightmap.BiomeArea (decomp/Heightmap.cs 47-53). It is a [Flags] enum and it really does define
    /// Everything = Edge | Median = 3: GetBiomeArea only ever RETURNS Edge or Median, but the game's one
    /// consumer tests it as a bitmask - ZoneSystem.GenerateLocationsTimeSliced does
    /// <c>if ((location.m_biomeArea &amp; biomeArea) == 0) continue;</c> (decomp/ZoneSystem.cs 1934) -
    /// and most Location prefabs ship m_biomeArea = Everything. Dropping the member would make a dumped
    /// 3 print as "3" and lose its name on a round-trip.
    /// </summary>
    [Flags]
    public enum BiomeArea
    {
        Edge = 1,
        Median = 2,
        Everything = Edge | Median,
    }

    /// <summary>
    /// Vector2s (assembly_utils) - two Int16. Subtraction wraps through conv.i2, which never matters
    /// inside a +/-10500 world. Used as the key of the biome-area caches.
    /// </summary>
    public readonly struct Vec2s : IEquatable<Vec2s>
    {
        public readonly short x;
        public readonly short y;
        public Vec2s(short x, short y) { this.x = x; this.y = y; }
        public Vec2s(int x, int y) { this.x = (short)x; this.y = (short)y; }
        public static Vec2s operator -(Vec2s a, Vec2s b) => new Vec2s((short)(a.x - b.x), (short)(a.y - b.y));
        public bool Equals(Vec2s other) => x == other.x && y == other.y;
        public override bool Equals(object? obj) => obj is Vec2s v && Equals(v);
        public override int GetHashCode() => (x << 16) ^ (ushort)y;
        public override string ToString() => "(" + x + ", " + y + ")";
    }

    /// <summary>
    /// An offline port of Valheim 1.0.15's <c>WorldGenerator</c> (assembly_valheim, sha256
    /// 59f53fb5...33adb1). Built from decomp/WorldGenerator.cs plus the IL evidence collected in
    /// specs/01-worldgen-core.md; every non-obvious method below cites its source.
    ///
    /// <para><b>Numerics.</b> The shipped IL does arithmetic in double and truncates to float at the end
    /// of each source statement, with float literals widened to double. This port copies the casts and
    /// the operation grouping exactly. Do not reorder an expression for readability: several sites
    /// (Meadows vs Plains, the two Length functions, the two LerpStep functions, MathfLikeSmoothStep's
    /// float-rounded return) differ from the obvious implementation by one rounding, and that rounding
    /// is what the game shipped.</para>
    ///
    /// <para><b>Bugs are reproduced on purpose</b> - the world data depends on them. See
    /// GetBiomeArea (the Vector3 overload tests (64,0,0) twice), Range(0, count) in FindRandomRiverEnd,
    /// the stale single-entry river cache, and the discarded second PlaceStreams list.</para>
    ///
    /// <para><b>Threading.</b> One instance is NOT safe for concurrent use. The per-handle mutable state
    /// is, in full: the single-entry river cache, the two biome-area dictionaries, the deferred
    /// pregeneration flag, and - since the O2 optimisation - the one-entry <c>GetBaseHeight</c> memo.
    /// <b>That last one matters to anyone reading this paragraph for the second time:</b> before O2,
    /// <c>GetBaseHeight</c> and <c>GetBiome</c> at their default arguments wrote nothing, so sharing a
    /// handle between threads for biome-only work happened to be harmless. It is not any more - two
    /// threads can interleave the four memo writes and leave a key from one paired with a value from the
    /// other, which returns a wrong height rather than throwing. Everything expensive - the offsets, the
    /// river point grid, the lake/river/stream lists - is still immutable after construction, so
    /// <see cref="Fork"/> is a cheap second handle onto the same world and is the answer; every
    /// parallel consumer in this repository already forks or constructs per worker. There is no static
    /// mutable state carrying seed data, so two worlds can never see each other.</para>
    /// </summary>
    public sealed class WorldGeneratorPort
    {
        // ---------------------------------------------------------------------------------------------
        // Types
        // ---------------------------------------------------------------------------------------------

        /// <summary>WorldGenerator.River (decomp 15-30).</summary>
        public sealed class River
        {
            public Vec2 p0;
            public Vec2 p1;
            public Vec2 center;
            public float widthMin;
            public float widthMax;
            public float curveWidth;
            public float curveWavelength;
        }

        /// <summary>WorldGenerator.RiverPoint (decomp 32-46). w2 is the float-rounded square of w.</summary>
        public readonly struct RiverPoint
        {
            public readonly Vec2 p;
            public readonly float w;
            public readonly float w2;

            public RiverPoint(Vec2 pp, float pw)
            {
                p = pp;
                w = pw;
                w2 = (float)((double)pw * (double)pw);
            }
        }

        /// <summary>WorldGenerator.RiverAdd (decomp 8-13). The numeric values matter: All=0.</summary>
        private enum RiverAdd
        {
            All = 0,
            SkipDeepNorth = 1,
            OnlyDeepNorth = 2,
        }

        // ---------------------------------------------------------------------------------------------
        // Constants (WorldGenerator's own fields and consts, decomp 48-190)
        // ---------------------------------------------------------------------------------------------

        /// <summary>public static readonly float in the game - a field load, not a const inline.</summary>
        public const float AshlandsMinDistance = 12000f;

        /// <summary>public static readonly float ashlandsYOffset = -4000f.</summary>
        public const float AshlandsYOffset = -4000f;

        public const float WorldSize = 10000f;
        public const float WaterEdge = 10500f;

        private static readonly Vec2s[] s_biomeAreaOffsetsInt =
        {
            new Vec2s(-64, -64), new Vec2s(64, -64), new Vec2s(64, 64), new Vec2s(-64, 64),
            new Vec2s(-64, 0), new Vec2s(64, 0), new Vec2s(0, -64), new Vec2s(0, 64),
        };

        // ---------------------------------------------------------------------------------------------
        // Immutable world state (safe to share between forks)
        // ---------------------------------------------------------------------------------------------

        private readonly int m_seed;
        private readonly int m_version;
        private readonly bool m_menu;

        private readonly float m_offset0;
        private readonly float m_offset1;
        private readonly float m_offset2;
        private readonly float m_offset3;
        private readonly float m_offset4;
        private readonly int m_riverSeed;
        private readonly int m_streamSeed;

        // VersionSetup-controlled (WorldGenerator.VersionSetup, decomp 245-257)
        private readonly float m_minMountainDistance;
        private readonly float m_minDarklandNoise;
        private readonly float m_maxMarshDistance;

        // Assigned once, by FindLakes, from pregeneration; never touched afterwards.
        private List<Vec2>? m_lakes;
        private List<River> m_rivers;
        private List<River> m_streams;
        private Dictionary<Vec2i, RiverPoint[]> m_riverPoints;

        // Read-only views built once and shared by every fork, so the public accessors cannot be cast
        // back to the live collections (see the accessor docs). They wrap, they do not copy.
        private ReadOnlyCollection<River> m_riversView;
        private ReadOnlyCollection<River> m_streamsView;
        private ReadOnlyDictionary<Vec2i, RiverPoint[]> m_riverPointsView;
        private ReadOnlyCollection<Vec2>? m_lakesView;

        // ---- deferred pregeneration ------------------------------------------------------------------
        // Pregeneration (lakes -> rivers -> streams) is ~99.5 % of the cost of building a world and is
        // read by EXACTLY ONE thing: the river/lake/stream data behind GetHeight's AddRivers and the
        // four public accessors. GetBaseHeight and GetBiome at its default arguments never consult it
        // (WorldGenerator.GetBiome takes GetBaseHeight, not GetHeight, unless waterAlwaysOcean is set),
        // so a biome-only consumer pays 0.2 s per seed for data it never reads.
        //
        // deferPregeneration: true keeps the RNG state that pregeneration would have consumed and runs
        // the identical four calls, from the identical state, the first time anything asks for river,
        // lake or stream data. The draws that precede it (the seven offsets) have already happened, and
        // pregeneration is the last thing the constructor does, so NOTHING observes a different RNG
        // stream. It is a pure deferral, not a different world - proven by the eager/deferred
        // equivalence check in the acceptance suite.
        //
        // It is per-handle mutable state, so a deferred generator is single-threaded until it has been
        // pregenerated, exactly like the river cache below. Fork() forces pregeneration for that reason.
        private bool m_pregenPending;
        private (int, int, int, int) m_pregenRandomState;

        /// <summary>Seed 0 always - see FastNoisePort. Shared because it carries no seed data.</summary>
        private readonly FastNoisePort m_noiseGen = FastNoisePort.WorldGen;

        // ---------------------------------------------------------------------------------------------
        // Mutable per-handle state (never shared; this is what makes an instance non-thread-safe)
        // ---------------------------------------------------------------------------------------------

        private RiverPoint[]? m_cachedRiverPoints;
        private Vec2i m_cachedRiverGrid = new Vec2i(-999999, -999999);

        // O2: the one-entry GetBaseHeight memo. Keyed on the raw bit patterns of the two coordinates,
        // so a hit means the arguments were identical in every bit. GetBaseHeight is a pure function of
        // (wx, wy) and the immutable offsets, so the memo can never return a value the call would not
        // have produced, and it never needs invalidating. Per-handle mutable state exactly like the
        // river cache above - which is what makes a handle single-threaded, as it already was.
        private int m_bhKeyX;
        private int m_bhKeyY;
        private float m_bhValue;
        private bool m_bhValid;

        // The game's equivalents are private STATIC dictionaries cleared in the ctor. Making them
        // per-instance is the one deliberate structural change in this file: it is what lets two search
        // workers run different seeds at once, and it cannot change any result, because the game clears
        // them for every new WorldGenerator anyway (decomp 50-52, 208-209).
        private Dictionary<Vec2s, BiomeArea>? m_cachedBiomeAreas;
        private Dictionary<Vec2s, Biome>? m_cachedBiomes;

        // ---------------------------------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldGenerator..ctor (decomp 205-235, IL_0000-IL_01c3). Generation reads exactly three inputs:
        /// the int seed, the world-gen version and the menu flag. The seed TEXT is never used.
        ///
        /// The seven Random draws happen in this order - off0, off1, off2, off3, riverSeed, streamSeed,
        /// off4 - from a stream freshly seeded with InitState(seed). off4 being last is not a typo; any
        /// extra draw inserted anywhere before it shifts everything after it.
        /// </summary>
        /// <param name="seed">World.m_seed, i.e. seedText.GetStableHashCode() (0 for an empty name).</param>
        /// <param name="worldGenVersion">World.m_worldGenVersion; 2 for every world created by 1.0.15.</param>
        /// <param name="menu">World.m_menu - skips pregeneration and switches to the menu terrain.</param>
        /// <param name="deferPregeneration">
        /// Run the lake/river/stream pregeneration on first use instead of here. The world is the same
        /// world either way - the same four calls run from the same RNG state - but a consumer that only
        /// ever asks for biomes or base heights never pays for it. Makes the handle single-threaded
        /// until something triggers pregeneration; <see cref="Fork"/> triggers it deliberately.
        /// </param>
        public WorldGeneratorPort(int seed, int worldGenVersion = 2, bool menu = false,
                                  bool deferPregeneration = false)
        {
            m_seed = seed;
            m_version = worldGenVersion;
            m_menu = menu;

            // --- VersionSetup(version) (decomp 245-257). The v2 values are the field initialisers.
            float minMountainDistance = 1000f;
            float minDarklandNoise = 0.4f;
            float maxMarshDistance = 6000f;
            if (m_version <= 0) minMountainDistance = 1500f;
            if (m_version <= 1) { minDarklandNoise = 0.5f; maxMarshDistance = 8000f; }
            m_minMountainDistance = minMountainDistance;
            m_minDarklandNoise = minDarklandNoise;
            m_maxMarshDistance = maxMarshDistance;

            m_rivers = new List<River>();
            m_streams = new List<River>();
            m_riverPoints = new Dictionary<Vec2i, RiverPoint[]>();

            // The game saves UnityEngine.Random.state here and restores it at the end. That only matters
            // inside the game process; this port owns its generator, so the save/restore is a no-op.
            UnityRandom rnd = new UnityRandom();
            rnd.InitState(seed);

            // m_noiseGen is constructed from the FIRST world's seed and then SetSeed(0) runs on every
            // ctor, so the cellular/simplex noise is identical in every world (01-worldgen-core.md 1.4).

            m_offset0 = (float)rnd.Range(-10000, 10000);
            m_offset1 = (float)rnd.Range(-10000, 10000);
            m_offset2 = (float)rnd.Range(-10000, 10000);
            m_offset3 = (float)rnd.Range(-10000, 10000);
            m_riverSeed = rnd.Range(int.MinValue, int.MaxValue);
            m_streamSeed = rnd.Range(int.MinValue, int.MaxValue);
            m_offset4 = (float)rnd.Range(-10000, 10000);

            if (!m_menu)
            {
                if (deferPregeneration)
                {
                    // The state pregeneration would have started from, kept verbatim.
                    m_pregenPending = true;
                    m_pregenRandomState = rnd.GetState();
                }
                else
                {
                    Pregenerate(rnd);
                }
            }

            // Built after pregeneration, when the three collections have their final contents. A
            // deferred handle builds them over the empty collections here and rebuilds them in
            // Pregenerate; no accessor can observe the empty pair, because every one of them ensures
            // pregeneration first.
            BuildViews();
        }

        /// <summary>WorldGenerator.Pregenerate() (decomp 259-265), in its own method so it can be deferred.</summary>
        private void Pregenerate(UnityRandom rnd)
        {
            FindLakes();
            m_rivers = PlaceRivers(rnd);
            m_streams = PlaceStreams(rnd, isDN: false);
            PlaceStreams(rnd, isDN: true);   // return value DISCARDED - GetStreams() returns pass 1
            // O6 (skip this second pass for a radius-bounded query) was tried and REJECTED - it is not
            // sound under a radius precondition. The radius part is fine: a Deep North stream's start
            // point is more than 7,900 m from the centre, its rendered points reach at most ~201 m from
            // it, and a river point only influences a height within its own 20 m width, so nothing
            // inside ~7,679 m can see one (measured: 1,777,728 heights inside 7,600 m, 0 differ). What
            // breaks it is the single-entry river cache this port deliberately does NOT invalidate
            // after RenderRivers (see Fork's remarks): the cell it is left pointing at when
            // pregeneration ends is wherever the LAST stream probe landed, and that probe is in this
            // pass. Skip the pass and the cache ends up on a different cell holding a different array,
            // so the first river query a fresh handle makes in that cell answers differently - measured
            // at 11 divergences inside 7,600 m over 30 seeds, as far in as 2,715 m, with weights of
            // 0.89 against 0 rather than last-bit noise. No radius bound can cover that, because the
            // cell is wherever a random draw put it.
        }

        /// <summary>
        /// Runs the deferred pregeneration, once, from the RNG state the constructor left it at. A no-op
        /// for an eager handle, a menu world, or a handle that has already been pregenerated.
        /// </summary>
        private void EnsurePregenerated()
        {
            if (!m_pregenPending) return;
            m_pregenPending = false;
            UnityRandom rnd = new UnityRandom();
            rnd.SetState(m_pregenRandomState);
            Pregenerate(rnd);
            BuildViews();
        }

        [MemberNotNull(nameof(m_riversView), nameof(m_streamsView), nameof(m_riverPointsView))]
        private void BuildViews()
        {
            m_riversView = new ReadOnlyCollection<River>(m_rivers);
            m_streamsView = new ReadOnlyCollection<River>(m_streams);
            m_riverPointsView = new ReadOnlyDictionary<Vec2i, RiverPoint[]>(m_riverPoints);
            m_lakesView = m_lakes == null ? null : new ReadOnlyCollection<Vec2>(m_lakes);
        }

        /// <summary>
        /// True while this handle was built with <c>deferPregeneration</c> and has not yet needed the
        /// lake/river/stream data. Diagnostics only - it says nothing about the world, only about how
        /// much of it has been computed.
        /// </summary>
        public bool PregenerationPending => m_pregenPending;

        /// <summary>Runs the deferred pregeneration now, so a later query cannot pay for it unexpectedly.</summary>
        public void ForcePregeneration() => EnsurePregenerated();

        /// <summary>Private copy ctor used by <see cref="Fork"/>: shares everything immutable.</summary>
        private WorldGeneratorPort(WorldGeneratorPort other, bool inheritRiverCache)
        {
            m_seed = other.m_seed;
            m_version = other.m_version;
            m_menu = other.m_menu;
            m_offset0 = other.m_offset0;
            m_offset1 = other.m_offset1;
            m_offset2 = other.m_offset2;
            m_offset3 = other.m_offset3;
            m_offset4 = other.m_offset4;
            m_riverSeed = other.m_riverSeed;
            m_streamSeed = other.m_streamSeed;
            m_minMountainDistance = other.m_minMountainDistance;
            m_minDarklandNoise = other.m_minDarklandNoise;
            m_maxMarshDistance = other.m_maxMarshDistance;
            m_lakes = other.m_lakes;
            m_rivers = other.m_rivers;
            m_streams = other.m_streams;
            m_riverPoints = other.m_riverPoints;
            m_riversView = other.m_riversView;
            m_streamsView = other.m_streamsView;
            m_riverPointsView = other.m_riverPointsView;
            m_lakesView = other.m_lakesView;
            // Fork() pregenerates the parent first, so a fork is never itself pending: it shares the
            // finished, immutable collections exactly as it always has.
            m_pregenPending = false;
            if (inheritRiverCache)
            {
                // Copies the parent's river cache AS IT STANDS RIGHT NOW - which is whatever the parent
                // last looked at, not necessarily the end-of-pregeneration state. The two coincide only
                // if Fork is called before the parent answers any query; call it straight after
                // construction if that is what you want (01-worldgen-core.md 4.7).
                m_cachedRiverGrid = other.m_cachedRiverGrid;
                m_cachedRiverPoints = other.m_cachedRiverPoints;
            }
            // Otherwise the cache starts at (-999999, -999999), which can never false-hit.
        }

        /// <summary>
        /// A second handle onto the same generated world, with its own river cache, for another thread.
        /// Costs nothing: no RNG, no pregeneration, no copying of the river grid.
        /// </summary>
        /// <param name="inheritRiverCache">
        /// The game never invalidates the single-entry river cache after RenderRivers, so the very first
        /// query made on a freshly pregenerated world can read a stale array - a ~1e-5, single-64 m-cell
        /// effect per world (01-worldgen-core.md 4.7). Pass true to copy the parent's cache entry as it
        /// stands at the moment of the call; the default starts the fork cold, which is the right choice
        /// for query work that is not trying to replay the game's exact first query.
        ///
        /// <para><b>This is a deliberate divergence, and it is a real fork/no-fork answer difference.</b>
        /// A forked worker and an unforked one can disagree about the height of one 64 m cell per world.
        /// Measured on both ground-truth worlds it never fires (0 of 23,380 and 0 of 23,262 cells), but
        /// over a whole-seed-space sweep the spec's bound is ~1e-5 per world. Callers that compare
        /// results across workers must therefore pick ONE mode and use it everywhere rather than mixing
        /// <c>gen</c> and <c>gen.Fork()</c> for the same question.</para>
        /// </param>
        public WorldGeneratorPort Fork(bool inheritRiverCache = false)
        {
            // A fork shares the parent's finished collections, so the parent must have them. Deferring
            // past this point would mean two threads racing to pregenerate the same world.
            EnsurePregenerated();
            return new WorldGeneratorPort(this, inheritRiverCache);
        }

        // ---------------------------------------------------------------------------------------------
        // Public accessors
        // ---------------------------------------------------------------------------------------------

        /// <summary>WorldGenerator.GetSeed() - World.m_seed.</summary>
        public int GetSeed() => m_seed;

        public int WorldGenVersion => m_version;
        public bool IsMenu => m_menu;

        public float Offset0 => m_offset0;
        public float Offset1 => m_offset1;
        public float Offset2 => m_offset2;
        public float Offset3 => m_offset3;
        public float Offset4 => m_offset4;
        public int RiverSeed => m_riverSeed;
        public int StreamSeed => m_streamSeed;

        public float MinMountainDistance => m_minMountainDistance;
        public float MinDarklandNoise => m_minDarklandNoise;
        public float MaxMarshDistance => m_maxMarshDistance;

        /// <summary>
        /// WorldGenerator.GetLakes() - null in a menu world.
        /// Returns a read-only VIEW, not the live List, so a caller cannot cast it back and mutate the
        /// world that every fork on this chain shares (Vec2 is a readonly struct, so the elements are
        /// safe too).
        /// </summary>
        public IReadOnlyList<Vec2>? GetLakes()
        {
            EnsurePregenerated();
            return m_lakesView;
        }

        /// <summary>
        /// WorldGenerator.GetRivers() - a read-only view of the list.
        /// <para><b>The River objects themselves are still shared and still have public mutable
        /// fields.</b> Treat them as immutable: writing to one changes the world seen by every other
        /// thread holding a fork of this generator. Same for the RiverPoint[] arrays from
        /// <see cref="GetRiverPoints"/>.</para>
        /// </summary>
        public IReadOnlyList<River> GetRivers()
        {
            EnsurePregenerated();
            return m_riversView;
        }

        /// <summary>
        /// WorldGenerator.GetStreams(). Reproduces the game's quirk: this is the PASS 1 list, which still
        /// contains the Deep North streams that were never rendered and omits the Deep North streams that
        /// were (pass 2's list is discarded by Pregenerate, IL_0026 'pop').
        /// </summary>
        public IReadOnlyList<River> GetStreams()
        {
            EnsurePregenerated();
            return m_streamsView;
        }

        /// <summary>
        /// The river-point grid, keyed by 64 m cell, as a read-only view. Immutable after construction.
        /// <para>The RiverPoint[] values are handed out by reference and are shared by every fork - the
        /// grid is far too large to copy per call. RiverPoint is a readonly struct, but the array SLOTS
        /// are writable; do not write to them. Use <see cref="CopyRiverPoints"/> if you need an array
        /// you own.</para>
        /// </summary>
        public IReadOnlyDictionary<Vec2i, RiverPoint[]> GetRiverPoints()
        {
            EnsurePregenerated();
            return m_riverPointsView;
        }

        /// <summary>
        /// A private copy of one cell's river points, for callers that need to sort or otherwise mutate
        /// them. Returns null when the cell holds none.
        /// </summary>
        public RiverPoint[]? CopyRiverPoints(Vec2i cell)
        {
            EnsurePregenerated();
            return m_riverPoints.TryGetValue(cell, out RiverPoint[]? a) ? (RiverPoint[])a.Clone() : null;
        }

        /// <summary>WorldGenerator.GetHeightMultiplier() - the literal 200f.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetHeightMultiplier() => 200f;

        // ---------------------------------------------------------------------------------------------
        // Perlin shorthand
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// DUtils.PerlinNoise(double, double) == Mathf.PerlinNoise((float)x, (float)y). Named PN here only
        /// to keep the transcribed statements one line long, exactly as the decompiler printed them.
        /// The two casts inside are load-bearing: every coordinate*frequency product is re-quantised to
        /// float before the noise sees it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float PN(double x, double y) => DUtils.PerlinNoise(x, y);

        // ---------------------------------------------------------------------------------------------
        // Lakes
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldGenerator.FindLakes (decomp 282-298). No RNG. The loops step by 128 m over
        /// [-10000, 10000], i.e. -10000, -9872, ..., 9968 = 157 x 157 = 24 649 candidates.
        /// The radius test uses Vector2.magnitude (float sum) while GetBaseHeight internally uses
        /// DUtils.Length (double sum) - both appear here at different precision, on purpose.
        /// "Lakes" are just low basins; open sea counts.
        /// </summary>
        private void FindLakes()
        {
            // Presized: the loops test exactly 157 x 157 = 24,649 candidates and roughly a third pass,
            // so the default List growth would reallocate and copy a dozen times for nothing. Capacity
            // is not observable - it changes no value and no order.
            List<Vec2> list = new List<Vec2>(1 << 13);
            for (float z = -10000f; z <= 10000f; z = (float)((double)z + 128.0))
            {
                for (float x = -10000f; x <= 10000f; x = (float)((double)x + 128.0))
                {
                    if (!(new Vec2(x, z).Magnitude > 10000f) && GetBaseHeight(x, z, menuTerrain: false) < 0.05f)
                    {
                        list.Add(new Vec2(x, z));
                    }
                }
            }
            m_lakes = MergePoints(list, 800f);
        }

        /// <summary>
        /// WorldGenerator.MergePoints (decomp 300-321). The running value is a MIDPOINT, not a centroid,
        /// and the inner removal is a SWAP-remove (points[i] = points[last]; RemoveAt(last)), which
        /// reorders the remainder. Both are load-bearing; a "cleaner" merge produces different lakes.
        /// </summary>
        private List<Vec2> MergePoints(List<Vec2> points, float range)
            => WorldGenTuning.LegacyMergePoints ? MergePointsLegacy(points, range)
                                                : MergePointsBuckets(points, range);

        /// <summary>
        /// O4. <see cref="MergePointsLegacy"/> with a uniform bucket grid of cell side
        /// <paramref name="range"/> in front of <see cref="FindClosest"/>, so each search looks at a
        /// 3x3 neighbourhood (about 350 candidates) instead of the whole live list (24,649 at the
        /// start). It does ZERO noise work, which is why it is worth doing separately from the Perlin
        /// optimisations: it is 19 % of pre-generation and none of that 19 % is arithmetic we can make
        /// cheaper.
        ///
        /// <para><b>This one is NOT safe by construction, and the argument matters.</b> Three things
        /// have to hold, and all three are checked against the legacy path by exhaustive comparison over
        /// whole worlds (see <see cref="WorldGenTuning.LegacyMergePoints"/>):</para>
        /// <list type="number">
        /// <item><b>The candidate set is the same.</b> <see cref="FindClosest"/> only ever accepts a
        /// point with <c>Vec2.Distance(v, q) &lt; range</c>. Cells are <c>range</c> wide, so
        /// <c>|q.x - v.x| &lt; range</c> forces q's column to be v's column or one either side, and the
        /// same for the row. Every point the full scan could accept is therefore inside the 3x3
        /// neighbourhood, and every point outside it is one the full scan would have rejected. Cells are
        /// derived from a point's VALUE, and a stored point's value never changes, so its cell never
        /// changes either - only <c>v</c> moves, and v is re-located on every query.</item>
        /// <item><b>The winner is the same.</b> The game's scan runs the live list in index order and
        /// keeps a candidate only when it is <i>strictly</i> closer than the best so far, so it returns
        /// the minimum by distance and, on an exact tie, the LOWEST live index. This code visits the
        /// same candidates in a different order, so it compares <c>(distance, live index)</c>
        /// lexicographically and gets the same answer. The tie-break is load-bearing rather than
        /// theoretical: lake candidates sit on a 128 m lattice, so exactly equal distances are common.
        /// The skip test is the same epsilon-based <c>Vec2.operator==</c>, not exact equality.</item>
        /// <item><b>The live list is the same list.</b> The game pops index 0 with
        /// <c>RemoveAt(0)</c> - which shifts everything down - and removes a merged point with a
        /// swap-remove that moves the tail element into the hole. Both reorder the remainder, and both
        /// are reproduced exactly: the live set is the slot range <c>[head, count)</c>, <c>RemoveAt(0)</c>
        /// is <c>head++</c> (which preserves relative order, as the shift does), and the swap-remove is
        /// spelled out below. Live index == slot - head, so "lowest live index" is "lowest slot".</item>
        /// </list>
        ///
        /// <para>Dead entries are left in the buckets rather than compacted: a bucket can hold at most
        /// the 6.25 x 6.25 = 39 lattice candidates that started in it, so the 3x3 scan is bounded by
        /// ~350 whatever happens, and removing entries would cost more than skipping them.</para>
        /// </summary>
        private static List<Vec2> MergePointsBuckets(List<Vec2> points, float range)
        {
            List<Vec2> result = new List<Vec2>();
            int n = points.Count;
            if (n == 0) return result;

            Vec2[] arr = new Vec2[n];
            points.CopyTo(arr);

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vec2 q = arr[i];
                if (q.x < minX) minX = q.x;
                if (q.x > maxX) maxX = q.x;
                if (q.y < minY) minY = q.y;
                if (q.y > maxY) maxY = q.y;
            }
            int gw = (int)((maxX - minX) / range) + 1;
            int gh = (int)((maxY - minY) / range) + 1;

            // Buckets as head/next singly linked lists over identities - one allocation each, no
            // List<int> per cell. -1 terminates.
            int[] cellHead = new int[gw * gh];
            int[] next = new int[n];
            for (int i = 0; i < cellHead.Length; i++) cellHead[i] = -1;
            for (int i = n - 1; i >= 0; i--)          // descending, so each cell's list comes out ascending
            {
                int c = Cell(arr[i], minX, minY, range, gw, gh);
                next[i] = cellHead[c];
                cellHead[c] = i;
            }

            int[] id = new int[n];                    // slot -> identity
            int[] pos = new int[n];                   // identity -> slot
            for (int i = 0; i < n; i++) { id[i] = i; pos[i] = i; }
            int head = 0, count = n;

            while (count - head > 0)
            {
                Vec2 v = arr[head];
                head++;                               // == points.RemoveAt(0)
                while (count - head > 0)
                {
                    int bestSlot = -1;
                    float bestD = 99999f;             // FindClosest's initial 'best'
                    int cx0 = Col(v.x, minX, range, gw);
                    int cy0 = Col(v.y, minY, range, gh);
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int cy = cy0 + dy;
                        if (cy < 0 || cy >= gh) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int cx = cx0 + dx;
                            if (cx < 0 || cx >= gw) continue;
                            for (int e = cellHead[cy * gw + cx]; e != -1; e = next[e])
                            {
                                int slot = pos[e];
                                if (slot < head || slot >= count || id[slot] != e) continue;   // not live
                                Vec2 q = arr[slot];
                                if (q == v) continue;                                          // epsilon equality
                                float d = Vec2.Distance(v, q);
                                if (d < range && (d < bestD || (d == bestD && slot < bestSlot)))
                                {
                                    bestD = d;
                                    bestSlot = slot;
                                }
                            }
                        }
                    }
                    if (bestSlot == -1) break;
                    v = (v + arr[bestSlot]) * 0.5f;
                    // points[i] = points[^1]; points.RemoveAt(^1)
                    int last = count - 1;
                    arr[bestSlot] = arr[last];
                    id[bestSlot] = id[last];
                    pos[id[bestSlot]] = bestSlot;
                    count--;
                }
                result.Add(v);
            }
            return result;
        }

        private static int Col(float value, float min, float range, int extent)
        {
            int c = (int)((value - min) / range);
            if (c < 0) return 0;
            if (c >= extent) return extent - 1;
            return c;
        }

        private static int Cell(Vec2 p, float minX, float minY, float range, int gw, int gh)
            => Col(p.y, minY, range, gh) * gw + Col(p.x, minX, range, gw);

        /// <summary>The game's literal O(n^2) merge. Kept as the reference O4 is compared against.</summary>
        private List<Vec2> MergePointsLegacy(List<Vec2> points, float range)
        {
            List<Vec2> list = new List<Vec2>();
            while (points.Count > 0)
            {
                Vec2 v = points[0];
                points.RemoveAt(0);
                while (points.Count > 0)
                {
                    int i = FindClosest(points, v, range);
                    if (i == -1) break;
                    v = (v + points[i]) * 0.5f;
                    points[i] = points[points.Count - 1];
                    points.RemoveAt(points.Count - 1);
                }
                list.Add(v);
            }
            return list;
        }

        /// <summary>
        /// WorldGenerator.FindClosest (decomp 323-340). The skip test is Vector2's EPSILON equality, not
        /// exact equality (Vec2.operator ==).
        /// </summary>
        private static int FindClosest(List<Vec2> points, Vec2 p, float maxDistance)
        {
            int result = -1;
            float best = 99999f;
            for (int i = 0; i < points.Count; i++)
            {
                if (!(points[i] == p))
                {
                    float d = Vec2.Distance(p, points[i]);
                    if (d < maxDistance && d < best)
                    {
                        result = i;
                        best = d;
                    }
                }
            }
            return result;
        }

        // ---------------------------------------------------------------------------------------------
        // Rivers
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldGenerator.PlaceRivers (decomp 416-451). Reseeds from m_riverSeed.
        ///
        /// Note that a start lake is removed from the work list ONLY when no end was found, so one lake
        /// keeps spawning rivers until it runs out of legal partners; and the loop condition is
        /// Count > 1, so the last remaining lake is never used as a start.
        /// FindRandomRiverEnd consumes exactly one int draw iff it returns an index and none when it
        /// returns -1, so the 5000 m fallback is always reached on a zero-draw path.
        /// </summary>
        private List<River> PlaceRivers(UnityRandom rnd)
        {
            (int, int, int, int) saved = rnd.GetState();
            rnd.InitState(m_riverSeed);
            List<River> list = new List<River>();
            List<Vec2> lakes = m_lakes!;
            List<Vec2> work = new List<Vec2>(lakes);
            while (work.Count > 1)
            {
                Vec2 p = work[0];
                int i = FindRandomRiverEnd(rnd, list, lakes, p, 2000f, 0.4f, 128f);
                if (i == -1 && !HaveRiver(list, p))
                {
                    i = FindRandomRiverEnd(rnd, list, lakes, p, 5000f, 0.4f, 128f);
                }
                if (i != -1)
                {
                    River river = new River();
                    river.p0 = p;
                    river.p1 = lakes[i];
                    river.center = (river.p0 + river.p1) * 0.5f;
                    river.widthMax = rnd.Range(60f, 100f);
                    river.widthMin = rnd.Range(60f, river.widthMax);   // uses the value just drawn
                    float len = Vec2.Distance(river.p0, river.p1);
                    river.curveWidth = (float)((double)len / 15.0);
                    river.curveWavelength = (float)((double)len / 20.0);
                    list.Add(river);
                }
                else
                {
                    work.RemoveAt(0);
                }
            }
            RenderRivers(rnd, list);
            rnd.SetState(saved);
            return list;
        }

        /// <summary>
        /// WorldGenerator.FindRandomRiverEnd (decomp 472-487). Range(0, count) is max-EXCLUSIVE, so the
        /// last candidate IS reachable here - unlike RandomBiomeFromBiomes elsewhere in the game.
        /// </summary>
        private int FindRandomRiverEnd(UnityRandom rnd, List<River> rivers, List<Vec2> points, Vec2 p,
                                       float maxDistance, float heightLimit, float checkStep)
        {
            List<int> list = new List<int>();
            for (int i = 0; i < points.Count; i++)
            {
                if (!(points[i] == p) && Vec2.Distance(p, points[i]) < maxDistance
                    && !HaveRiver(rivers, p, points[i]) && IsRiverAllowed(p, points[i], checkStep, heightLimit))
                {
                    list.Add(i);
                }
            }
            if (list.Count == 0) return -1;      // no draw is consumed on this path
            return list[rnd.Range(0, list.Count)];
        }

        /// <summary>WorldGenerator.HaveRiver(rivers, p0) (decomp 489-499) - epsilon equality.</summary>
        private static bool HaveRiver(List<River> rivers, Vec2 p0)
        {
            for (int i = 0; i < rivers.Count; i++)
            {
                River r = rivers[i];
                if (r.p0 == p0 || r.p1 == p0) return true;
            }
            return false;
        }

        /// <summary>WorldGenerator.HaveRiver(rivers, p0, p1) (decomp 501-511) - unordered pair test.</summary>
        private static bool HaveRiver(List<River> rivers, Vec2 p0, Vec2 p1)
        {
            for (int i = 0; i < rivers.Count; i++)
            {
                River r = rivers[i];
                if ((r.p0 == p0 && r.p1 == p1) || (r.p0 == p1 && r.p1 == p0)) return true;
            }
            return false;
        }

        /// <summary>
        /// WorldGenerator.IsRiverAllowed (decomp 513-536). No RNG. Any sample above the height limit
        /// kills the river outright; and the line must cross at least some land (flag stays true only if
        /// every sample was under water).
        /// </summary>
        private bool IsRiverAllowed(Vec2 p0, Vec2 p1, float step, float heightLimit)
        {
            float len = Vec2.Distance(p0, p1);
            Vec2 dir = (p1 - p0).Normalized;
            bool allWater = true;
            for (float s = step; s <= (float)((double)len - (double)step); s = (float)((double)s + (double)step))
            {
                Vec2 q = p0 + dir * s;
                float b = GetBaseHeight(q.x, q.y, menuTerrain: false);
                if (b > heightLimit) return false;
                if (b > 0.05f) allWater = false;
            }
            return !allWater;
        }

        // ---------------------------------------------------------------------------------------------
        // Streams
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldGenerator.PlaceStreams (decomp 342-375). Called TWICE, both times reseeding from the same
        /// m_streamSeed - but pass 2 sees different terrain (the Deep North pregeneration +0.1f, and the
        /// river points pass 1 just rendered), so the two diverge at the first height comparison that
        /// flips. riverPreGen = !isDN is threaded all the way down to GetDeepNorthHeightPregenerate.
        /// </summary>
        private List<River> PlaceStreams(UnityRandom rnd, bool isDN)
        {
            (int, int, int, int) saved = rnd.GetState();
            rnd.InitState(m_streamSeed);
            List<River> list = new List<River>();
            for (int i = 0; i < 3000; i++)
            {
                if (FindStreamStartPoint(rnd, 100, 26f, 31f, out Vec2 p, out _, !isDN)
                    && FindStreamEndPoint(rnd, 100, 36f, 44f, p, 80f, 200f, out Vec2 end, !isDN))
                {
                    Vec2 center = (p + end) * 0.5f;
                    float mh = GetPregenerationHeight(center.x, center.y, !isDN);
                    if (!(mh < 26f) && !(mh > 44f))
                    {
                        River river = new River();
                        river.p0 = p;
                        river.p1 = end;
                        river.center = center;
                        river.widthMax = 20f;
                        river.widthMin = 20f;
                        float len = Vec2.Distance(river.p0, river.p1);
                        river.curveWidth = (float)((double)len / 15.0);
                        river.curveWavelength = (float)((double)len / 20.0);
                        list.Add(river);
                    }
                }
            }
            RenderRivers(rnd, list, isDN ? RiverAdd.OnlyDeepNorth : RiverAdd.SkipDeepNorth);
            rnd.SetState(saved);
            return list;
        }

        /// <summary>
        /// WorldGenerator.FindStreamStartPoint (decomp 397-414). BOTH coordinates are drawn before the
        /// height test, so a failed attempt costs exactly two draws - 100 failures cost 200.
        /// </summary>
        private bool FindStreamStartPoint(UnityRandom rnd, int iterations, float minHeight, float maxHeight,
                                          out Vec2 p, out float starth, bool riverPreGen)
        {
            for (int i = 0; i < iterations; i++)
            {
                float x = rnd.Range(-10000f, 10000f);
                float y = rnd.Range(-10000f, 10000f);
                float h = GetPregenerationHeight(x, y, riverPreGen);
                if (h > minHeight && h < maxHeight)
                {
                    p = new Vec2(x, y);
                    starth = h;
                    return true;
                }
            }
            p = Vec2.Zero;
            starth = 0f;
            return false;
        }

        /// <summary>
        /// WorldGenerator.FindStreamEndPoint (decomp 377-395). The radius shrinks by
        /// (maxLength-minLength)/iterations each try, so the first candidate is at 198.8 m and the last
        /// at 80 m. Random.Range(0f, MathF.PI*2f) - the float constant is 6.2831854820251465.
        /// </summary>
        private bool FindStreamEndPoint(UnityRandom rnd, int iterations, float minHeight, float maxHeight,
                                        Vec2 start, float minLength, float maxLength, out Vec2 end, bool riverPreGen)
        {
            float stepLen = (float)(((double)maxLength - (double)minLength) / (double)iterations);
            float cur = maxLength;
            for (int i = 0; i < iterations; i++)
            {
                cur = (float)((double)cur - (double)stepLen);
                float ang = rnd.Range(0f, 6.2831854820251465f);
                Vec2 q = start + new Vec2(UMathf.Sin(ang), UMathf.Cos(ang)) * cur;
                float h = GetPregenerationHeight(q.x, q.y, riverPreGen);
                if (h > minHeight && h < maxHeight)
                {
                    end = q;
                    return true;
                }
            }
            end = Vec2.Zero;
            return false;
        }

        // ---------------------------------------------------------------------------------------------
        // River rendering and the river grid
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldGenerator.RenderRivers (decomp 538-578). One Random draw per point step, every step,
        /// even for streams where widthMin == widthMax == 20.
        ///
        /// Per-cell point ORDER is load-bearing because GetWeight accumulates a float sum over the array:
        /// rivers (in m_rivers order, by step, by grid scan), then pass-1 streams, then pass-2 streams.
        /// The merge below appends the new points AFTER the existing ones and installs a NEW array.
        /// </summary>
        private void RenderRivers(UnityRandom rnd, List<River> rivers, RiverAdd addRule = RiverAdd.All)
        {
            Dictionary<Vec2i, List<RiverPoint>> pending = new Dictionary<Vec2i, List<RiverPoint>>();
            foreach (River river in rivers)
            {
                if (addRule != RiverAdd.All)
                {
                    bool dn = IsDeepnorth(river.p0.x, river.p0.y);   // p0 only
                    if ((dn && addRule == RiverAdd.SkipDeepNorth) || (!dn && addRule == RiverAdd.OnlyDeepNorth))
                    {
                        continue;
                    }
                }
                float step = (float)((double)river.widthMin / 8.0);
                Vec2 dir = (river.p1 - river.p0).Normalized;
                // RenderRivers IL_0083 'ldfld Vector2::y' / IL_0088 'neg' - a sign flip, not a
                // subtraction from +0. The two differ only for dir.y == +0f, where neg gives -0f and
                // 0f - y gives +0f; harmless here (the component is scaled by the curve offset and
                // added to a position, and x + (-0) == x + (+0) for every x except -0) but there is no
                // reason to write the one the IL does not.
                Vec2 perp = new Vec2(-dir.y, dir.x);
                float len = Vec2.Distance(river.p0, river.p1);
                for (float s = 0f; s <= len; s = (float)((double)s + (double)step))
                {
                    float t = (float)((double)s / (double)river.curveWavelength);
                    // Three DOUBLE sines multiplied together, one conv.r4 at the end.
                    float off = (float)(Math.Sin(t) * Math.Sin((double)t * 0.634119987487793)
                                        * Math.Sin((double)t * 0.3341200053691864) * (double)river.curveWidth);
                    float r = rnd.Range(river.widthMin, river.widthMax);
                    Vec2 p = river.p0 + dir * s + perp * off;
                    AddRiverPoint(pending, p, r);
                }
            }
            foreach (KeyValuePair<Vec2i, List<RiverPoint>> item in pending)
            {
                if (m_riverPoints.TryGetValue(item.Key, out RiverPoint[]? existing))
                {
                    // The old spelling was new List<RiverPoint>(existing) + AddRange + ToArray: three
                    // allocations and three copies of the cell to produce one array. This is the same
                    // array - the existing points first, in their order, then the new ones in theirs -
                    // built in one allocation. Order is what GetWeight's float sum depends on, and the
                    // order here is unchanged by construction: a block copy cannot reorder anything.
                    RiverPoint[] merged = new RiverPoint[existing.Length + item.Value.Count];
                    Array.Copy(existing, merged, existing.Length);
                    item.Value.CopyTo(merged, existing.Length);
                    m_riverPoints[item.Key] = merged;
                }
                else
                {
                    m_riverPoints.Add(item.Key, item.Value.ToArray());
                }
            }
            // NOTE (01-worldgen-core.md 4.7): the game does NOT invalidate m_cachedRiverGrid /
            // m_cachedRiverPoints here, so the first GetRiverWeight after a render can read a stale
            // array. That is reproduced by simply not touching the cache fields.
        }

        /// <summary>
        /// WorldGenerator.AddRiverPoint(dict, p, r, river) (decomp 580-595). The scan is y OUTER, x INNER
        /// and the cell is built as (x, y) - keep the nesting, it fixes the per-cell point order.
        /// The 'river' argument is unused by the point it builds, so it is dropped here.
        /// </summary>
        private void AddRiverPoint(Dictionary<Vec2i, List<RiverPoint>> riverPoints, Vec2 p, float r)
        {
            Vec2i g = GetRiverGrid(p.x, p.y);
            int n = UMathf.CeilToInt((float)((double)r / 64.0));
            for (int y = g.y - n; y <= g.y + n; y++)
            {
                for (int x = g.x - n; x <= g.x + n; x++)
                {
                    Vec2i grid = new Vec2i(x, y);
                    if (InsideRiverGrid(grid, p, r))
                    {
                        if (riverPoints.TryGetValue(grid, out List<RiverPoint>? cell))
                        {
                            cell.Add(new RiverPoint(p, r));
                        }
                        else
                        {
                            cell = new List<RiverPoint>();
                            cell.Add(new RiverPoint(p, r));
                            riverPoints.Add(grid, cell);
                        }
                    }
                }
            }
        }

        /// <summary>WorldGenerator.InsideRiverGrid (decomp 609-618). Math.Abs on floats.</summary>
        public bool InsideRiverGrid(Vec2i grid, Vec2 p, float r)
        {
            Vec2 c = new Vec2((float)((double)grid.x * 64.0), (float)((double)grid.y * 64.0));
            Vec2 d = p - c;
            if (Math.Abs(d.x) < (float)((double)r + 32.0))
            {
                return Math.Abs(d.y) < (float)((double)r + 32.0);
            }
            return false;
        }

        /// <summary>WorldGenerator.GetRiverGrid (decomp 620-625) - the same layout as the zone grid.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vec2i GetRiverGrid(float wx, float wy)
        {
            int x = UMathf.FloorToInt((float)(((double)wx + 32.0) / 64.0));
            int y = UMathf.FloorToInt((float)(((double)wy + 32.0) / 64.0));
            return new Vec2i(x, y);
        }

        /// <summary>
        /// WorldGenerator.GetRiverWeight (decomp 627-664), minus the ReaderWriterLockSlim - a single
        /// handle is single-threaded here, and Fork() gives every thread its own cache.
        ///
        /// The single-entry cache starts at (-999999, -999999) so it can never false-hit on the first
        /// query. It is deliberately NOT invalidated by RenderRivers: see the note there.
        /// </summary>
        private void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            // The one place a height query reads pregenerated data. A deferred handle pays for
            // pregeneration here, the first time a height (not a BASE height) is asked for.
            if (m_pregenPending) EnsurePregenerated();
            Vec2i g = GetRiverGrid(wx, wy);
            if (g == m_cachedRiverGrid)
            {
                if (m_cachedRiverPoints != null)
                {
                    GetWeight(m_cachedRiverPoints, wx, wy, out weight, out width);
                }
                else
                {
                    weight = 0f;
                    width = 0f;
                }
                return;
            }
            if (m_riverPoints.TryGetValue(g, out RiverPoint[]? pts))
            {
                GetWeight(pts, wx, wy, out weight, out width);
                m_cachedRiverGrid = g;
                m_cachedRiverPoints = pts;
            }
            else
            {
                m_cachedRiverGrid = g;
                m_cachedRiverPoints = null;
                weight = 0f;
                width = 0f;
            }
        }

        /// <summary>
        /// GetRiverWeight, exposed for validation and inspection (it is private in the game). Reports
        /// how strongly a point is inside a river/stream and the weighted mean width there.
        /// </summary>
        public void GetRiverWeightPublic(float wx, float wy, out float weight, out float width)
            => GetRiverWeight(wx, wy, out weight, out width);

        /// <summary>
        /// WorldGenerator.GetWeight (decomp 666-693). 'weight' is a max and so order-insensitive, but the
        /// two accumulators are FLOAT sums over the array in array order and are order-SENSITIVE
        /// (01-worldgen-core.md 6.9). The distance test is Vector2.SqrMagnitude - float interior.
        /// </summary>
        private static void GetWeight(RiverPoint[] points, float wx, float wy, out float weight, out float width)
        {
            Vec2 q = new Vec2(wx, wy);
            weight = 0f;
            width = 0f;
            float acc = 0f;
            float wsum = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                RiverPoint rp = points[i];
                float d2 = Vec2.SqrMagnitude(rp.p - q);
                if (d2 < rp.w2)
                {
                    float d = (float)Math.Sqrt((double)d2);
                    float w = (float)(1.0 - (double)d / (double)rp.w);
                    if (w > weight) weight = w;
                    acc = (float)((double)acc + (double)rp.w * (double)w);
                    wsum = (float)((double)wsum + (double)w);
                }
            }
            if (wsum > 0f)
            {
                width = (float)((double)acc / (double)wsum);
            }
        }

        /// <summary>
        /// WorldGenerator.AddRivers (decomp 937-957). Rivers only ever LOWER terrain, to 0.14...0.12
        /// normalised (28...24 m), i.e. below the 30 m water line.
        ///
        /// <para>NaN: a NaN weight does NOT return h - it falls through into the river math. The source
        /// is <c>if (weight &lt;= 0f) return h;</c>, which compiles to
        /// <c>ldloc.0; ldc.r4 0; bgt.un.s IL_0016; ldarg.3; ret</c> (IL_000c-IL_0015), and
        /// <c>bgt.un</c> branches if greater-than OR unordered, so NaN jumps PAST the return. C#'s
        /// <c>&lt;=</c> is likewise false for NaN, so the line below already matches the game; do not
        /// "fix" it to the NaN-safe <c>if (!(weight &gt; 0f)) return h;</c>, which would invert exactly
        /// the input this note is about. (Unreachable in practice: weight is only NaN if a river point's
        /// w were 0, and widths are drawn in [20, 100].)</para>
        /// </summary>
        private float AddRivers(float wx, float wy, float h)
        {
            GetRiverWeight(wx, wy, out float weight, out float width);
            if (weight <= 0f) return h;
            float t = DUtils.LerpStep(20f, 60f, width);
            float bed = DUtils.Lerp(0.14f, 0.12f, t);
            float mid = DUtils.Lerp(0.139f, 0.128f, t);
            if (h > bed)
            {
                h = DUtils.Lerp(h, bed, weight);
            }
            if (h > mid)
            {
                float t2 = DUtils.LerpStep(0.85f, 1f, weight);
                h = DUtils.Lerp(h, mid, t2);
            }
            return h;
        }

        // ---------------------------------------------------------------------------------------------
        // World angle, regions
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldGenerator.WorldAngle (decomp 879-882 / IL_0000-IL_001d). THREE float truncations: after
        /// Atan2, after *20.0 and after Sin. Keeping it in double changes band boundaries.
        /// Note the argument order (x, y) into Atan2 - not the usual (y, x).
        /// Result is in [-1, 1]; the wobble used everywhere is WorldAngle*100, i.e. +/-100 m in 20 lobes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float WorldAngle(float wx, float wy)
        {
            return (float)Math.Sin((double)(float)((double)(float)Math.Atan2((double)wx, (double)wy) * 20.0));
        }

        /// <summary>
        /// WorldGenerator.IsAshlands (decomp 751-755). AshLands is everything OUTSIDE a circle of radius
        /// 12000 + wobble centred at (0, +4000) - the far SOUTH.
        ///
        /// Deliberately different from IsDeepnorth: this one uses DUtils.Length (double-interior sqrt)
        /// and compares in DOUBLE, with the wobble never truncated to float. Do not unify them.
        /// </summary>
        public static bool IsAshlands(float x, float y) => IsAshlands(x, y, WorldAngle(x, y));

        /// <summary>
        /// O1. <see cref="IsAshlands(float,float)"/> with <see cref="WorldAngle"/>'s result supplied by
        /// the caller.
        ///
        /// <para><b>Exactness.</b> <c>WorldAngle</c> is static, pure and depends on nothing but its two
        /// arguments, so <c>WorldAngle(x, y)</c> evaluated once and handed to three consumers is the
        /// same float as <c>WorldAngle(x, y)</c> evaluated three times - it is common subexpression
        /// elimination done by hand, on a function the JIT cannot do it for because <c>Math.Atan2</c>
        /// and <c>Math.Sin</c> are intrinsic calls it will not prove pure. The body below is copied
        /// character for character from the two-argument form; only the first line's source of
        /// <c>a</c> changed. The two-argument form now calls this one, so there is exactly one copy of
        /// the arithmetic and the two cannot drift apart.</para>
        /// </summary>
        public static bool IsAshlands(float x, float y, float worldAngle)
        {
            double a = (double)worldAngle * 100.0;
            return (double)DUtils.Length(x, (float)((double)y + (double)AshlandsYOffset)) > (double)AshlandsMinDistance + a;
        }

        /// <summary>
        /// WorldGenerator.IsDeepnorth (decomp 773-777). Outside a circle of radius 12000 + wobble centred
        /// at (0, -4000) - the far NORTH.
        ///
        /// Uses Vector2.magnitude (FLOAT-interior sum) and compares in FLOAT, with the wobble truncated -
        /// the mirror image of IsAshlands in geometry but not in arithmetic.
        /// </summary>
        public static bool IsDeepnorth(float x, float y) => IsDeepnorth(x, y, WorldAngle(x, y));

        /// <summary>
        /// O1. <see cref="IsDeepnorth(float,float)"/> with <see cref="WorldAngle"/>'s result supplied.
        /// Same argument as <see cref="IsAshlands(float,float,float)"/>; the body is unchanged, and note
        /// that it still truncates the wobble to float where IsAshlands does not.
        /// </summary>
        public static bool IsDeepnorth(float x, float y, float worldAngle)
        {
            float a = (float)((double)worldAngle * 100.0);
            return new Vec2(x, (float)((double)y + 4000.0)).Magnitude > (float)(12000.0 + (double)a);
        }

        /// <summary>
        /// WorldGenerator.CreateAshlandsGap (decomp 1385-1391) - a private INSTANCE method in the game,
        /// but it reads no instance state. 0 exactly on the AshLands ring, rising to 1 at 400 m from it;
        /// multiplied into the height multiplier, this is what carves the ocean moat.
        /// The (float) cast before MathfLikeSmoothStep is real, and MathfLikeSmoothStep itself returns a
        /// float-rounded double.
        /// </summary>
        public static double CreateAshlandsGap(float wx, float wy)
        {
            double a = (double)WorldAngle(wx, wy) * 100.0;
            double v = (double)DUtils.Length(wx, wy + AshlandsYOffset) - ((double)AshlandsMinDistance + a);
            v = DUtils.Clamp01(Math.Abs(v) / 400.0);
            return DUtils.MathfLikeSmoothStep(0.0, 1.0, (float)v);
        }

        /// <summary>
        /// WorldGenerator.CreateDeepNorthGap (decomp 1393-1399) - public static, and written with the
        /// literals 12000/4000 rather than the AshLands fields.
        /// </summary>
        public static double CreateDeepNorthGap(float wx, float wy)
        {
            double a = (double)WorldAngle(wx, wy) * 100.0;
            double v = (double)DUtils.Length(wx, wy + 4000f) - (12000.0 + a);
            v = DUtils.Clamp01(Math.Abs(v) / 400.0);
            return DUtils.MathfLikeSmoothStep(0.0, 1.0, (float)v);
        }

        /// <summary>WorldGenerator.DeepNorthWaveFade (decomp 1401-1405) - no smoothstep, /200 not /400.</summary>
        public static double DeepNorthWaveFade(float wx, float wy)
        {
            double a = (double)WorldAngle(wx, wy) * 100.0;
            return DUtils.Clamp01(((double)DUtils.Length(wx, wy + 4000f) - (12000.0 + a)) / 200.0);
        }

        /// <summary>
        /// WorldGenerator.GetAshlandsOceanGradient (decomp 757-761). Note the wobble is evaluated at
        /// (x, y + ashlandsYOffset) here, OUT OF PHASE with IsAshlands. Not used by biome or height.
        /// </summary>
        public static float GetAshlandsOceanGradient(float x, float y)
        {
            double a = (double)WorldAngle(x, y + AshlandsYOffset) * 100.0;
            return (float)(((double)DUtils.Length(x, y + AshlandsYOffset) - ((double)AshlandsMinDistance + a)) / 300.0);
        }

        // ---------------------------------------------------------------------------------------------
        // Base height
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldGenerator.GetBaseHeight (decomp 884-935). The heart of the terrain.
        ///
        /// X and Y stay DOUBLE here and are never truncated to float - unlike the per-biome coordinates
        /// (see GetMeadowsHeight), which are quantised to a 1/128 m lattice. The grouping is
        /// wx + (100000.0 + off0); the per-biome functions use (wx + 100000.0) + off3 instead. Both are
        /// exact, but they are copied as written.
        ///
        /// off0 feeds X and off1 feeds Y here; every mask and detail noise elsewhere uses ONE offset for
        /// both axes.
        ///
        /// <para>This method is now the dispatcher: the transcription itself lives unchanged in
        /// <see cref="GetBaseHeightCore"/>. Two optimisations sit here, and neither can change a value.
        /// <b>O2</b> is a one-entry memo - GetBaseHeight is pure, and GetBiome and the per-biome height
        /// function that follows it are always called at the SAME (wx, wy), so the second call is free.
        /// <b>O5</b> routes the non-menu path to <see cref="GetBaseHeightSimd"/>, which issues the same
        /// eight Perlin samples 8-wide. On a machine without AVX2 the scalar transcription runs instead.
        /// </para>
        /// </summary>
        private float GetBaseHeight(float wx, float wy, bool menuTerrain)
        {
            if (menuTerrain) return GetBaseHeightCore(wx, wy, menuTerrain: true);

            // O2. The one-entry memo. The key is the exact BIT PATTERN of both coordinates, not a float
            // comparison, so a hit can only happen for arguments identical in every bit - and
            // GetBaseHeightCore is a pure, deterministic function of those bits and the immutable
            // offsets, so the memoised float is the float the call would have produced. That is the
            // whole argument: no case analysis is needed for -0f (a different bit pattern from +0f, so
            // it simply misses) or for NaN (a NaN key matches only the same NaN, and returning the same
            // answer for it is still right). The memo is never invalidated because nothing it depends
            // on can change.
            int kx = BitConverter.SingleToInt32Bits(wx);
            int ky = BitConverter.SingleToInt32Bits(wy);
            if (m_bhValid && m_bhKeyX == kx && m_bhKeyY == ky) return m_bhValue;

            float r = PerlinFast.Use8Wide ? GetBaseHeightSimd(wx, wy) : GetBaseHeightCore(wx, wy, menuTerrain: false);
            m_bhKeyX = kx; m_bhKeyY = ky; m_bhValue = r; m_bhValid = true;
            return r;
        }

        /// <summary>
        /// O5. <see cref="GetBaseHeightCore"/>'s non-menu path with its EIGHT
        /// <c>Mathf.PerlinNoise</c> evaluations issued as one 8-wide AVX2 batch.
        ///
        /// <para><b>Why the batch is legitimate.</b> All eight arguments are functions of
        /// <c>(wx, wy)</c> and the immutable offsets alone. None of them depends on the running sum
        /// <c>h</c>, none depends on another sample's result, and all eight are evaluated
        /// unconditionally on this path in the original too (the two sea-channel samples sit above the
        /// first early return). So gathering them changes which values are computed not at all - only
        /// when. The sixteen coordinate expressions below are copied character for character from
        /// <see cref="GetBaseHeightCore"/>, including the <c>(x * f) * 0.5</c> grouping and the double
        /// interior, and the single conv.r4 that <c>DUtils.PerlinNoise</c> would have applied is written
        /// out as the <c>(float)</c> cast on each one.</para>
        ///
        /// <para><b>Why the 8-wide noise is bit-identical</b> is argued, and checked at startup, in
        /// <see cref="PerlinFast"/> and <see cref="PerlinSelfTest"/>: per-lane IEEE-754 binary32
        /// operations, no cross-lane dependency, no FMA, and a guarded domain.</para>
        ///
        /// <para>Everything after the eight samples - the four accumulation statements, the sea-channel
        /// blend, the ocean edge and the mountain cap - is the original code, unchanged and still scalar.
        /// <c>menuTerrain</c> is not handled here: it is a different formula used only by the main-menu
        /// world, and it falls back to <see cref="GetBaseHeightCore"/>.</para>
        /// </summary>
        // SkipLocalsInit: the three stackalloc buffers are 96 bytes that the JIT would otherwise
        // zero on every call, and all 24 floats are written before anything reads them (ax and ay
        // below, r by PerlinNoise8, which fills all eight lanes on both of its paths). It changes
        // no value - only whether dead bytes are cleared first.
        [SkipLocalsInit]
        private unsafe float GetBaseHeightSimd(float wx, float wy)
        {
            // The 8-wide Perlin is only valid for finite arguments well below 2^31 (see
            // PerlinFast.Domain8). Every argument below is at most (|w| + 100000 + |offset|) * 0.01,
            // so |wx|, |wy| < 1e6 is a sufficient, and very slack, precondition; NaN fails the test and
            // takes the scalar path, which is the reference.
            if (!(UMathf.Abs(wx) < 1e6f && UMathf.Abs(wy) < 1e6f)) return GetBaseHeightCore(wx, wy, menuTerrain: false);

            float add = 0f;
            float mul = 1f;

            float dist = DUtils.Length(wx, wy);
            double x = wx;
            double y = wy;
            x += 100000.0 + (double)m_offset0;
            y += 100000.0 + (double)m_offset1;

            float* ax = stackalloc float[8];
            float* ay = stackalloc float[8];
            float* r = stackalloc float[8];
            ax[0] = (float)(x * 0.0020000000949949026 * 0.5);   ay[0] = (float)(y * 0.0020000000949949026 * 0.5);
            ax[1] = (float)(x * 0.003000000026077032 * 0.5);    ay[1] = (float)(y * 0.003000000026077032 * 0.5);
            ax[2] = (float)(x * 0.0020000000949949026 * 1.0);   ay[2] = (float)(y * 0.0020000000949949026 * 1.0);
            ax[3] = (float)(x * 0.003000000026077032 * 1.0);    ay[3] = (float)(y * 0.003000000026077032 * 1.0);
            ax[4] = (float)(x * 0.004999999888241291 * 1.0);    ay[4] = (float)(y * 0.004999999888241291 * 1.0);
            ax[5] = (float)(x * 0.009999999776482582 * 1.0);    ay[5] = (float)(y * 0.009999999776482582 * 1.0);
            ax[6] = (float)(x * 0.0020000000949949026 * 0.25 + 0.12300000339746475);
            ay[6] = (float)(y * 0.0020000000949949026 * 0.25 + 0.15123000741004944);
            ax[7] = (float)(x * 0.0020000000949949026 * 0.25 + 0.32100000977516174);
            ay[7] = (float)(y * 0.0020000000949949026 * 0.25 + 0.23100000619888306);
            PerlinFast.PerlinNoise8(ax, ay, r);

            float h = 0f;
            h = (float)((double)h + (double)r[0] * (double)r[1] * 1.0);
            h = (float)((double)h + (double)r[2] * (double)r[3] * (double)h * 0.8999999761581421);
            h = (float)((double)h + (double)r[4] * (double)r[5] * 0.5 * (double)h);
            h = (float)((double)h - 0.07000000029802322);

            float v = UMathf.Abs((float)((double)r[6] - (double)r[7]));
            float c = (float)(1.0 - (double)DUtils.LerpStep(0.02f, 0.12f, v));
            c = (float)((double)c * (double)DUtils.SmoothStep(744f, 1000f, dist));
            h = (float)((double)h * (1.0 - (double)c));

            if (dist > 10000f)
            {
                float t = DUtils.LerpStep(10000f, 10500f, dist);
                h = DUtils.Lerp(h, -0.2f, t);
                float edge = 10490f;
                if (dist > edge)
                {
                    float t2 = UtilsMath.LerpStep(edge, 10500f, dist);
                    h = DUtils.Lerp(h, -2f, t2);
                }
                return h * mul + add;
            }

            if (dist < m_minMountainDistance && h > 0.28f)
            {
                float t3 = (float)DUtils.Clamp01(((double)h - 0.2800000011920929) / MountainCapDivisor);
                h = DUtils.Lerp(DUtils.Lerp(0.28f, 0.38f, t3), h,
                                DUtils.LerpStep((float)((double)m_minMountainDistance - 400.0), m_minMountainDistance, dist));
            }
            return h * mul + add;
        }

        /// <summary>
        /// The two <see cref="GetBaseHeight"/> implementations, exposed so a test can compare them
        /// directly. <c>simd: true</c> returns the 8-wide answer where the hardware allows it and the
        /// scalar answer otherwise; they must be equal.
        /// </summary>
        public float GetBaseHeightPathPublic(float wx, float wy, bool simd)
            => (simd && PerlinFast.Use8Wide) ? GetBaseHeightSimd(wx, wy) : GetBaseHeightCore(wx, wy, menuTerrain: false);

        /// <summary>The literal transcription, unchanged. See <see cref="GetBaseHeight"/>.</summary>
        private float GetBaseHeightCore(float wx, float wy, bool menuTerrain)
        {
            float add = 0f;    // the additive term, always 0
            float mul = 1f;    // the multiplicative term, always 1

            if (menuTerrain)
            {
                double mx = wx;
                double my = wy;
                mx += 100000.0 + (double)m_offset0;
                my += 100000.0 + (double)m_offset1;
                float mh = 0f;
                mh = (float)((double)mh + (double)PN(mx * 0.0020000000949949026 * 0.5, my * 0.0020000000949949026 * 0.5) * (double)PN(mx * 0.003000000026077032 * 0.5, my * 0.003000000026077032 * 0.5) * 1.0);
                mh = (float)((double)mh + (double)PN(mx * 0.0020000000949949026 * 1.0, my * 0.0020000000949949026 * 1.0) * (double)PN(mx * 0.003000000026077032 * 1.0, my * 0.003000000026077032 * 1.0) * (double)mh * 0.8999999761581421);
                mh = (float)((double)mh + (double)PN(mx * 0.004999999888241291 * 1.0, my * 0.004999999888241291 * 1.0) * (double)PN(mx * 0.009999999776482582 * 1.0, my * 0.009999999776482582 * 1.0) * 0.5 * (double)mh);
                mh = (float)((double)mh - 0.07000000029802322);
                return mh * mul + add;
            }

            float dist = DUtils.Length(wx, wy);
            double x = wx;
            double y = wy;
            x += 100000.0 + (double)m_offset0;
            y += 100000.0 + (double)m_offset1;
            float h = 0f;
            h = (float)((double)h + (double)PN(x * 0.0020000000949949026 * 0.5, y * 0.0020000000949949026 * 0.5) * (double)PN(x * 0.003000000026077032 * 0.5, y * 0.003000000026077032 * 0.5) * 1.0);
            h = (float)((double)h + (double)PN(x * 0.0020000000949949026 * 1.0, y * 0.0020000000949949026 * 1.0) * (double)PN(x * 0.003000000026077032 * 1.0, y * 0.003000000026077032 * 1.0) * (double)h * 0.8999999761581421);
            h = (float)((double)h + (double)PN(x * 0.004999999888241291 * 1.0, y * 0.004999999888241291 * 1.0) * (double)PN(x * 0.009999999776482582 * 1.0, y * 0.009999999776482582 * 1.0) * 0.5 * (double)h);
            h = (float)((double)h - 0.07000000029802322);

            // Sea channels: two Perlin samples of the same low-frequency field at different phases; where
            // they agree (|difference| small) the terrain is pushed towards 0, carving straits.
            float n10 = PN(x * 0.0020000000949949026 * 0.25 + 0.12300000339746475, y * 0.0020000000949949026 * 0.25 + 0.15123000741004944);
            float n11 = PN(x * 0.0020000000949949026 * 0.25 + 0.32100000977516174, y * 0.0020000000949949026 * 0.25 + 0.23100000619888306);
            float v = UMathf.Abs((float)((double)n10 - (double)n11));
            float c = (float)(1.0 - (double)DUtils.LerpStep(0.02f, 0.12f, v));
            c = (float)((double)c * (double)DUtils.SmoothStep(744f, 1000f, dist));
            h = (float)((double)h * (1.0 - (double)c));

            if (dist > 10000f)
            {
                float t = DUtils.LerpStep(10000f, 10500f, dist);
                h = DUtils.Lerp(h, -0.2f, t);
                float edge = 10490f;
                if (dist > edge)
                {
                    // The ONLY Utils.LerpStep call in the whole generator - all-float, unlike every other
                    // LerpStep here (GetBaseHeight / IL_04b3, 01-worldgen-core.md 6.4).
                    float t2 = UtilsMath.LerpStep(edge, 10500f, dist);
                    h = DUtils.Lerp(h, -2f, t2);
                }
                return h * mul + add;
            }

            if (dist < m_minMountainDistance && h > 0.28f)
            {
                // The divisor is the widening of the FLOAT expression 0.38f - 0.28f =
                // 0.099999994039535522, NOT (double)0.1f = 0.10000000149011612
                // (GetBaseHeight / IL_04f1 ldc.r8, div at IL_04fa; 01-worldgen-core.md 6.1).
                float t3 = (float)DUtils.Clamp01(((double)h - 0.2800000011920929) / MountainCapDivisor);
                h = DUtils.Lerp(DUtils.Lerp(0.28f, 0.38f, t3), h,
                                DUtils.LerpStep((float)((double)m_minMountainDistance - 400.0), m_minMountainDistance, dist));
            }
            return h * mul + add;
        }

        /// <summary>(double)0.38f - (double)0.28f, written as an expression so no digit can be mistyped.</summary>
        private const double MountainCapDivisor = (double)0.38f - (double)0.28f;

        /// <summary>
        /// WorldGenerator.BaseHeightTilt (decomp 1301-1308). Four extra GetBaseHeight evaluations, which
        /// is what makes a Mountain sample the most expensive biome to query. Each coordinate step is a
        /// double add truncated back to float.
        /// </summary>
        private float BaseHeightTilt(float wx, float wy)
        {
            float a = GetBaseHeight((float)((double)wx - 1.0), wy, menuTerrain: false);
            float b = GetBaseHeight((float)((double)wx + 1.0), wy, menuTerrain: false);
            float c = GetBaseHeight(wx, (float)((double)wy - 1.0), menuTerrain: false);
            float d = GetBaseHeight(wx, (float)((double)wy + 1.0), menuTerrain: false);
            return (float)((double)UMathf.Abs((float)((double)b - (double)a)) + (double)UMathf.Abs((float)((double)c - (double)d)));
        }

        /// <summary>
        /// GetBaseHeight, exposed for validation and for tools that want the raw normalised field.
        /// Not part of the game's public surface (it is private there).
        /// </summary>
        public float GetBaseHeightPublic(float wx, float wy, bool menuTerrain = false)
            => GetBaseHeight(wx, wy, menuTerrain);

        // ---------------------------------------------------------------------------------------------
        // Biome
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldGenerator.GetBiome (decomp 779-833). The test ORDER is the whole specification: AshLands
        /// beats Ocean, Ocean beats DeepNorth, DeepNorth beats Mountain, and the four noise-gated biomes
        /// are tried Swamp, Mistlands, Plains, BlackForest before the two fallbacks.
        ///
        /// Mask-noise coordinate shape, exactly: (double)((float)((double)off + (double)w)) * 0.001f,
        /// then DUtils.PerlinNoise truncates the product back to float - two truncations per axis, and
        /// the OFFSET comes first in the add.
        ///
        /// The lower band bounds carry the +/-100 m wobble (truncated to float after the double add); the
        /// upper bounds (6000/8000/10000) and the whole Swamp band do not.
        ///
        /// Setting waterAlwaysOcean pulls in the entire height path - rivers, m_offset3 and all - because
        /// the first test then calls GetHeight. It does not recurse: GetHeight calls GetBiome with the
        /// default flag (01-worldgen-core.md 8.2).
        /// </summary>
        public Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        {
            if (m_menu)
            {
                if (GetBaseHeight(wx, wy, menuTerrain: true) >= 0.4f) return Biome.Mountain;
                return Biome.BlackForest;
            }
            float dist = DUtils.Length(wx, wy);
            float baseHeight = GetBaseHeight(wx, wy, menuTerrain: false);
            // O1. The game evaluates WorldAngle(wx, wy) here, then again inside IsAshlands and again
            // inside IsDeepnorth: three Atan2 + Sin pairs for one value, at identical arguments. It is
            // static and pure, so the one float computed here is the same float all three would have
            // computed; the three consumers below differ only in what they do with it afterwards, and
            // those bodies are untouched (see IsAshlands(x, y, worldAngle)).
            float wa = WorldAngle(wx, wy);
            float a = (float)((double)wa * 100.0);
            if (waterAlwaysOcean && GetHeight(wx, wy) <= oceanLevel)
            {
                return Biome.Ocean;
            }
            if (IsAshlands(wx, wy, wa))
            {
                return Biome.AshLands;
            }
            if (!waterAlwaysOcean && baseHeight <= oceanLevel)
            {
                return Biome.Ocean;
            }
            if (IsDeepnorth(wx, wy, wa))
            {
                return Biome.DeepNorth;
            }
            if (baseHeight > 0.4f)
            {
                return Biome.Mountain;
            }
            if (PN((double)(float)((double)m_offset0 + (double)wx) * 0.0010000000474974513, (double)(float)((double)m_offset0 + (double)wy) * 0.0010000000474974513) > 0.6f
                && dist > 2000f && dist < m_maxMarshDistance && baseHeight > 0.05f && baseHeight < 0.25f)
            {
                return Biome.Swamp;
            }
            if (PN((double)(float)((double)m_offset4 + (double)wx) * 0.0010000000474974513, (double)(float)((double)m_offset4 + (double)wy) * 0.0010000000474974513) > m_minDarklandNoise
                && dist > (float)(6000.0 + (double)a) && dist < 10000f)
            {
                return Biome.Mistlands;
            }
            if (PN((double)(float)((double)m_offset1 + (double)wx) * 0.0010000000474974513, (double)(float)((double)m_offset1 + (double)wy) * 0.0010000000474974513) > 0.4f
                && dist > (float)(3000.0 + (double)a) && dist < 8000f)
            {
                return Biome.Plains;
            }
            if (PN((double)(float)((double)m_offset2 + (double)wx) * 0.0010000000474974513, (double)(float)((double)m_offset2 + (double)wy) * 0.0010000000474974513) > 0.4f
                && dist > (float)(600.0 + (double)a) && dist < 6000f)
            {
                return Biome.BlackForest;
            }
            if (dist > (float)(5000.0 + (double)a))
            {
                return Biome.BlackForest;
            }
            return Biome.Meadows;
        }

        /// <summary>
        /// WorldGenerator.GetBiome(Vector3) (decomp 746-749) - drops the Y component: the generator's
        /// second argument is always the world Z.
        /// </summary>
        public Biome GetBiomeAt(float x, float y, float z) => GetBiome(x, z);

        /// <summary>
        /// WorldGenerator.GetBiome(Vector2s) (decomp 725-734) - the cached short-coordinate lookup that
        /// backs GetBiomeArea. The shorts widen to float.
        /// </summary>
        private Biome GetBiome(Vec2s point)
        {
            m_cachedBiomes ??= new Dictionary<Vec2s, Biome>();
            if (m_cachedBiomes.TryGetValue(point, out Biome v)) return v;
            v = GetBiome(point.x, point.y);
            m_cachedBiomes.Add(point, v);
            return v;
        }

        /// <summary>
        /// WorldGenerator.GetBiomeArea(Vector2s) (decomp 705-723) - the overload vanilla actually uses.
        /// Median only when all eight 64 m neighbours agree with the centre.
        ///
        /// The other overload, GetBiomeArea(Vector3) (decomp 736-744), is buggy: it tests the
        /// (64, 0, 0) offset TWICE and never (-64, 0, 0). It has no vanilla caller, and it reads
        /// AltBiome sector data an offline tool does not have (without m_biomeData every lookup returns
        /// BiomeSector.EmptyBlackForest, so it would answer Median everywhere). It is therefore not
        /// ported - never "fix" it into this one.
        /// </summary>
        public BiomeArea GetBiomeArea(Vec2s point)
        {
            m_cachedBiomeAreas ??= new Dictionary<Vec2s, BiomeArea>();
            if (m_cachedBiomeAreas.TryGetValue(point, out BiomeArea v)) return v;
            // The game assigns all eight neighbours to locals BEFORE evaluating the || chain
            // (decomp 705-723), so all eight GetBiome calls always run and all eight results always
            // land in m_cachedBiomes. An early `break` would return the same BiomeArea - GetBiome is
            // pure and the cache is pure memoisation - but it would leave a different cache behind,
            // so the loop is spelled out the way the game wrote it.
            Biome b = GetBiome(point);
            Biome b2 = GetBiome(point - s_biomeAreaOffsetsInt[0]);
            Biome b3 = GetBiome(point - s_biomeAreaOffsetsInt[1]);
            Biome b4 = GetBiome(point - s_biomeAreaOffsetsInt[2]);
            Biome b5 = GetBiome(point - s_biomeAreaOffsetsInt[3]);
            Biome b6 = GetBiome(point - s_biomeAreaOffsetsInt[4]);
            Biome b7 = GetBiome(point - s_biomeAreaOffsetsInt[5]);
            Biome b8 = GetBiome(point - s_biomeAreaOffsetsInt[6]);
            Biome b9 = GetBiome(point - s_biomeAreaOffsetsInt[7]);
            v = (b != b2 || b != b3 || b != b4 || b != b5 || b != b6 || b != b7 || b != b8 || b != b9)
                ? BiomeArea.Edge
                : BiomeArea.Median;
            m_cachedBiomeAreas.Add(point, v);
            return v;
        }

        // ---------------------------------------------------------------------------------------------
        // Height entry points
        // ---------------------------------------------------------------------------------------------

        /// <summary>WorldGenerator.GetHeight(float,float) (decomp 998-1003). World metres, sea level 30.</summary>
        public float GetHeight(float wx, float wy)
        {
            Biome biome = GetBiome(wx, wy);
            return GetBiomeHeight(biome, wx, wy, out _);
        }

        /// <summary>WorldGenerator.GetHeight(float,float,out Color) (decomp 1005-1009).</summary>
        public float GetHeight(float wx, float wy, out ColorRGBA mask)
        {
            Biome biome = GetBiome(wx, wy);
            return GetBiomeHeight(biome, wx, wy, out mask);
        }

        /// <summary>WorldGenerator.GetPregenerationHeight (decomp 1011-1016).</summary>
        public float GetPregenerationHeight(float wx, float wy, bool riverPreGen)
        {
            Biome biome = GetBiome(wx, wy);
            return GetBiomeHeight(biome, wx, wy, out _, preGeneration: true, riverPreDN: riverPreGen);
        }

        /// <summary>
        /// WorldGenerator.GetBiomeHeight (decomp 1018-1071). Returns WORLD METRES with sea level at 30
        /// (0.15 normalised x 200).
        ///
        /// The multiplier is 200 during pregeneration and 200 * ashlandsGap * deepNorthGap afterwards -
        /// the two gaps are what sink the ocean moat in front of AshLands and Deep North.
        ///
        /// The game also calls GetBiomeSector(wx, wy) here and DISCARDS the result (IL_003b); it only
        /// reads World.m_biomeData, which an offline tool does not have, so it is omitted.
        ///
        /// The 10500 m short circuit returns exactly -400 and happens after the menu branch and before
        /// the switch - note that it ignores the gap multiplier entirely.
        /// </summary>
        public float GetBiomeHeight(Biome biome, float wx, float wy, out ColorRGBA mask,
                                    bool preGeneration = false, bool riverPreDN = true)
        {
            float add = 0f;
            float mult = (!preGeneration)
                ? (float)((double)GetHeightMultiplier() * CreateAshlandsGap(wx, wy) * CreateDeepNorthGap(wx, wy))
                : GetHeightMultiplier();
            mask = ColorRGBA.Black;

            if (m_menu)
            {
                if (biome == Biome.Mountain)
                {
                    return (float)((double)GetSnowMountainHeight(wx, wy, menu: true) * (double)mult + (double)add);
                }
                return (float)((double)GetMenuHeight(wx, wy) * (double)mult + (double)add);
            }
            if (DUtils.Length(wx, wy) > 10500f)
            {
                return -2f * GetHeightMultiplier();
            }
            switch (biome)
            {
                case Biome.Swamp:
                    return (float)((double)GetMarshHeight(wx, wy) * (double)mult + (double)add);
                case Biome.DeepNorth:
                    if (preGeneration)
                    {
                        return (float)((double)GetDeepNorthHeightPregenerate(wx, wy, riverPreDN) * (double)mult + (double)add);
                    }
                    return (float)((double)GetDeepNorthHeight(wx, wy, out mask) * (double)mult + (double)add);
                case Biome.Mountain:
                    return (float)((double)GetSnowMountainHeight(wx, wy, menu: false) * (double)mult + (double)add);
                case Biome.BlackForest:
                    return (float)((double)GetForestHeight(wx, wy) * (double)mult + (double)add);
                case Biome.Ocean:
                    return (float)((double)GetOceanHeight(wx, wy) * (double)mult + (double)add);
                case Biome.AshLands:
                    if (preGeneration)
                    {
                        return (float)((double)GetAshlandsHeightPregenerate(wx, wy) * (double)mult + (double)add);
                    }
                    return (float)((double)GetAshlandsHeight(wx, wy, out mask) * (double)mult + (double)add);
                case Biome.Plains:
                    return (float)((double)GetPlainsHeight(wx, wy) * (double)mult + (double)add);
                case Biome.Meadows:
                    return (float)((double)GetMeadowsHeight(wx, wy) * (double)mult + (double)add);
                case Biome.Mistlands:
                    if (preGeneration)
                    {
                        // Pre-generation Mistlands is literally BlackForest.
                        return (float)((double)GetForestHeight(wx, wy) * (double)mult + (double)add);
                    }
                    return (float)((double)GetMistlandsHeight(wx, wy, out mask) * (double)mult + (double)add);
                default:
                    return 0f;     // includes Biome.None
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Per-biome heights - all return NORMALISED height (x mult afterwards)
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldGenerator.GetMarshHeight (decomp 1073-1087) - Swamp.
        /// The only biome with NO seed offset at all, so swamp micro-terrain is identical in every world;
        /// only where swamp appears changes. It also ignores GetBaseHeight entirely - a flat 0.137
        /// (27.4 m) plus detail.
        /// </summary>
        private float GetMarshHeight(float wx, float wy)
        {
            float wx2 = wx;
            float wy2 = wy;
            float h = 0.137f;
            wx = (float)((double)wx + 100000.0);
            wy = (float)((double)wy + 100000.0);
            double u = wx;
            double v = wy;
            float n = (float)((double)PN(u * 0.03999999910593033, v * 0.03999999910593033) * (double)PN(u * 0.07999999821186066, v * 0.07999999821186066));
            h = (float)((double)h + (double)n * 0.029999999329447746);
            h = AddRivers(wx2, wy2, h);
            h = (float)((double)h + (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * 0.009999999776482582);
            return (float)((double)h + (double)PN(u * 0.4000000059604645, v * 0.4000000059604645) * 0.003000000026077032);
        }

        /// <summary>
        /// WorldGenerator.GetMeadowsHeight (decomp 1089-1112) - Meadows.
        ///
        /// The per-biome coordinate preparation shared by almost every biome below: the ORIGINAL wx/wy are
        /// kept for AddRivers, and the noise coordinates are (w + 100000.0) + off3 truncated to FLOAT.
        /// That truncation is not cosmetic - u lies in 79500...120499, the binade where a float ulp is
        /// exactly 1/128, so all detail noise is quantised to a 1/128 m lattice. Keeping u in double
        /// produces visibly different micro-terrain (01-worldgen-core.md 3.1).
        ///
        /// The sea-level squash groups as over * ((1-k) * 0.75). Plains groups the SAME formula as
        /// (over * (1-k)) * 0.75 - see GetPlainsHeight. That difference is real.
        /// </summary>
        private float GetMeadowsHeight(float wx, float wy)
        {
            float wx2 = wx;
            float wy2 = wy;
            float baseHeight = GetBaseHeight(wx, wy, menuTerrain: false);
            wx = (float)((double)wx + 100000.0 + (double)m_offset3);
            wy = (float)((double)wy + 100000.0 + (double)m_offset3);
            double u = wx;
            double v = wy;
            float d = (float)((double)PN(u * 0.009999999776482582, v * 0.009999999776482582) * (double)PN(u * 0.019999999552965164, v * 0.019999999552965164));
            d = (float)((double)d + (double)PN(u * 0.05000000074505806, v * 0.05000000074505806) * (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * (double)d * 0.5);
            float h = baseHeight;
            h = (float)((double)h + (double)d * 0.10000000149011612);
            float sea = 0.15f;
            float over = (float)((double)h - (double)sea);
            float k = (float)DUtils.Clamp01((double)baseHeight / 0.4000000059604645);
            if (over > 0f)
            {
                h = (float)((double)h - (double)over * ((1.0 - (double)k) * 0.75));
            }
            h = AddRivers(wx2, wy2, h);
            h = (float)((double)h + (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * 0.009999999776482582);
            return (float)((double)h + (double)PN(u * 0.4000000059604645, v * 0.4000000059604645) * 0.003000000026077032);
        }

        /// <summary>
        /// WorldGenerator.GetPlainsHeight (decomp 1160-1183) - Plains. Same shape as Meadows with TWO
        /// differences that are both real: 'over' is a plain FLOAT subtraction (h - sea, IL_00ed), and
        /// the squash groups as (over * (1-k)) * 0.75 rather than over * ((1-k) * 0.75).
        /// The float subtraction is numerically identical to Meadows' double-sub-then-round; the grouping
        /// is not (01-worldgen-core.md 6.10).
        /// </summary>
        private float GetPlainsHeight(float wx, float wy)
        {
            float wx2 = wx;
            float wy2 = wy;
            float baseHeight = GetBaseHeight(wx, wy, menuTerrain: false);
            wx = (float)((double)wx + 100000.0 + (double)m_offset3);
            wy = (float)((double)wy + 100000.0 + (double)m_offset3);
            double u = wx;
            double v = wy;
            float d = (float)((double)PN(u * 0.009999999776482582, v * 0.009999999776482582) * (double)PN(u * 0.019999999552965164, v * 0.019999999552965164));
            d = (float)((double)d + (double)PN(u * 0.05000000074505806, v * 0.05000000074505806) * (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * (double)d * 0.5);
            float h = baseHeight;
            h = (float)((double)h + (double)d * 0.10000000149011612);
            float sea = 0.15f;
            float over = h - sea;
            float k = (float)DUtils.Clamp01((double)baseHeight / 0.4000000059604645);
            if (over > 0f)
            {
                h = (float)((double)h - (double)over * (1.0 - (double)k) * 0.75);
            }
            h = AddRivers(wx2, wy2, h);
            h = (float)((double)h + (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * 0.009999999776482582);
            return (float)((double)h + (double)PN(u * 0.4000000059604645, v * 0.4000000059604645) * 0.003000000026077032);
        }

        /// <summary>
        /// WorldGenerator.GetForestHeight (decomp 1114-1129) - BlackForest, and also pre-generation
        /// Mistlands. No sea-level squash.
        /// </summary>
        private float GetForestHeight(float wx, float wy)
        {
            float wx2 = wx;
            float wy2 = wy;
            float h = GetBaseHeight(wx, wy, menuTerrain: false);
            wx = (float)((double)wx + 100000.0 + (double)m_offset3);
            wy = (float)((double)wy + 100000.0 + (double)m_offset3);
            double u = wx;
            double v = wy;
            float d = (float)((double)PN(u * 0.009999999776482582, v * 0.009999999776482582) * (double)PN(u * 0.019999999552965164, v * 0.019999999552965164));
            d = (float)((double)d + (double)PN(u * 0.05000000074505806, v * 0.05000000074505806) * (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * (double)d * 0.5);
            h = (float)((double)h + (double)d * 0.10000000149011612);
            h = AddRivers(wx2, wy2, h);
            h = (float)((double)h + (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * 0.009999999776482582);
            return (float)((double)h + (double)PN(u * 0.4000000059604645, v * 0.4000000059604645) * 0.003000000026077032);
        }

        /// <summary>
        /// WorldGenerator.GetMistlandsHeight (decomp 1131-1158) - Mistlands.
        ///
        /// Two traps: the FIRST product of the M field is a plain FLOAT multiply (no conv.r8 around it),
        /// unlike every other biome's D; and 0.02*0.7 etc. are two separate multiply instructions with
        /// the literals 0.019999999552965164 and 0.699999988079071 - do NOT pre-multiply them.
        ///
        /// mask.a = 1 - 1.2k - (1 - LerpStep(0.1, 0.3, k)) is what suppresses vegetation in thick mist.
        /// The Ceil(h*400)/400 term terraces the ground into 1/400 normalised (0.5 m) steps, blended in
        /// by k.
        /// </summary>
        private float GetMistlandsHeight(float wx, float wy, out ColorRGBA mask)
        {
            float wx2 = wx;
            float wy2 = wy;
            float baseHeight = GetBaseHeight(wx, wy, menuTerrain: false);
            wx = (float)((double)wx + 100000.0 + (double)m_offset3);
            wy = (float)((double)wy + 100000.0 + (double)m_offset3);
            double u = wx;
            double v = wy;
            float m = PN(u * 0.019999999552965164 * 0.699999988079071, v * 0.019999999552965164 * 0.699999988079071) * PN(u * 0.03999999910593033 * 0.699999988079071, v * 0.03999999910593033 * 0.699999988079071);
            m = (float)((double)m + (double)PN(u * 0.029999999329447746 * 0.699999988079071, v * 0.029999999329447746 * 0.699999988079071) * (double)PN(u * 0.05000000074505806 * 0.699999988079071, v * 0.05000000074505806 * 0.699999988079071) * (double)m * 0.5);
            m = ((m > 0f) ? ((float)Math.Pow(m, 1.5)) : m);
            float h = (float)((double)baseHeight + (double)m * 0.4000000059604645);
            h = AddRivers(wx2, wy2, h);
            float k = (float)DUtils.Clamp01((double)m * 7.0);
            h = (float)((double)h + (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * 0.029999999329447746 * (double)k);
            h = (float)((double)h + (double)PN(u * 0.4000000059604645, v * 0.4000000059604645) * 0.009999999776482582 * (double)k);
            float alpha = (float)(1.0 - (double)k * 1.2000000476837158);
            alpha = (float)((double)alpha - (1.0 - (double)DUtils.LerpStep(0.1f, 0.3f, k)));
            float smooth = (float)((double)h + (double)PN(u * 0.4000000059604645, v * 0.4000000059604645) * 0.0020000000949949026);
            float terraced = h;
            terraced = (float)((double)terraced * 400.0);
            terraced = UMathf.Ceil(terraced);
            terraced = (float)((double)terraced / 400.0);
            h = DUtils.Lerp(smooth, terraced, k);
            mask = new ColorRGBA(0f, 0f, 0f, alpha);
            return h;
        }

        /// <summary>
        /// WorldGenerator.GetSnowMountainHeight (decomp 1310-1329) - Mountain.
        /// h + (h - 0.4) is UNCLAMPED here, unlike the Deep North pregeneration version which uses
        /// Mathf.Max(0f, h - 0.4). The final term is the tilt, which costs four extra GetBaseHeight calls.
        /// The menu flag is passed straight through to GetBaseHeight.
        /// </summary>
        private float GetSnowMountainHeight(float wx, float wy, bool menu)
        {
            float wx2 = wx;
            float wy2 = wy;
            float h = GetBaseHeight(wx, wy, menu);
            float tilt = BaseHeightTilt(wx, wy);     // uses the ORIGINAL coordinates
            wx = (float)((double)wx + 100000.0 + (double)m_offset3);
            wy = (float)((double)wy + 100000.0 + (double)m_offset3);
            double u = wx;
            double v = wy;
            float over = (float)((double)h - 0.4000000059604645);
            h = (float)((double)h + (double)over);
            float d = (float)((double)PN(u * 0.009999999776482582, v * 0.009999999776482582) * (double)PN(u * 0.019999999552965164, v * 0.019999999552965164));
            d = (float)((double)d + (double)PN(u * 0.05000000074505806, v * 0.05000000074505806) * (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * (double)d * 0.5);
            h = (float)((double)h + (double)d * 0.20000000298023224);
            h = AddRivers(wx2, wy2, h);
            h = (float)((double)h + (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * 0.009999999776482582);
            h = (float)((double)h + (double)PN(u * 0.4000000059604645, v * 0.4000000059604645) * 0.003000000026077032);
            return (float)((double)h + (double)PN(u * 0.20000000298023224, v * 0.20000000298023224) * 2.0 * (double)tilt);
        }

        /// <summary>WorldGenerator.GetOceanHeight (decomp 1296-1299) - the base height, no detail, NO rivers.</summary>
        private float GetOceanHeight(float wx, float wy) => GetBaseHeight(wx, wy, menuTerrain: false);

        /// <summary>
        /// WorldGenerator.GetDeepNorthHeight (decomp 1355-1383) - Deep North, final.
        /// The +0.1f is a PURE FLOAT add, and k is computed from that SHIFTED base, not from the raw one.
        /// The squash uses the Meadows grouping. mask.g in [0.45, 0.7125] is read by
        /// Heightmap.GetCultivationMask (&gt; 0.5 = "cultivated", i.e. deep snow).
        /// </summary>
        private float GetDeepNorthHeight(float wx, float wy, out ColorRGBA mask)
        {
            float wx2 = wx;
            float wy2 = wy;
            // GetDeepNorthHeight IL_0008 'call GetBaseHeight' / IL_000d 'ldc.r4 0.1' / IL_0012 'add'
            // leaves the sum on the EVALUATION STACK - there is no stloc and no conv.r4 there. It stays
            // there across the whole Perlin block; at IL_00d1 'dup' / IL_00d2 'stloc.s V_5' exactly ONE
            // copy is narrowed to float32 (that copy becomes h below), and at IL_00f9 'conv.r8' the
            // OTHER, still-unnarrowed copy is what gets divided by 0.4 to make k.
            //
            // Unity's Mono keeps every FP evaluation-stack slot at R8 and narrows only at a conv.r4 or a
            // store into a float32 location, so the game's k comes from the EXACT DOUBLE sum while h
            // comes from the float32 one. ILSpy cannot express a stack-only temporary and prints a single
            // float local `num` for both (decomp 1359 and 1370), so the obvious transcription is wrong.
            //
            // Every other biome function stores GetBaseHeight's result immediately (GetMistlandsHeight
            // IL_000d 'stloc.2', GetSnowMountainHeight IL_000d, GetDeepNorthHeightPregenerate IL_000d),
            // which is why only Deep North has this shape.
            double bStack = (double)GetBaseHeight(wx, wy, menuTerrain: false) + (double)0.1f;
            float b = (float)bStack;   // IL_00d2 'stloc.s V_5' - the narrowed copy only
            wx = (float)((double)wx + 100000.0 + (double)m_offset3);
            wy = (float)((double)wy + 100000.0 + (double)m_offset3);
            double u = wx;
            double v = wy;
            float d = (float)((double)PN(u * 0.009999999776482582, v * 0.009999999776482582) * (double)PN(u * 0.019999999552965164, v * 0.019999999552965164));
            d = (float)((double)d + (double)PN(u * 0.05000000074505806, v * 0.05000000074505806) * (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * (double)d * 0.5);
            float h = b;
            h = (float)((double)h + (double)d * 0.10000000149011612);
            float sea = 0.15f;
            float over = (float)((double)h - (double)sea);
            // IL_00f9 'conv.r8' consumes the un-narrowed stack copy (see the note on bStack above), NOT
            // the float32 b that fed h. Passing b here is what ILSpy printed and what costs ~2.5 % of
            // Deep North samples one float ULP.
            float k = (float)DUtils.Clamp01(bStack / 0.4000000059604645);
            if (over > 0f)
            {
                h = (float)((double)h - (double)over * ((1.0 - (double)k) * 0.75));
            }
            h = AddRivers(wx2, wy2, h);
            h = (float)((double)h + (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * 0.009999999776482582);
            h = (float)((double)h + (double)PN(u * 0.4000000059604645, v * 0.4000000059604645) * 0.003000000026077032);
            // The DOUBLE Fbm overload, with coordinates already truncated to float by the Vector2 ctor.
            float g = (float)DUtils.Fbm(new Vec2((float)(u * 0.009999999776482582), (float)(v * 0.009999999776482582)), 3, 2.0, 0.5);
            // IL_01d5-IL_01e3: 'add' then 'div' with one narrowing, at 'stloc.s V_9'. Halving is exact
            // and commutes with rounding, and (g + 1f) for g in [-1, 1] is exact in double, so this
            // statement gives the same answer either way; written R8 anyway for uniformity.
            g = (float)(((double)g + (double)1f) / (double)2f);
            // IL_01e5 'ldc.r4 0.3' / IL_01ea 'ldloc.s V_9' / IL_01ec 'ldc.r4 0.3' / IL_01f1 'mul' /
            // IL_01f2 'add' / IL_01f3 'stloc.s V_9' - the multiply AND the add run on the evaluation
            // stack at R8 with a single narrowing at the store, the same Mono rule as bStack above.
            // A float chain rounds twice and can differ by one ULP.
            //
            // NOT verifiable against the ground truth: mask.g is not part of the returned height and the
            // minimap cache does not store it, so no oracle can see this. It is corrected on the IL plus
            // the Mono model that the GetDeepNorthHeight/Vec2 fixes confirmed empirically. It matters
            // only to Heightmap.GetCultivationMask's `> 0.5` test (deep-snow coverage).
            g = (float)((double)0.3f + (double)g * (double)0.3f);
            mask = new ColorRGBA(0f, g, 0f, 0f);
            return h;
        }

        /// <summary>
        /// WorldGenerator.GetDeepNorthHeightPregenerate (decomp 1331-1353) - pre-generation only.
        /// riverPregen == false (stream pass 2) adds the +0.1f; pass 1 does not.
        ///
        /// The last two noise terms are the ONLY place in the whole file that uses the FLOAT
        /// PerlinNoise(float,float) overload with float-multiplied coordinates (wx*0.1f, wx*0.4f);
        /// everywhere else the multiply is in double. That, plus the +0.1f and the *1.2, is why the
        /// Deep North pregeneration height differs from its final height.
        /// </summary>
        private float GetDeepNorthHeightPregenerate(float wx, float wy, bool riverPregen)
        {
            float wx2 = wx;
            float wy2 = wy;
            float h = GetBaseHeight(wx, wy, menuTerrain: false);
            if (!riverPregen)
            {
                h += 0.1f;
            }
            wx = (float)((double)wx + 100000.0 + (double)m_offset3);
            wy = (float)((double)wy + 100000.0 + (double)m_offset3);
            double u = wx;
            double v = wy;
            float over = UMathf.Max(0f, (float)((double)h - 0.4000000059604645));
            h = (float)((double)h + (double)over);
            float d = (float)((double)PN(u * 0.009999999776482582, v * 0.009999999776482582) * (double)PN(u * 0.019999999552965164, v * 0.019999999552965164));
            d = (float)((double)d + (double)PN(u * 0.05000000074505806, v * 0.05000000074505806) * (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * (double)d * 0.5);
            h = (float)((double)h + (double)d * 0.20000000298023224);
            h = (float)((double)h * 1.2000000476837158);
            h = AddRivers(wx2, wy2, h);
            h = (float)((double)h + (double)DUtils.PerlinNoise(wx * 0.1f, wy * 0.1f) * 0.009999999776482582);
            return (float)((double)h + (double)DUtils.PerlinNoise(wx * 0.4f, wy * 0.4f) * 0.003000000026077032);
        }

        /// <summary>
        /// WorldGenerator.GetAshlandsHeightPregenerate (decomp 1197-1213) - pre-generation only.
        /// AddRivers comes LAST here, after the fine noise, unlike every other biome.
        /// </summary>
        private float GetAshlandsHeightPregenerate(float wx, float wy)
        {
            float wx2 = wx;
            float wy2 = wy;
            float h = GetBaseHeight(wx, wy, menuTerrain: false);
            wx = (float)((double)wx + 100000.0 + (double)m_offset3);
            wy = (float)((double)wy + 100000.0 + (double)m_offset3);
            double u = wx;
            double v = wy;
            float d = (float)((double)PN(u * 0.009999999776482582, v * 0.009999999776482582) * (double)PN(u * 0.019999999552965164, v * 0.019999999552965164));
            d = (float)((double)d + (double)PN(u * 0.05000000074505806, v * 0.05000000074505806) * (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * (double)d * 0.5);
            h = (float)((double)h + (double)d * 0.10000000149011612);
            h = (float)((double)h + 0.10000000149011612);
            h = (float)((double)h + (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * 0.009999999776482582);
            h = (float)((double)h + (double)PN(u * 0.4000000059604645, v * 0.4000000059604645) * 0.003000000026077032);
            return AddRivers(wx2, wy2, h);
        }

        /// <summary>
        /// WorldGenerator.GetMenuHeight (decomp 1185-1195) - menu world only. No rivers, no squash, and
        /// the first product is a FLOAT multiply like Mistlands'.
        /// </summary>
        private float GetMenuHeight(float wx, float wy)
        {
            float b = GetBaseHeight(wx, wy, menuTerrain: true);
            wx = (float)((double)wx + 100000.0 + (double)m_offset3);
            wy = (float)((double)wy + 100000.0 + (double)m_offset3);
            double u = wx;
            double v = wy;
            float d = PN(u * 0.009999999776482582, v * 0.009999999776482582) * PN(u * 0.019999999552965164, v * 0.019999999552965164);
            d = (float)((double)d + (double)PN(u * 0.05000000074505806, v * 0.05000000074505806) * (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * (double)d * 0.5);
            return (float)((double)(float)((double)(float)((double)b + (double)d * 0.10000000149011612) + (double)PN(u * 0.10000000149011612, v * 0.10000000149011612) * 0.009999999776482582) + (double)PN(u * 0.4000000059604645, v * 0.4000000059604645) * 0.003000000026077032);
        }

        /// <summary>
        /// WorldGenerator.GetAshlandsHeight (decomp 1215-1279) - AshLands, final. The ONLY height
        /// function that works in double end to end; its coordinates are never truncated to float except
        /// for the (100000f + m_offset3) addend, which is a FLOAT add before widening.
        ///
        /// Structure: a ridge band around a circle 1200 m SOUTH of the AshLands ring (centre moves from
        /// (0, +4000) to (0, +2800), so at x = 0 the crest sits near z = -9200 against a biome boundary
        /// near z = -8000), fattened with 5 octaves of cellular noise; an ocean edge fade at 10150 m;
        /// a simplex-fractal multiply; then a lava mask built from an fbm x cellular overlay that carves
        /// the ground down into lava pools. mask.a is the lava amount, which is what
        /// ZoneSystem.IsLavaPreHeightmap tests against 0.6.
        ///
        /// Minimap passes cheap: true (2/2 octaves instead of 5/3); HeightmapBuilder, GetBiomeHeight and
        /// ZoneSystem.IsLavaPreHeightmap pass false.
        ///
        /// DEAD CODE, deliberately omitted (decomp 1244-1245): between the ridge and the height blend the
        /// game computes
        ///     double num11 = P(x*0.01, y*0.01) * P(x*0.02, y*0.02);
        ///     num11 += (double)(P(x*0.05, y*0.05) * P(x*0.1, y*0.1)) * num11 * 0.5;
        /// into a local that nothing ever reads. Mathf.PerlinNoise is pure, so dropping the four calls is
        /// observationally equivalent and saves real time on the AshLands cap.
        /// </summary>
        public float GetAshlandsHeight(float wx, float wy, out ColorRGBA mask, bool cheap = false)
        {
            double x = wx;
            double y = wy;
            double a = GetBaseHeight((float)x, (float)y, menuTerrain: false);
            double angle = (double)WorldAngle((float)x, (float)y) * 100.0;

            // y + ashlandsYOffset - ashlandsYOffset*0.3 == y - 2800 : the ridge circle, 1200 m south of
            // the biome ring. Note this is the DOUBLE Length overload.
            double ridge = DUtils.Length(x, y + (double)AshlandsYOffset - (double)AshlandsYOffset * 0.3) - ((double)AshlandsMinDistance + angle);
            ridge = Math.Abs(ridge) / 1000.0;
            ridge = 1.0 - DUtils.Clamp01(ridge);
            ridge = DUtils.MathfLikeSmoothStep(0.1, 1.0, ridge);
            double xFade = Math.Abs(x);
            xFade = 1.0 - DUtils.Clamp01(xFade / 7500.0);
            ridge *= xFade;

            double edge = DUtils.Length(x, y) - 10150.0;
            edge = 1.0 - DUtils.Clamp01(edge / 600.0);

            // FLOAT add of the offset, then widened (GetAshlandsHeight / IL_00f0-IL_00fc).
            x += (double)(100000f + m_offset3);
            y += (double)(100000f + m_offset3);

            double cell = 0.0;
            double amp = 1.0;
            double freq = 0.33000001311302185;
            int octaves = (cheap ? 2 : 5);
            for (int i = 0; i < octaves; i++)
            {
                cell += amp * DUtils.MathfLikeSmoothStep(0.0, 1.0, m_noiseGen.GetCellular(x * freq, y * freq));
                freq *= 2.0;
                amp *= 0.5;
            }
            cell = DUtils.Remap(cell, -1.0, 1.0, 0.0, 1.0);
            double ridgeBlend = DUtils.Lerp(ridge, DUtils.BlendOverlay(ridge, cell), 0.5);

            double h = DUtils.Lerp(a, 0.15000000596046448, 0.75);
            h += ridgeBlend * 0.5;
            h = DUtils.Lerp(-1.0, h, DUtils.MathfLikeSmoothStep(0.0, 1.0, edge));

            double seaLevel = 0.15;
            double lavaCell = 0.0;
            amp = 1.0;
            freq = 8.0;
            int lavaOctaves = (cheap ? 2 : 3);
            for (int j = 0; j < lavaOctaves; j++)
            {
                lavaCell += amp * m_noiseGen.GetCellular(x * freq, y * freq);
                freq *= 2.0;
                amp *= 0.5;
            }
            lavaCell = DUtils.Remap(lavaCell, -1.0, 1.0, 0.0, 1.0);
            lavaCell = DUtils.Clamp01(Math.Pow(lavaCell, 4.0) * 2.0);

            double simplex = m_noiseGen.GetSimplexFractal(x * 0.075, y * 0.075);
            simplex = DUtils.Remap(simplex, -1.0, 1.0, 0.0, 1.0);
            simplex = Math.Pow(simplex, 1.399999976158142);
            h *= simplex;

            double f = DUtils.Fbm(new Vec2((float)(x * 0.009999999776482582), (float)(y * 0.009999999776482582)), 3, 2.0, 0.5);
            f *= DUtils.Clamp01(DUtils.Remap(ridge, 0.0, 0.5, 0.5, 1.0));
            f = DUtils.LerpStep(0.699999988079071, 1.0, f);     // the DOUBLE LerpStep, used only here
            f = Math.Pow(f, 2.0);
            double lava = DUtils.BlendOverlay(f, lavaCell);
            lava *= DUtils.Clamp01((h - seaLevel - 0.02) / 0.01);

            double dip = (double)PN(x * 0.05 + 5124.0, y * 0.05 + 5000.0);
            dip = Math.Pow(dip, 2.0);
            dip = DUtils.Remap(dip, 0.0, 1.0, 0.009999999776482582, 0.054999999701976776);
            double clamped = UMathf.Clamp((float)(h - dip), (float)(seaLevel + 0.009999999776482582), 5000f);
            h = DUtils.Lerp(h, clamped, lava);
            mask = new ColorRGBA(0f, 0f, 0f, (float)lava);
            return (float)h;
        }

        // ---------------------------------------------------------------------------------------------
        // Misc helpers
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// WorldGenerator.GetForestFactor (decomp 1412-1416). Static and SEEDLESS - the forest density
        /// pattern is identical in every world. pos * 0.01f * 0.4f is two Vector3 scalar multiplies, so
        /// each component is (c * 0.01f) * 0.4f in float; Fbm(Vector3) then uses (x, z).
        /// Range is roughly [0, 2.19]; Minimap uses 0.8 (Meadows) and SmoothStep(1.1, 1.3) (Mistlands).
        /// </summary>
        public static float GetForestFactor(float x, float y, float z)
        {
            float k = 0.4f;
            return DUtils.Fbm((x * 0.01f) * k, (z * 0.01f) * k, 3, 1.6f, 0.7f);
        }

        /// <summary>WorldGenerator.InForest (decomp 1407-1410).</summary>
        public static bool InForest(float x, float y, float z) => GetForestFactor(x, y, z) < 1.15f;

        /// <summary>
        /// WorldGenerator.GetNormal (decomp 979-986). The radius argument is NEVER loaded - the samples
        /// are always +/-1 m (GetNormal / IL: ldarg.2 is unused). Costs four full GetHeight calls.
        /// Returns the normalised (x, y, z).
        /// </summary>
        public (float x, float y, float z) GetNormal(float wx, float wy)
        {
            float hr = GetHeight(wx + 1f, wy);
            float hl = GetHeight(wx - 1f, wy);
            float hu = GetHeight(wx, wy + 1f);
            float hd = GetHeight(wx, wy - 1f);
            float nx = 2f * (hr - hl);
            float ny = 4f;
            float nz = 2f * (hd - hu);
            // Vector3.normalized: magnitude is a float sum widened only for the sqrt, and the guard is
            // magnitude > 1e-05f.
            float mag = (float)Math.Sqrt((double)(nx * nx + ny * ny + nz * nz));
            if (mag > 1E-05f) return (nx / mag, ny / mag, nz / mag);
            return (0f, 0f, 0f);
        }

        /// <summary>
        /// WorldGenerator.GetTerrainDelta (decomp 1418-1443). Used by LOCATION PLACEMENT, not by terrain
        /// generation, and it draws from whatever ambient Random stream the caller set up - hence the
        /// explicit generator argument here.
        ///
        /// Random.insideUnitCircle is an extern; UnityRandom.InsideUnitCircle is a disassembly-based
        /// reconstruction that the dumper has not yet confirmed in the last bit (item D8). Anything that
        /// needs this bit-exactly should be validated against dumped game values first.
        /// </summary>
        public void GetTerrainDelta(UnityRandom rnd, float cx, float cy, float cz, float radius,
                                    out float delta, out (float x, float y, float z) slopeDirection)
        {
            const int n = 10;
            float hi = -999999f;
            float lo = 999999f;
            float hiX = cx, hiY = cy, hiZ = cz;
            float loX = cx, loY = cy, loZ = cz;
            for (int i = 0; i < n; i++)
            {
                (float rx, float ry) = rnd.InsideUnitCircle();
                float qx = cx + rx * radius;
                float qz = cz + ry * radius;
                float h = GetHeight(qx, qz);
                if (h < lo) { lo = h; loX = qx; loY = cy; loZ = qz; }
                if (h > hi) { hi = h; hiX = qx; hiY = cy; hiZ = qz; }
            }
            delta = (float)((double)hi - (double)lo);
            float dx = loX - hiX;
            float dy = loY - hiY;
            float dz = loZ - hiZ;
            float mag = (float)Math.Sqrt((double)(dx * dx + dy * dy + dz * dz));
            if (mag > 1E-05f) slopeDirection = (dx / mag, dy / mag, dz / mag);
            else slopeDirection = (0f, 0f, 0f);
        }
    }
}
