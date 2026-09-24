using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Runtime.Execution;

namespace SeedLab.Runtime.Estimation
{
    /// <summary>
    /// A constant the estimate rested on, with where it came from. Every number the estimator prints
    /// can be traced to one of these, and each says plainly whether it was MEASURED or ASSUMED.
    /// </summary>
    public sealed class ConstantUse
    {
        public ConstantUse(string name, double value, string unit, string source, bool measured)
        {
            Name = name; Value = value; Unit = unit; Source = source; Measured = measured;
        }

        public string Name { get; }
        public double Value { get; }
        public string Unit { get; }
        public string Source { get; }

        /// <summary>False means the value is an assumption; it is labelled as one wherever it is shown.</summary>
        public bool Measured { get; }

        public override string ToString() =>
            Name + " = " + Value.ToString("G6", CultureInfo.InvariantCulture) + " " + Unit
            + "  [" + (Measured ? "measured" : "ASSUMED") + ": " + Source + "]";
    }

    /// <summary>
    /// The cost constants, in one place, with their provenance.
    ///
    /// <para>Measured on DoomMachine's machine (Ryzen 9800X3D, 16 logical cores, .NET 10) on
    /// 2026-09-23. They are the DEFAULTS, not the truth about anyone else's machine: the estimator
    /// prefers live measurements as soon as <see cref="Calibration"/> has any
    /// (<see cref="Estimator.Project"/>), which is how a slow laptop stops being estimated as if it were
    /// this one after the first slice.</para>
    /// </summary>
    public static class CostModel
    {
        public const int AnchorThreads = 16;
        public const string AnchorMachine = "Ryzen 9800X3D, 16 logical cores, .NET 10, 2026-09-23";

        /// <summary>Seeds per second at 16 threads, biome criteria on the 384 m grid.</summary>
        public const double BiomeG384SeedsPerSecond = 10_717.0;

        /// <summary>Seeds per second at 16 threads, biome criteria on the game's own 12 m grid.</summary>
        public const double BiomeG12SeedsPerSecond = 18.2;

        /// <summary>Seeds per second at 16 threads, heights and rivers on the 384 m grid.</summary>
        public const double HeightsG384SeedsPerSecond = 28.6;

        /// <summary>Bosses and traders: about one second of ONE thread per seed.</summary>
        public const double LocationsCoreSecondsPerSeedPerThread = 1.0;

        /// <summary>All 183 location types: about seven seconds of ONE thread per seed.</summary>
        public const double LocationsAllSecondsPerSeedPerThread = 7.0;

        /// <summary>Parallel efficiency at 16 threads for the compute-bound coarse-grid tiers.</summary>
        public const double EfficiencyAt16Compute = 0.84;

        /// <summary>Parallel efficiency at 16 threads for the memory-bound fine-grid and location tiers.</summary>
        public const double EfficiencyAt16Memory = 0.56;

        /// <summary>Cells at the two grid anchors, on the engine's grid law.</summary>
        public const long CoarseAnchorCells = 56L * 56L;              // G384
        public const long FineAnchorCells = 2048L * 2048L;            // G12

        /// <summary>
        /// The +/- factor an estimate is widened by before any live measurement exists. An assumption:
        /// it stands for seed-to-seed spread and for this machine not being that machine. Calibration
        /// replaces it with the observed spread after the first slices.
        /// </summary>
        public const double UncalibratedBandInterpolated = 1.25;
        public const double UncalibratedBandExtrapolated = 1.60;

        /// <summary>Record sizes, from the disk audit: JSONL ~= 54 + 150.5 x goals bytes.</summary>
        public const double JsonlBaseBytes = 54.0;
        public const double JsonlBytesPerGoal = 150.5;
        public const double CsvBaseBytes = 75.0;
        public const double CsvBytesPerGoal = 28.0;

        /// <summary>A JSON array record measured 1,712.6 B against JSONL's 1,709.6 B: the same plus 3 B.</summary>
        public const double JsonArrayExtraBytes = 3.0;

        /// <summary>gzip on JSONL: 8x to 15x, from the disk audit.</summary>
        public const double GzipRatioLow = 8.0;
        public const double GzipRatioHigh = 15.0;

        /// <summary>Runtime, buffers and the writer, outside the workers. An assumption, deliberately generous.</summary>
        public const long BaseProcessBytes = 64L * 1024 * 1024;

        /// <summary>In-memory cost of one record in the bounded best-N heap, beyond its serialised bytes.</summary>
        public const long HeapRecordOverheadBytes = 96;

        /// <summary>Single-thread seconds per seed at a tier and grid, with how it was arrived at.</summary>
        public static (double seconds, bool extrapolated, string how) SingleThreadSeconds(WorkTier tier, long gridCells)
        {
            switch (tier)
            {
                case WorkTier.LocationsCore:
                    return (LocationsCoreSecondsPerSeedPerThread, false,
                        "measured: ~1 s per seed per thread for bosses and traders");
                case WorkTier.LocationsAll:
                    return (LocationsAllSecondsPerSeedPerThread, false,
                        "measured: ~7 s per seed per thread for all 183 location types");

                case WorkTier.BiomeGrid:
                {
                    double cCoarse = AnchorThreads * EfficiencyAt16Compute / BiomeG384SeedsPerSecond;
                    double cFine = AnchorThreads * EfficiencyAt16Memory / BiomeG12SeedsPerSecond;
                    return GridCost(cCoarse, cFine, gridCells, "biome tier");
                }

                case WorkTier.HeightsRivers:
                {
                    double cCoarse = AnchorThreads * EfficiencyAt16Compute / HeightsG384SeedsPerSecond;
                    // Only one grid anchor exists for this tier, so the biome tier's grid exponent is
                    // borrowed. That is an assumption and is reported as one.
                    double k = GridExponent();
                    double c = cCoarse * Math.Pow((double)gridCells / CoarseAnchorCells, k);
                    bool extra = gridCells < CoarseAnchorCells || gridCells > FineAnchorCells;
                    return (c, true,
                        "measured at G384 (28.6 seeds/s at 16 threads); scaled by the biome tier's grid "
                        + "exponent " + k.ToString("0.###", CultureInfo.InvariantCulture)
                        + (extra ? " and EXTRAPOLATED past the measured grids" : " (ASSUMED to carry over)"));
                }
                default:
                    return (0.0, true, "unknown tier");
            }
        }

