using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Hardware;

namespace SeedLab.Runtime.Estimation
{
    /// <summary>The thresholds from the decisions file, in one place so the CLI and the GUI agree.</summary>
    public sealed class EstimateThresholds
    {
        public static EstimateThresholds Default => new EstimateThresholds();

        /// <summary>Confirm above an hour.</summary>
        public TimeSpan ConfirmAboveTime { get; set; } = TimeSpan.FromHours(1);

        /// <summary>Confirm above 100 million seeds.</summary>
        public long ConfirmAboveSeeds { get; set; } = 100_000_000L;

        /// <summary>Confirm above 1 GB of output (decimal GB, as the audit measured it).</summary>
        public long ConfirmAboveOutputBytes { get; set; } = 1_000_000_000L;

        /// <summary>Confirm when the output would take more than a tenth of what is free.</summary>
        public double ConfirmAboveFreeSpaceFraction { get; set; } = 0.10;

        /// <summary>Refuse when the output would take more than this share of free space.</summary>
        public double RefuseAboveFreeSpaceFraction { get; set; } = 0.90;

        /// <summary>Leave this much free whatever happens.</summary>
        public long ReserveFreeBytes { get; set; } = 1_073_741_824L;

        /// <summary>A query matching this fraction of seeds is one with no real filter (defect 8).</summary>
        public double SuspiciousHitRate { get; set; } = 0.5;
    }

    /// <summary>
    /// Turns a plan into time, memory and disk RANGES with the constants behind them, plus the verdict.
    ///
    /// <para>It is the same object before the run and during it: feed it a <see cref="Calibration"/> and
    /// it re-projects from what the run has actually measured, which is how the ETA sharpens after every
    /// slice instead of repeating a guess made on someone else's machine.</para>
    /// </summary>
    public sealed class Estimator
    {
        private readonly EstimateThresholds _t;

        public Estimator(EstimateThresholds? thresholds = null)
        {
            _t = thresholds ?? EstimateThresholds.Default;
        }

        public EstimateThresholds Thresholds => _t;

