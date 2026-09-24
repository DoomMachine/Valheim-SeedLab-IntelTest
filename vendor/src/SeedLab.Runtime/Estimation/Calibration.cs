using System;
using System.Globalization;
using System.Threading;

namespace SeedLab.Runtime.Estimation
{
    /// <summary>An immutable view of what the run has actually done so far.</summary>
    public sealed class CalibrationSnapshot
    {
        public CalibrationSnapshot(int slices, long seeds, double seconds, long matches, long bytes,
                                   double seedsPerSecond, double slowestSlice, double fastestSlice)
        {
            Slices = slices;
            Seeds = seeds;
            Seconds = seconds;
            Matches = matches;
            BytesWritten = bytes;
            SeedsPerSecond = seedsPerSecond;
            SlowestSliceSeedsPerSecond = slowestSlice;
            FastestSliceSeedsPerSecond = fastestSlice;
        }

        public int Slices { get; }
        public long Seeds { get; }
        public double Seconds { get; }
        public long Matches { get; }
        public long BytesWritten { get; }

        /// <summary>The smoothed rate: what the next slice is expected to run at.</summary>
        public double SeedsPerSecond { get; }

        /// <summary>The observed spread, which becomes the estimate's range once there are enough slices.</summary>
        public double SlowestSliceSeedsPerSecond { get; }
        public double FastestSliceSeedsPerSecond { get; }

        public double? HitRate => Seeds > 0 ? (double)Matches / Seeds : (double?)null;
        public double? BytesPerMatch => Matches > 0 ? (double)BytesWritten / Matches : (double?)null;

        /// <summary>Two slices is the least that can show a spread; below that the band stays the assumed one.</summary>
        public bool IsUsable => Slices >= 1 && Seeds > 0 && Seconds > 0 && SeedsPerSecond > 0;
        public bool HasSpread => Slices >= 2 && SlowestSliceSeedsPerSecond > 0 && FastestSliceSeedsPerSecond > 0;

        public static readonly CalibrationSnapshot Empty =
            new CalibrationSnapshot(0, 0, 0, 0, 0, 0, 0, 0);

        public override string ToString() =>
            !IsUsable ? "no live measurements yet"
            : Slices + " slice" + (Slices == 1 ? "" : "s") + ", "
              + SeedsPerSecond.ToString("N0", CultureInfo.InvariantCulture) + " seeds/s measured"
              + (HitRate.HasValue ? ", hit rate " + (HitRate.Value * 100).ToString("0.####", CultureInfo.InvariantCulture) + " %" : "");
    }

    /// <summary>
    /// The live re-calibration the decisions file requires: the search engine feeds real numbers after
    /// every slice, and every later estimate is re-projected from them instead of from this machine's
    /// table constants.
    ///
    /// <para>The rate is smoothed (an exponentially weighted average, so a single slow slice does not
    /// make the ETA jump) while the RANGE comes from the raw slowest and fastest slices - once there is
    /// real spread, the estimate stops using an assumed band and starts using the observed one.</para>
    ///
    /// <para>Thread-safe: the engine's collector can call <see cref="ObserveSlice"/> while a progress
    /// thread reads <see cref="Snapshot"/>.</para>
    /// </summary>
    public sealed class Calibration
    {
        private readonly object _gate = new object();
        private readonly double _alpha;

        private int _slices;
        private long _seeds;
        private double _seconds;
        private long _matches;
        private long _bytes;
        private double _ewma;
        private double _slowest = double.MaxValue;
        private double _fastest;

        /// <param name="alpha">Weight of the newest slice, 0.35 by default: responsive but not jumpy.</param>
        public Calibration(double alpha = 0.35)
        {
            _alpha = alpha > 0 && alpha <= 1 ? alpha : 0.35;
        }

        /// <summary>
        /// One finished slice. <paramref name="bytesWritten"/> is what actually reached disk for it, so
        /// the disk projection tracks the real record size rather than the audit's average.
        /// </summary>
        public void ObserveSlice(long seeds, TimeSpan elapsed, long matches, long bytesWritten)
        {
            if (seeds <= 0 || elapsed.TotalSeconds <= 0) return;
            double rate = seeds / elapsed.TotalSeconds;

            lock (_gate)
            {
                _slices++;
                _seeds += seeds;
                _seconds += elapsed.TotalSeconds;
                _matches += Math.Max(0, matches);
                _bytes += Math.Max(0, bytesWritten);
                _ewma = _slices == 1 ? rate : _alpha * rate + (1 - _alpha) * _ewma;
                if (rate < _slowest) _slowest = rate;
                if (rate > _fastest) _fastest = rate;
            }
        }

        public CalibrationSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new CalibrationSnapshot(_slices, _seeds, _seconds, _matches, _bytes,
                    _ewma, _slowest == double.MaxValue ? 0 : _slowest, _fastest);
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _slices = 0; _seeds = 0; _seconds = 0; _matches = 0; _bytes = 0;
                _ewma = 0; _slowest = double.MaxValue; _fastest = 0;
            }
        }
    }
}
