using System;
using SeedLab.Runtime.Execution;

namespace SeedLab.Runtime.Estimation
{
    /// <summary>The output format a run will write. Drives record size and compression only.</summary>
    public enum OutputFormat
    {
        Jsonl = 0,
        JsonlGz = 1,
        Csv = 2,

        /// <summary>A single JSON array. Cannot be rotated - a split array is not valid JSON.</summary>
        Json = 3,

        /// <summary>Nothing is written (a count, a dry run, the GUI's preview).</summary>
        None = 4
    }

    /// <summary>
    /// Everything the estimator needs. Deliberately a flat bag of the numbers the caller already has,
    /// so the CLI and the web UI build it identically from the same query JSON.
    /// </summary>
    public sealed class EstimateInputs
    {
        /// <summary>How many seeds this run will evaluate. 4,294,967,296 for the whole space.</summary>
        public long Seeds { get; set; }

        /// <summary>The most expensive tier any goal needs - that is what the run costs.</summary>
        public WorkTier Tier { get; set; } = WorkTier.BiomeGrid;

        /// <summary>Samples per seed. <see cref="WorkerFootprints.CellsForSpacing"/> turns a spacing into this.</summary>
        public long GridCells { get; set; } = WorkerFootprints.CellsForSpacing(384);

        /// <summary>Workers, as the <see cref="WorkerPlan"/> decided - not the core count.</summary>
        public int Threads { get; set; } = 1;

        /// <summary>
        /// Workers that will have a block to compute, when fewer than <see cref="Threads"/>; 0 means
        /// all of them. Used for the RATE only.
        ///
        /// <para>One worker computes a whole block, so a run cut into fewer blocks than workers runs at
        /// the parallelism of its block count, and a rate projected for every worker is a rate the run
        /// cannot reach. It is not folded into <see cref="Threads"/> because that also scales the
        /// memory figure, and memory follows every worker: each thread is started and builds its
        /// evaluator and grid buffers before it tries to claim a block, busy or not (2026-09-24).</para>
        /// </summary>
        public int BusyWorkers { get; set; }

        public OutputFormat Format { get; set; } = OutputFormat.Jsonl;

        /// <summary>Goals in the query: what decides record size, per the disk audit.</summary>
        public int GoalCount { get; set; }

        /// <summary>
        /// The bounded cap (<c>--keep N</c>), or null for <c>--keep all</c>. In bounded mode this is a
        /// REAL cap on the file, so disk is O(N) and independent of how many seeds are scanned.
        /// </summary>
        public long? KeepN { get; set; } = 1000;

        /// <summary>
        /// The fraction of seeds expected to match, when anything is known. Null means unknown, and an
        /// unbounded run with an unknown hit rate is reported as what it is: unpredictable, worst case
        /// every seed.
        /// </summary>
        public double? ExpectedHitRate { get; set; }

        /// <summary>Free bytes on the output volume (<see cref="Hardware.VolumeInfo.For"/>).</summary>
        public long FreeSpaceBytes { get; set; }

        /// <summary>Available RAM, as the probe reported it.</summary>
        public long AvailableMemoryBytes { get; set; }

        /// <summary>Per-worker footprint; defaults to the catalogue for the tier and grid when null.</summary>
        public WorkerFootprint? Footprint { get; set; }

        /// <summary>The user passed <c>--all</c>: always a confirmation, whatever the numbers say.</summary>
        public bool AllSeedsFlag { get; set; }

        /// <summary>A whole-space run, for the "% of 2^32" line.</summary>
        public const long WholeSeedSpace = 4_294_967_296L;

        public EstimateInputs Clone() => (EstimateInputs)MemberwiseClone();
    }

    /// <summary>What the caller should do with an estimate.</summary>
    public enum EstimateVerdict
    {
        /// <summary>Start.</summary>
        Ok = 0,

        /// <summary>Start only after an explicit confirmation (or <c>--yes</c>).</summary>
        Confirm = 1,

        /// <summary>Do not start. The reasons carry the arithmetic.</summary>
        Refuse = 2
    }
}
