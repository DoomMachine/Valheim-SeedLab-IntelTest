using System;
using System.Globalization;

namespace SeedLab.Runtime.Hardware
{
    /// <summary>
    /// One formatting of byte counts and durations for every message this layer prints, so a number
    /// means the same thing in the CLI, in the web UI and in a log line.
    /// </summary>
    public static class Bytes
    {
        public const long KiB = 1024L;
        public const long MiB = 1024L * 1024L;
        public const long GiB = 1024L * 1024L * 1024L;
        public const long TiB = 1024L * 1024L * 1024L * 1024L;

        /// <summary>Binary units, because this is memory and disk: "1.7 MiB", "61.6 GiB".</summary>
        public static string Human(double bytes)
        {
            double a = Math.Abs(bytes);
            if (a < KiB) return bytes.ToString("0", CultureInfo.InvariantCulture) + " B";
            if (a < MiB) return (bytes / KiB).ToString("0.##", CultureInfo.InvariantCulture) + " KiB";
            if (a < GiB) return (bytes / MiB).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
            if (a < TiB) return (bytes / GiB).ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
            return (bytes / TiB).ToString("0.###", CultureInfo.InvariantCulture) + " TiB";
        }

        /// <summary>"3.4 s", "7 min 12 s", "2 h 05 min", "4.6 days" - never "00:00:00.0000".</summary>
        public static string Duration(TimeSpan t)
        {
            double s = t.TotalSeconds;
            if (double.IsNaN(s) || double.IsInfinity(s)) return "unknown";
            if (s < 1.0) return (s * 1000.0).ToString("0", CultureInfo.InvariantCulture) + " ms";
            if (s < 90.0) return s.ToString("0.#", CultureInfo.InvariantCulture) + " s";
            if (s < 3600.0) return ((int)(s / 60)).ToString(CultureInfo.InvariantCulture) + " min "
                                   + ((int)(s % 60)).ToString("00", CultureInfo.InvariantCulture) + " s";
            if (s < 86400.0) return ((int)(s / 3600)).ToString(CultureInfo.InvariantCulture) + " h "
                                    + ((int)((s % 3600) / 60)).ToString("00", CultureInfo.InvariantCulture) + " min";
            return (s / 86400.0).ToString("0.##", CultureInfo.InvariantCulture) + " days";
        }

        public static string Duration(double seconds) =>
            double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds > 3.0e9
                ? (double.IsNaN(seconds) ? "unknown" : "beyond a human lifetime")
                : Duration(TimeSpan.FromSeconds(seconds));

        public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    }
}
