using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SeedLab.Runtime.Hardware;

namespace SeedLab.Runtime.Estimation
{
    /// <summary>
    /// A low/high pair. Everything this layer predicts is a range, because a single number would be a
    /// claim the measurements do not support.
    /// </summary>
    public readonly struct EstimateRange
    {
        public EstimateRange(double low, double high)
        {
            if (double.IsNaN(low)) low = 0;
            if (double.IsNaN(high)) high = low;
            Low = Math.Min(low, high);
            High = Math.Max(low, high);
        }

        public double Low { get; }
        public double High { get; }

        /// <summary>The geometric middle - the value to quote when one number is wanted.</summary>
        public double Mid => Low > 0 && High > 0 ? Math.Sqrt(Low * High) : (Low + High) / 2.0;

        public static EstimateRange Exact(double v) => new EstimateRange(v, v);
        public bool IsExact => Low == High;

        public EstimateRange Scale(double f) => new EstimateRange(Low * f, High * f);

        public string HumanBytes() =>
            IsExact ? Bytes.Human(Low) : Bytes.Human(Low) + " - " + Bytes.Human(High);

        public string HumanTime() =>
            IsExact ? Bytes.Duration(Low) : Bytes.Duration(Low) + " - " + Bytes.Duration(High);

        public override string ToString() =>
            IsExact ? Low.ToString("G6", CultureInfo.InvariantCulture)
                    : Low.ToString("G6", CultureInfo.InvariantCulture) + " - " + High.ToString("G6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The estimate: what a run will cost in time, memory and disk, on what constants, and whether it
    /// should start at all. Printed before EVERY run, not only for <c>--dry-run</c>.
    /// </summary>
    public sealed class Estimate
    {
        internal Estimate(EstimateInputs inputs, EstimateRange seconds, EstimateRange memoryBytes,
                          EstimateRange diskBytes, EstimateRange matches, double seedsPerSecond,
                          bool calibrated, IReadOnlyList<ConstantUse> constants,
                          EstimateVerdict verdict, IReadOnlyList<string> reasons, IReadOnlyList<string> notes)
        {
            Inputs = inputs;
            Time = seconds;
            Memory = memoryBytes;
            Disk = diskBytes;
            Matches = matches;
            SeedsPerSecond = seedsPerSecond;
            Calibrated = calibrated;
            Constants = constants;
            Verdict = verdict;
            Reasons = reasons;
            Notes = notes;
        }

        public EstimateInputs Inputs { get; }

        /// <summary>Wall-clock seconds.</summary>
        public EstimateRange Time { get; }

        /// <summary>Peak resident bytes: workers plus the bounded heap plus a fixed base.</summary>
        public EstimateRange Memory { get; }

        /// <summary>Bytes the run will write. In bounded mode this is capped by <c>--keep N</c>.</summary>
        public EstimateRange Disk { get; }

        /// <summary>Records expected to match, before the bound is applied.</summary>
        public EstimateRange Matches { get; }

        /// <summary>The rate the projection used.</summary>
        public double SeedsPerSecond { get; }

        /// <summary>True when live slice measurements, not the table constants, drove this projection.</summary>
        public bool Calibrated { get; }

        public IReadOnlyList<ConstantUse> Constants { get; }

        public EstimateVerdict Verdict { get; }

        /// <summary>Why the verdict is what it is. Empty when it is <see cref="EstimateVerdict.Ok"/>.</summary>
        public IReadOnlyList<string> Reasons { get; }

        /// <summary>Things worth saying that are not verdict reasons (assumptions, extrapolations).</summary>
        public IReadOnlyList<string> Notes { get; }

        public double PercentOfSeedSpace => 100.0 * Inputs.Seeds / EstimateInputs.WholeSeedSpace;

        public bool NeedsConfirmation => Verdict == EstimateVerdict.Confirm;
        public bool Refused => Verdict == EstimateVerdict.Refuse;

        /// <summary>The block printed before a run - the same text in the CLI and the web UI.</summary>
        public string Format(bool includeConstants = true)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("  seeds       ").Append(Bytes.Count(Inputs.Seeds)).Append(" (")
              .Append(PercentOfSeedSpace.ToString(PercentOfSeedSpace < 0.01 ? "0.00000" : "0.###", CultureInfo.InvariantCulture))
              .Append(" % of all 4,294,967,296 worlds)\n");

            sb.Append("  work        ").Append(Inputs.Tier).Append(" on ")
              .Append(Bytes.Count(Inputs.GridCells)).Append(" samples/seed, ")
              .Append(Inputs.Threads).Append(" worker").Append(Inputs.Threads == 1 ? "" : "s").Append('\n');

            sb.Append("  rate        ").Append(SeedsPerSecond.ToString("N0", CultureInfo.InvariantCulture))
              .Append(" seeds/s (").Append(Calibrated ? "measured on this run" : "projected from this build's measurements")
              .Append(")\n");

            sb.Append("  time        ").Append(Time.HumanTime()).Append('\n');
            sb.Append("  memory      ").Append(Memory.HumanBytes()).Append(" peak\n");
            sb.Append("  output      ").Append(Disk.HumanBytes());
            if (Inputs.KeepN.HasValue)
                sb.Append("  (bounded: --keep ").Append(Bytes.Count(Inputs.KeepN.Value)).Append(" is a real cap on the file)");
            else
                sb.Append("  (UNBOUNDED: --keep all)");
            sb.Append('\n');

            sb.Append("  matches     ").Append(Bytes.Count((long)Matches.Low)).Append(" - ")
              .Append(Bytes.Count((long)Matches.High)).Append(" expected\n");

            sb.Append("  free space  ").Append(Bytes.Human(Inputs.FreeSpaceBytes)).Append('\n');

            sb.Append("  verdict     ").Append(Verdict switch
            {
                EstimateVerdict.Ok => "fits",
                EstimateVerdict.Confirm => "needs confirmation",
                _ => "REFUSED"
            }).Append('\n');

            foreach (string r in Reasons) sb.Append("              - ").Append(r).Append('\n');
            foreach (string n in Notes) sb.Append("              . ").Append(n).Append('\n');

            if (includeConstants)
            {
                sb.Append("  constants\n");
                foreach (ConstantUse c in Constants) sb.Append("              ").Append(c).Append('\n');
            }
            return sb.ToString();
        }

        public override string ToString() =>
            Time.HumanTime() + ", " + Disk.HumanBytes() + " of output, " + Verdict;
    }
}
