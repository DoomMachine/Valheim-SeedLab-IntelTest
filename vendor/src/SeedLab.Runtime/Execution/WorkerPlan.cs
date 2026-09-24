using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using SeedLab.Runtime.Hardware;

namespace SeedLab.Runtime.Execution
{
    /// <summary>Knobs for <see cref="WorkerPlanner"/>.</summary>
    public sealed class WorkerPlanOptions
    {
        public static WorkerPlanOptions Default => new WorkerPlanOptions();

        /// <summary>
        /// The share of AVAILABLE memory workers may hold, 0.5 per the decisions file: half of what is
        /// free right now, so the rest of the machine keeps breathing and we never drive it into swap.
        /// </summary>
        public double MemoryFraction { get; set; } = 0.5;

        /// <summary>An explicit <c>--threads N</c>. It replaces the mode's share but is still memory-capped.</summary>
        public int? RequestedWorkers { get; set; }

        /// <summary>Fixed overhead held outside the workers (output buffers, the bounded heap).</summary>
        public long ReservedBytes { get; set; }

        /// <summary>Plan against the optimistic end of a footprint range. Off: a guard uses the high end.</summary>
        public bool UseOptimisticFootprint { get; set; }

        /// <summary>Never plan more workers than this, whatever the mode says.</summary>
        public int MaxWorkers { get; set; } = 1024;
    }

    /// <summary>
    /// The answer to "how many workers, at what priority, and why that number". Carries BOTH the number
    /// the mode asked for and the number memory allowed, because the user asked to be told when memory
    /// is what is limiting them rather than their choice of mode.
    /// </summary>
    public sealed class WorkerPlan
    {
        internal WorkerPlan(ResourceMode mode, int workers, int modeWorkers, int memoryWorkers,
                            int? requestedWorkers, WorkerFootprint footprint, long budgetBytes,
                            long plannedBytes, HardwareInfo hardware, bool memoryLimited,
                            bool insufficientMemory, double memoryFraction)
        {
            Mode = mode;
            Workers = workers;
            ModeWorkers = modeWorkers;
            MemoryWorkers = memoryWorkers;
            RequestedWorkers = requestedWorkers;
            Footprint = footprint;
            MemoryBudgetBytes = budgetBytes;
            PlannedMemoryBytes = plannedBytes;
            Hardware = hardware;
            MemoryLimited = memoryLimited;
            InsufficientMemory = insufficientMemory;
            MemoryFraction = memoryFraction;
        }

        public ResourceMode Mode { get; }

        /// <summary>What to actually run with. Never below 1.</summary>
        public int Workers { get; }

        /// <summary>What the mode alone would have given on this core count.</summary>
        public int ModeWorkers { get; }

        /// <summary>What available memory alone would have allowed.</summary>
        public int MemoryWorkers { get; }

        /// <summary>An explicit --threads, if the caller passed one.</summary>
        public int? RequestedWorkers { get; }

        public WorkerFootprint Footprint { get; }

        /// <summary>Available RAM x <see cref="WorkerPlanOptions.MemoryFraction"/>, minus the reservation.</summary>
        public long MemoryBudgetBytes { get; }

        /// <summary>What <see cref="Workers"/> workers are expected to hold.</summary>
        public long PlannedMemoryBytes { get; }

        public HardwareInfo Hardware { get; }

        /// <summary>The share of available memory the budget was taken from (0.5 by default).</summary>
        public double MemoryFraction { get; }

        /// <summary>True when memory, not the mode, chose the worker count - always reported out loud.</summary>
        public bool MemoryLimited { get; }

        /// <summary>
        /// True when even ONE worker does not fit in the budget. The run still gets a single worker -
        /// refusing is the caller's decision, with the arithmetic in <see cref="Lines"/> - but this is
        /// the flag that should stop a search rather than let the machine start swapping.
        /// </summary>
        public bool InsufficientMemory { get; }

        public ProcessPriorityClass Priority => ResourceModes.Priority(Mode);
        public ThreadPriority WorkerThreadPriority => ResourceModes.WorkerThreadPriority(Mode);

        /// <summary>The lines to print. One line per fact, in the order a user reads them.</summary>
        public IReadOnlyList<string> Lines()
        {
            List<string> l = new List<string>();
            string basis = RequestedWorkers.HasValue
                ? "--threads " + RequestedWorkers.Value
                : ResourceModes.Name(Mode) + " mode = " + (ResourceModes.CoreShare(Mode) * 100).ToString("0", CultureInfo.InvariantCulture)
                  + " % of " + Hardware.LogicalCores + " logical cores";

            l.Add("workers     " + Workers + "  (" + basis + " -> " + ModeWorkers
                  + "; memory allowed " + MemoryWorkers + ")");

            if (MemoryLimited)
            {
                l.Add("            memory is the limit, not the mode: " + ModeWorkers + " asked for, "
                      + Workers + " will run");
            }

            l.Add("memory      " + Bytes.Human(PlannedMemoryBytes) + " planned of a "
                  + Bytes.Human(MemoryBudgetBytes) + " budget ("
                  + Bytes.Human(Hardware.AvailableMemoryBytes) + " available x "
                  + MemoryFraction.ToString("0.##", CultureInfo.InvariantCulture) + "), " + Footprint);

            l.Add("priority    process " + Priority + ", worker threads " + WorkerThreadPriority);

            if (InsufficientMemory)
            {
                l.Add("            WARNING: one worker needs " + Bytes.Human(Footprint.HighBytes)
                      + ", which is more than the whole budget - this run should not start");
            }
            return l;
        }