        /// <summary>
        /// Projects a run. <paramref name="calibration"/> is null before the first slice and the live
        /// snapshot afterwards; <paramref name="seedsDone"/> lets a mid-run call project the REMAINDER.
        /// </summary>
        public Estimate Project(EstimateInputs inputs, CalibrationSnapshot? calibration = null, long seedsDone = 0)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));

            List<string> notes = new List<string>();
            List<string> reasons = new List<string>();

            long seeds = Math.Max(0, inputs.Seeds);
            long remaining = Math.Max(0, seeds - Math.Max(0, seedsDone));
            int threads = Math.Max(1, inputs.Threads);
            long cells = Math.Max(1, inputs.GridCells);

            // ---- rate --------------------------------------------------------------------------
            (double tableCost, bool extrapolated, string how) = CostModel.SingleThreadSeconds(inputs.Tier, cells);

            // The rate is the busy workers', the memory below is every worker's (EstimateInputs.BusyWorkers).
            int busy = inputs.BusyWorkers > 0 ? Math.Min(threads, inputs.BusyWorkers) : threads;
            double tableRate = CostModel.SeedsPerSecond(inputs.Tier, cells, busy);
            if (busy < threads)
            {
                notes.Add("only " + busy.ToString(CultureInfo.InvariantCulture) + " of the "
                          + threads.ToString(CultureInfo.InvariantCulture)
                          + " workers have a block to compute (one worker computes a whole block), so the rate is "
                          + busy.ToString(CultureInfo.InvariantCulture) + " workers', and the memory is still all "
                          + threads.ToString(CultureInfo.InvariantCulture) + "'s");
            }

            bool calibrated = calibration != null && calibration.IsUsable;
            double rate = calibrated ? calibration!.SeedsPerSecond : tableRate;
            if (rate <= 0) rate = 1e-9;

            double bandLow, bandHigh;
            if (calibrated && calibration!.HasSpread)
            {
                // The observed spread IS the range: no assumed band once the run has measured itself.
                bandLow = calibration.FastestSliceSeedsPerSecond / rate;
                bandHigh = calibration.SlowestSliceSeedsPerSecond / rate;
                if (bandLow < 1) { /* fastest slice -> the optimistic end */ }
                notes.Add("the time range is the spread of " + calibration.Slices
                          + " measured slices, not an assumed band");
            }
            else
            {
                double band = extrapolated ? CostModel.UncalibratedBandExtrapolated
                                           : CostModel.UncalibratedBandInterpolated;
                bandLow = 1.0 / band;
                bandHigh = band;
                notes.Add("time is projected from this build's measurements on " + CostModel.AnchorMachine
                          + ", widened by an ASSUMED +/-"
                          + ((band - 1) * 100).ToString("0", CultureInfo.InvariantCulture)
                          + " % until this run measures itself");
                if (calibrated) notes.Add("one slice measured so far: the rate is live, the band is still the assumed one");
            }
            if (extrapolated) notes.Add("the per-seed cost is EXTRAPOLATED beyond the measured grids: " + how);

            double secondsMid = remaining / rate;
            EstimateRange time = new EstimateRange(secondsMid / bandHigh, secondsMid / bandLow);
            if (calibrated && calibration!.HasSpread)
            {
                time = new EstimateRange(remaining / calibration.FastestSliceSeedsPerSecond,
                                         remaining / calibration.SlowestSliceSeedsPerSecond);
            }

            // ---- matches and disk --------------------------------------------------------------
            double? hitRate = calibrated && calibration!.HitRate.HasValue && calibration.Matches > 0
                ? calibration.HitRate
                : inputs.ExpectedHitRate;

            EstimateRange matches;
            bool hitRateKnown = hitRate.HasValue;
            if (hitRateKnown)
            {
                double h = Math.Max(0, Math.Min(1, hitRate!.Value));
                // A hit rate is itself uncertain: half to double, unless it is already near 1.
                matches = new EstimateRange(seeds * Math.Max(0, h / 2.0), seeds * Math.Min(1.0, h * 2.0));
            }
            else
            {
                matches = new EstimateRange(0, seeds);
                notes.Add("no hit rate is known yet, so the match count is bounded only by the seeds scanned");
            }

            double recordBytes = calibrated && calibration!.BytesPerMatch.HasValue
                ? calibration.BytesPerMatch!.Value
                : CostModel.RecordBytes(inputs.Format, inputs.GoalCount);

            EstimateRange disk;
            if (inputs.Format == OutputFormat.None)
            {
                disk = EstimateRange.Exact(0);
            }
            else if (inputs.KeepN.HasValue)
            {
                // Bounded: only the kept records are ever written. This is the whole point of --keep
                // being a real cap - disk is O(N) and does not grow with the seeds scanned.
                double lowRecs = Math.Min(inputs.KeepN.Value, matches.Low);
                double highRecs = Math.Min(inputs.KeepN.Value, matches.High);
                disk = new EstimateRange(lowRecs * recordBytes, highRecs * recordBytes);
                if (!hitRateKnown)
                    notes.Add("bounded output: at most " + Bytes.Count(inputs.KeepN.Value)
                              + " records reach disk however many seeds match");
            }
            else
            {
                disk = new EstimateRange(matches.Low * recordBytes, matches.High * recordBytes);
                if (inputs.Format == OutputFormat.JsonlGz)
                    disk = new EstimateRange(disk.Low / CostModel.GzipRatioHigh, disk.High / CostModel.GzipRatioLow);
            }

            // ---- memory ------------------------------------------------------------------------
            WorkerFootprint fp = inputs.Footprint ?? WorkerFootprints.For(inputs.Tier, cells);
            long heapBytes = inputs.KeepN.HasValue
                ? (long)Math.Min((double)long.MaxValue,
                                 inputs.KeepN.Value * (recordBytes + CostModel.HeapRecordOverheadBytes))
                : 0;
            EstimateRange memory = new EstimateRange(
                CostModel.BaseProcessBytes + heapBytes + (double)threads * fp.LowBytes,
                CostModel.BaseProcessBytes + heapBytes + (double)threads * fp.HighBytes);

            if (!inputs.KeepN.HasValue)
                notes.Add("unbounded output holds nothing extra in memory, but the file grows without limit "
                          + "unless --rotate is set");

            // ---- verdict -----------------------------------------------------------------------
            EstimateVerdict verdict = EstimateVerdict.Ok;

            void Confirm(string why)
            {
                reasons.Add(why);
                if (verdict == EstimateVerdict.Ok) verdict = EstimateVerdict.Confirm;
            }
            void Refuse(string why)
            {
                reasons.Add(why);
                verdict = EstimateVerdict.Refuse;
            }

            long usableFree = Math.Max(0, inputs.FreeSpaceBytes - _t.ReserveFreeBytes);

            if (inputs.FreeSpaceBytes > 0 && disk.High > usableFree)
            {
                Refuse("the output could reach " + Bytes.Human(disk.High) + " and only "
                       + Bytes.Human(inputs.FreeSpaceBytes) + " is free ("
                       + Bytes.Human(_t.ReserveFreeBytes) + " of it held back) - bound the run with --keep N, "
                       + "add --rotate, or write to another volume");
            }
            else if (inputs.FreeSpaceBytes > 0
                     && disk.High > inputs.FreeSpaceBytes * _t.RefuseAboveFreeSpaceFraction)
            {
                Refuse("the output could reach " + Bytes.Human(disk.High) + ", over "
                       + (_t.RefuseAboveFreeSpaceFraction * 100).ToString("0") + " % of the "
                       + Bytes.Human(inputs.FreeSpaceBytes) + " free");
            }

            if (inputs.AvailableMemoryBytes > 0 && memory.High > inputs.AvailableMemoryBytes)
            {
                Refuse("peak memory could reach " + Bytes.Human(memory.High) + " against "
                       + Bytes.Human(inputs.AvailableMemoryBytes)
                       + " available - run fewer workers, or use a coarser grid");
            }

            if (time.Mid > _t.ConfirmAboveTime.TotalSeconds)
                Confirm("estimated time " + Bytes.Duration(time.Mid) + " is over "
                        + Bytes.Duration(_t.ConfirmAboveTime));

            if (seeds > _t.ConfirmAboveSeeds)
                Confirm(Bytes.Count(seeds) + " seeds is over " + Bytes.Count(_t.ConfirmAboveSeeds));

            if (disk.High > _t.ConfirmAboveOutputBytes)
                Confirm("output could reach " + Bytes.Human(disk.High) + ", over "
                        + Bytes.Human(_t.ConfirmAboveOutputBytes));

            if (inputs.FreeSpaceBytes > 0 && disk.High > inputs.FreeSpaceBytes * _t.ConfirmAboveFreeSpaceFraction)
                Confirm("output could take more than "
                        + (_t.ConfirmAboveFreeSpaceFraction * 100).ToString("0") + " % of the free space on this volume");

            if (inputs.AllSeedsFlag)
                Confirm("--all was given: every one of the 4,294,967,296 worlds will be visited");

            if (hitRateKnown && hitRate!.Value >= _t.SuspiciousHitRate)
                Confirm("this query matches about "
                        + (hitRate.Value * 100).ToString("0.#", CultureInfo.InvariantCulture)
                        + " % of seeds - with no must-have goal it is not filtering anything");

            if (!inputs.KeepN.HasValue && !hitRateKnown && seeds > 1_000_000)
                Confirm("--keep all with an unknown hit rate over " + Bytes.Count(seeds)
                        + " seeds: worst case every seed is written");

            List<ConstantUse> constants = new List<ConstantUse>(
                CostModel.Provenance(inputs.Tier, cells, threads, inputs.Format, inputs.GoalCount));
            constants.Add(new ConstantUse("per-worker footprint", fp.HighBytes, "B", fp.Source, true));
            if (calibrated)
            {
                constants.Add(new ConstantUse("live rate", rate, "seeds/s",
                    "measured on this run over " + calibration!.Slices + " slice(s), "
                    + Bytes.Count(calibration.Seeds) + " seeds", true));
                if (calibration!.BytesPerMatch.HasValue)
                    constants.Add(new ConstantUse("live record size", calibration.BytesPerMatch!.Value, "B",
                        "measured on this run", true));
            }

            return new Estimate(inputs, time, memory, disk, matches, rate, calibrated,
                                constants, verdict, reasons, notes);
        }

        /// <summary>
        /// Re-projects the remainder of a running search. The engine calls this after each slice, per
        /// the decisions file's "estimates re-projected after every slice".
        /// </summary>
        public Estimate Reproject(EstimateInputs inputs, Calibration calibration, long seedsDone) =>
            Project(inputs, (calibration ?? throw new ArgumentNullException(nameof(calibration))).Snapshot(), seedsDone);
    }
}