        private static (double, bool, string) GridCost(double coarse, double fine, long cells, string what)
        {
            double k = GridExponent();
            double c = coarse * Math.Pow((double)cells / CoarseAnchorCells, k);
            bool extrapolated = cells < CoarseAnchorCells || cells > FineAnchorCells;
            string how = "two measured anchors for the " + what
                + " (G384 and G12 at 16 threads), cost ~ cells^"
                + k.ToString("0.####", CultureInfo.InvariantCulture)
                + (extrapolated ? ", EXTRAPOLATED beyond them" : ", interpolated between them");
            return (c, extrapolated, how);
        }

        /// <summary>
        /// The exponent of the two-point power law through the grid anchors: cost grows as
        /// cells^0.83, not linearly - the fine grids get more out of each cell than the coarse ones do.
        /// </summary>
        public static double GridExponent()
        {
            double cCoarse = AnchorThreads * EfficiencyAt16Compute / BiomeG384SeedsPerSecond;
            double cFine = AnchorThreads * EfficiencyAt16Memory / BiomeG12SeedsPerSecond;
            return Math.Log(cFine / cCoarse) / Math.Log((double)FineAnchorCells / CoarseAnchorCells);
        }

        /// <summary>
        /// Parallel efficiency at a thread count, from a contention model fitted to the measured 16-thread
        /// points: eff(t) = 1 / (1 + (t-1) * s). The grid tiers slide from the compute-bound slope at
        /// G384 to the memory-bound slope at G12 in log(cells); the location tiers are memory-bound.
        /// </summary>
        public static double Efficiency(WorkTier tier, long gridCells, int threads)
        {
            if (threads <= 1) return 1.0;
            double s = Slope(tier, gridCells);
            return 1.0 / (1.0 + (threads - 1) * s);
        }

        private static double Slope(WorkTier tier, long gridCells)
        {
            double sCompute = (1.0 / EfficiencyAt16Compute - 1.0) / (AnchorThreads - 1);
            double sMemory = (1.0 / EfficiencyAt16Memory - 1.0) / (AnchorThreads - 1);

            if (tier == WorkTier.LocationsCore || tier == WorkTier.LocationsAll) return sMemory;

            double lo = Math.Log(CoarseAnchorCells), hi = Math.Log(FineAnchorCells);
            double x = Math.Log(Math.Max(1L, gridCells));
            double t = (x - lo) / (hi - lo);
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return sCompute + (sMemory - sCompute) * t;
        }

        /// <summary>Seeds per second at a thread count, before any calibration.</summary>
        public static double SeedsPerSecond(WorkTier tier, long gridCells, int threads)
        {
            if (threads < 1) threads = 1;
            (double c, _, _) = SingleThreadSeconds(tier, gridCells);
            if (c <= 0) return 0;
            return threads * Efficiency(tier, gridCells, threads) / c;
        }

        /// <summary>Bytes one result record takes in a format, given the goal count.</summary>
        public static double RecordBytes(OutputFormat format, int goalCount)
        {
            int g = Math.Max(0, goalCount);
            switch (format)
            {
                case OutputFormat.Csv: return CsvBaseBytes + CsvBytesPerGoal * g;
                case OutputFormat.Json: return JsonlBaseBytes + JsonlBytesPerGoal * g + JsonArrayExtraBytes;
                case OutputFormat.Jsonl:
                case OutputFormat.JsonlGz:
                default: return JsonlBaseBytes + JsonlBytesPerGoal * g;
            }
        }

        public static IReadOnlyList<ConstantUse> Provenance(WorkTier tier, long gridCells, int threads,
                                                            OutputFormat format, int goalCount)
        {
            (double c, bool extrapolated, string how) = SingleThreadSeconds(tier, gridCells);
            List<ConstantUse> l = new List<ConstantUse>
            {
                new ConstantUse("per-seed cost, one thread", c, "s", how + " on " + AnchorMachine, !extrapolated),
                new ConstantUse("parallel efficiency at " + threads + " threads",
                    Efficiency(tier, gridCells, threads), "",
                    "fitted to the measured 16-thread scaling (" + (EfficiencyAt16Compute * 100).ToString("0")
                    + " % compute-bound, " + (EfficiencyAt16Memory * 100).ToString("0") + " % memory-bound)",
                    threads == AnchorThreads),
                new ConstantUse("record size", RecordBytes(format, goalCount), "B",
                    "disk audit: " + (format == OutputFormat.Csv ? "75 + 28 x goals" : "54 + 150.5 x goals")
                    + " at " + goalCount + " goals", true),
                new ConstantUse("grid cells", gridCells, "samples/seed", "the query's sampling grid", true)
            };
            if (format == OutputFormat.JsonlGz)
            {
                l.Add(new ConstantUse("gzip ratio", GzipRatioLow, "x (low)", "disk audit: JSONL compresses 8-15x", true));
                l.Add(new ConstantUse("gzip ratio", GzipRatioHigh, "x (high)", "disk audit: JSONL compresses 8-15x", true));
            }
            return l;
        }
    }
}