        public override string ToString() =>
            Workers + " worker" + (Workers == 1 ? "" : "s") + " in " + ResourceModes.Name(Mode) + " mode"
            + (MemoryLimited ? " (memory-limited from " + ModeWorkers + ")" : "");
    }

    /// <summary>
    /// Worker count = min(what the mode asks for, what half of available memory affords), never below 1.
    /// That is the whole rule, and it is applied identically to the CLI and the web UI.
    /// </summary>
    public static class WorkerPlanner
    {
        public static WorkerPlan Plan(ResourceMode mode, WorkerFootprint footprint,
                                      HardwareInfo hardware, WorkerPlanOptions? options = null)
        {
            if (hardware == null) throw new ArgumentNullException(nameof(hardware));
            WorkerPlanOptions o = options ?? WorkerPlanOptions.Default;

            int logical = Math.Max(1, hardware.LogicalCores);

            int modeWorkers = o.RequestedWorkers.HasValue
                ? Math.Max(1, o.RequestedWorkers.Value)
                : Math.Max(1, (int)Math.Round(logical * ResourceModes.CoreShare(mode), MidpointRounding.AwayFromZero));

            if (!o.RequestedWorkers.HasValue) modeWorkers = Math.Min(modeWorkers, logical);
            modeWorkers = Math.Min(modeWorkers, Math.Max(1, o.MaxWorkers));

            long per = o.UseOptimisticFootprint ? footprint.LowBytes : footprint.HighBytes;
            if (per < 1) per = 1;

            double fraction = o.MemoryFraction > 0 && o.MemoryFraction <= 1 ? o.MemoryFraction : 0.5;
            long budget = (long)(hardware.AvailableMemoryBytes * fraction) - Math.Max(0, o.ReservedBytes);
            if (budget < 0) budget = 0;

            long affords = budget / per;
            int memoryWorkers = affords > int.MaxValue ? int.MaxValue : (int)affords;
            bool insufficient = memoryWorkers < 1;
            if (memoryWorkers < 1) memoryWorkers = 1;

            int workers = Math.Max(1, Math.Min(modeWorkers, memoryWorkers));
            bool limited = memoryWorkers < modeWorkers;

            return new WorkerPlan(mode, workers, modeWorkers, memoryWorkers, o.RequestedWorkers,
                                  footprint, budget, (long)workers * per, hardware, limited, insufficient,
                                  fraction);
        }

        /// <summary>Convenience: probe the footprint from the tier and grid, then plan.</summary>
        public static WorkerPlan Plan(ResourceMode mode, WorkTier tier, long gridCells,
                                      HardwareInfo hardware, WorkerPlanOptions? options = null) =>
            Plan(mode, WorkerFootprints.For(tier, gridCells), hardware, options);
    }

    /// <summary>
    /// Applies a process priority and restores the previous one on dispose. Lowering priority is allowed
    /// everywhere; raising it above Normal is not attempted at all, and a platform that refuses the
    /// change is reported rather than crashed on.
    /// </summary>
    public sealed class PriorityScope : IDisposable
    {
        private readonly ProcessPriorityClass _previous;
        private readonly bool _applied;
        private bool _disposed;

        private PriorityScope(ProcessPriorityClass previous, bool applied, ProcessPriorityClass now, string note)
        {
            _previous = previous;
            _applied = applied;
            Current = now;
            Note = note;
        }

        public ProcessPriorityClass Current { get; }

        /// <summary>Empty on success; otherwise why the priority could not be set.</summary>
        public string Note { get; }

        public static PriorityScope Apply(ProcessPriorityClass target)
        {
            // Never above Normal, whatever a caller asks for.
            if (target == ProcessPriorityClass.AboveNormal || target == ProcessPriorityClass.High
                || target == ProcessPriorityClass.RealTime)
            {
                target = ProcessPriorityClass.Normal;
            }

            ProcessPriorityClass previous = ProcessPriorityClass.Normal;
            try
            {
                using Process me = Process.GetCurrentProcess();
                previous = me.PriorityClass;
                if (previous == target) return new PriorityScope(previous, false, previous, "");
                me.PriorityClass = target;
                return new PriorityScope(previous, true, target, "");
            }
            catch (Exception ex)
            {
                return new PriorityScope(previous, false, previous,
                    "process priority could not be set to " + target + " (" + ex.GetType().Name + "); running at " + previous);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_applied) return;
            try
            {
                using Process me = Process.GetCurrentProcess();
                me.PriorityClass = _previous;
            }
            catch (Exception) { /* restoring is best-effort; the process is about to end anyway */ }
        }
    }
}
