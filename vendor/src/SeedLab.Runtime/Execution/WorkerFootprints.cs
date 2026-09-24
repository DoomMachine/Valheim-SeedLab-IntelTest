using System;
using System.Globalization;
using SeedLab.Runtime.Hardware;

namespace SeedLab.Runtime.Execution
{
    /// <summary>
    /// What one worker holds while it evaluates a seed, as a range with the measurement it came from.
    /// The planner takes this as a PARAMETER: the numbers below are this session's measurements of the
    /// current engine, and the engine is free to hand the planner a better one at any time.
    /// </summary>
    public readonly struct WorkerFootprint
    {
        public WorkerFootprint(long lowBytes, long highBytes, string source)
        {
            if (lowBytes < 0) lowBytes = 0;
            if (highBytes < lowBytes) highBytes = lowBytes;
            LowBytes = lowBytes;
            HighBytes = highBytes;
            Source = source ?? "";
        }

        public WorkerFootprint(long bytes, string source) : this(bytes, bytes, source) { }

        public long LowBytes { get; }

        /// <summary>The number the memory guard plans against: a guard that uses the optimistic end is not a guard.</summary>
        public long HighBytes { get; }

        public string Source { get; }

        public bool IsRange => HighBytes != LowBytes;

        public override string ToString() =>
            (IsRange ? Bytes.Human(LowBytes) + " - " + Bytes.Human(HighBytes) : Bytes.Human(HighBytes))
            + " per worker (" + Source + ")";
    }

    /// <summary>The work a seed is being put through - what decides both cost and footprint.</summary>
    public enum WorkTier
    {
        /// <summary>Biome-only criteria sampled on a grid. The cheap tier the funnel starts with.</summary>
        BiomeGrid = 0,

        /// <summary>Terrain height and the river/lake pre-generation, sampled on a grid.</summary>
        HeightsRivers = 1,

        /// <summary>Bosses, traders and the handful of ordered location groups.</summary>
        LocationsCore = 2,

        /// <summary>The full 183-entry location placement.</summary>
        LocationsAll = 3
    }

    /// <summary>
    /// The measured per-worker footprints, one place, each with its provenance. Nothing else in the
    /// layer hard-codes a memory number.
    /// </summary>
    public static class WorkerFootprints
    {
        // Two measured anchors for the grid tiers, from this session's profiling:
        //   G384 (3,136 cells on the engine's grid law) ~= 30 KiB per worker
        //   G12  (4,194,304 cells)                      ~= 24 MiB per worker
        // The straight line through them is 5.997 B per cell + 11.9 KiB of fixed per-worker state.
        // It is a two-point fit, not a theory: outside [G12, G384] it is an extrapolation and says so.
        public const double GridBytesPerCell = 5.99716;
        public const long GridFixedBytes = 11_913;
        public const long GridAnchorFineCells = 2048L * 2048L;
        public const long GridAnchorCoarseCells = 56L * 56L;

        /// <summary>50-60 MB allocated per seed at the height/river tier (measured this session).</summary>
        public const long HeightTierLowBytes = 50L * 1000L * 1000L;
        public const long HeightTierHighBytes = 60L * 1000L * 1000L;

        /// <summary>~65 MB for the location tier (measured this session).</summary>
        public const long LocationTierBytes = 65L * 1000L * 1000L;

        /// <summary>
        /// The per-worker footprint for a tier and grid. <paramref name="gridCells"/> is the number of
        /// sample points per seed (<c>size * size</c>); it is ignored by the location tiers, whose
        /// placement runs on the game's own hard-coded 2048^2 grid whatever the query asks for.
        /// </summary>
        public static WorkerFootprint For(WorkTier tier, long gridCells)
        {
            if (gridCells < 1) gridCells = 1;

            switch (tier)
            {
                case WorkTier.BiomeGrid:
                {
                    long b = GridBytes(gridCells);
                    string src = Provenance(gridCells, "biome grid buffers");
                    return new WorkerFootprint(b, b, src);
                }
                case WorkTier.HeightsRivers:
                {
                    // Grid buffers plus the height tier's own per-seed allocation.
                    long grid = GridBytes(gridCells);
                    return new WorkerFootprint(grid + HeightTierLowBytes, grid + HeightTierHighBytes,
                        "measured 2026-09-23: 50-60 MB per seed at the height tier, plus "
                        + Bytes.Human(grid) + " of grid buffers");
                }
                case WorkTier.LocationsCore:
                case WorkTier.LocationsAll:
                    return new WorkerFootprint(LocationTierBytes, LocationTierBytes,
                        "measured 2026-09-23: ~65 MB per worker for the location tier (its own 2048^2 grid)");
                default:
                    return new WorkerFootprint(GridBytes(gridCells), "grid buffers");
            }
        }

        public static long GridBytes(long gridCells) =>
            (long)Math.Round(GridBytesPerCell * gridCells) + GridFixedBytes;

        private static string Provenance(long cells, string what)
        {
            bool interpolated = cells >= GridAnchorCoarseCells && cells <= GridAnchorFineCells;
            return "measured 2026-09-23 at G384 (~30 KiB) and G12 (~24 MiB), "
                   + (interpolated ? "interpolated" : "EXTRAPOLATED") + " to "
                   + Bytes.Count(cells) + " cells of " + what;
        }

        /// <summary>
        /// Samples per seed for a grid spacing, mirroring
        /// <c>SeedLab.Search.Evaluation.SearchGrids.ForSpacing</c>: <c>N = 2 * ceil(10500 / r)</c>, with
        /// <c>r = 12</c> special-cased to the game's own 2048. Mirrored rather than referenced so this
        /// project keeps zero SeedLab dependencies; the estimator only ever uses RATIOS of cell counts,
        /// so a caller that passes its own cell count directly gets the same answer.
        /// </summary>
        public static long CellsForSpacing(double spacingMetres)
        {
            if (!(spacingMetres > 0) || !double.IsFinite(spacingMetres))
                throw new ArgumentOutOfRangeException(nameof(spacingMetres), spacingMetres, "Grid spacing must be positive.");
            if (spacingMetres == 12.0) return 2048L * 2048L;
            int n = 2 * (int)Math.Ceiling(10500.0 / spacingMetres);
            if (n < 2) n = 2;
            return (long)n * n;
        }
    }
}
