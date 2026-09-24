using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SeedLab.Runtime.Hardware
{
    /// <summary>Knobs for <see cref="HardwareProbe"/>; the defaults read the environment.</summary>
    public sealed class HardwareProbeOptions
    {
        public static HardwareProbeOptions Default => new HardwareProbeOptions();

        /// <summary>Physical core count the user supplied (CLI flag or config), if any.</summary>
        public int? PhysicalCoresOverride { get; set; }

        /// <summary>Pretend this much RAM is available - for tests and for "plan as if on a 8 GB laptop".</summary>
        public long? AvailableMemoryOverride { get; set; }

        /// <summary>Pretend this many logical cores - for tests and for planning for a smaller machine.</summary>
        public int? LogicalCoresOverride { get; set; }

        /// <summary>
        /// <c>GCMemoryInfo.MemoryLoadBytes</c> is 0 until the first GC of this process (measured on
        /// win-x64, .NET 10.0.12, 2026-09-23), which would read as "the whole machine is free". A forced
        /// gen0 collection costs 0.4 ms and fills it in. Turn it off only if you know a GC has run.
        /// </summary>
        public bool ForceGcForMemoryLoad { get; set; } = true;

        /// <summary>Read <c>SEEDLAB_PHYSICAL_CORES</c> / <c>SEEDLAB_ASSUME_CORES</c>.</summary>
        public bool ReadEnvironment { get; set; } = true;
    }

    /// <summary>
    /// The hardware probe. Cheap enough to run at every start (measured below a millisecond on this
    /// machine), and safe to run anywhere: every call that can fail is wrapped, because a probe that
    /// throws on someone else's laptop would stop a search that would have run fine.
    /// </summary>
    public static class HardwareProbe
    {
        /// <summary>Probes the machine now. Never throws.</summary>
        public static HardwareInfo Probe(HardwareProbeOptions? options = null)
        {
            HardwareProbeOptions o = options ?? HardwareProbeOptions.Default;

            int logical = o.LogicalCoresOverride ?? Math.Max(1, Environment.ProcessorCount);

            (int? physical, string physSource) = ProbePhysicalCores(o);
            (long total, long available, string memSource, bool isLimit) = ProbeMemory(o);

            CpuFeatures features = ProbeFeatures();

            string rid;
            try { rid = RuntimeInformation.RuntimeIdentifier; }
            catch (Exception) { rid = "unknown-rid"; }

            return new HardwareInfo(
                logical, physical, physSource,
                total, available, memSource, isLimit,
                RuntimeInformation.ProcessArchitecture,
                RuntimeInformation.OSArchitecture,
                rid,
                Safe(() => RuntimeInformation.OSDescription),
                Safe(() => RuntimeInformation.FrameworkDescription),
                features,
                DateTime.UtcNow);
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? ""; }
            catch (Exception) { return ""; }
        }

        private static CpuFeatures ProbeFeatures()
        {
            bool sse2 = false, avx = false, avx2 = false, avx512 = false, fma = false, adv = false;
            try
            {
                sse2 = Sse2.IsSupported;
                avx = Avx.IsSupported;
                avx2 = Avx2.IsSupported;
                avx512 = Avx512F.IsSupported;
                fma = Fma.IsSupported;
                adv = AdvSimd.IsSupported;
            }
            catch (Exception) { /* an ISA class that will not load simply reads as absent */ }

            int width = 16;
            bool v256 = false, v512 = false;
            try
            {
                width = Vector<byte>.Count;
                v256 = System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated;
                v512 = System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated;
            }
            catch (Exception) { }

            return new CpuFeatures(sse2, avx, avx2, avx512, fma, adv, width, v256, v512);
        }

        private static (int?, string) ProbePhysicalCores(HardwareProbeOptions o)
        {
            if (o.PhysicalCoresOverride.HasValue && o.PhysicalCoresOverride.Value > 0)
                return (o.PhysicalCoresOverride.Value, "override");

            if (o.ReadEnvironment)
            {
                string? env = Environment.GetEnvironmentVariable("SEEDLAB_PHYSICAL_CORES");
                if (!string.IsNullOrWhiteSpace(env)
                    && int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    && n > 0)
                {
                    return (n, "env:SEEDLAB_PHYSICAL_CORES");
                }
            }

            int? sysfs = ReadLinuxPhysicalCores();
            if (sysfs.HasValue) return (sysfs, "sysfs-topology");

            return (null, "unavailable");
        }

        /// <summary>
        /// Distinct (package, core) pairs under <c>/sys/devices/system/cpu</c>. Pure file reading, so it
        /// stays inside the BCL-only rule, and it is simply absent on Windows and macOS.
        /// </summary>
        private static int? ReadLinuxPhysicalCores()
        {
            try
            {
                const string root = "/sys/devices/system/cpu";
                if (!Directory.Exists(root)) return null;

                HashSet<string> cores = new HashSet<string>(StringComparer.Ordinal);
                foreach (string dir in Directory.EnumerateDirectories(root, "cpu*"))
                {
                    string name = Path.GetFileName(dir);
                    bool numbered = name.Length > 3;
                    for (int i = 3; numbered && i < name.Length; i++)
                        if (name[i] < '0' || name[i] > '9') numbered = false;
                    if (!numbered) continue;

                    string coreId = Path.Combine(dir, "topology", "core_id");
                    string pkgId = Path.Combine(dir, "topology", "physical_package_id");
                    if (!File.Exists(coreId)) continue;

                    string c = File.ReadAllText(coreId).Trim();
                    string p = File.Exists(pkgId) ? File.ReadAllText(pkgId).Trim() : "0";
                    if (c.Length > 0) cores.Add(p + ":" + c);
                }
                return cores.Count > 0 ? cores.Count : (int?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static (long total, long available, string source, bool isLimit) ProbeMemory(HardwareProbeOptions o)
        {
            long? procAvail = ReadProcMemAvailable();
            long total = 0, available = 0;
            bool isLimit = false;
            string source = "gc-memory-info";

            try
            {
                if (o.ForceGcForMemoryLoad)
                {
                    GCMemoryInfo pre = GC.GetGCMemoryInfo();
                    if (pre.MemoryLoadBytes == 0)
                    {
                        // Measured on win-x64 .NET 10.0.12: MemoryLoadBytes is 0 before the first GC and
                        // 21.15 GB straight after a 0.4 ms forced gen0. Without this the guard would
                        // believe the whole machine is free.
                        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                    }
                }

                GCMemoryInfo gi = GC.GetGCMemoryInfo();
                total = gi.TotalAvailableMemoryBytes;
                long load = gi.MemoryLoadBytes;
                available = load > 0 && total > 0 ? Math.Max(0, total - load) : total;

                // A container or cgroup limit shows up as a GC total well below the machine's RAM. The
                // only place the BCL lets us see the machine's RAM is /proc/meminfo, which is also the
                // only place a container limit is common - so the flag is set there and left false
                // elsewhere rather than guessed at.
                long? memTotal = ReadProcMemTotal();
                isLimit = memTotal.HasValue && total > 0 && total < memTotal.Value * 0.95;
            }
            catch (Exception)
            {
                total = 0;
                available = 0;
            }

            if (procAvail.HasValue && procAvail.Value > 0)
            {
                // The kernel's own answer beats total-minus-load: it counts reclaimable page cache.
                available = procAvail.Value;
                source = "proc-meminfo";
            }

            if (o.AvailableMemoryOverride.HasValue)
            {
                available = Math.Max(0, o.AvailableMemoryOverride.Value);
                if (total < available) total = available;
                source = "override";
            }

            if (total <= 0) total = available;
            return (total, available, source, isLimit);
        }

        private static long? ReadProcMemTotal() => ReadMemInfoField("MemTotal:");

        private static long? ReadProcMemAvailable() => ReadMemInfoField("MemAvailable:");

        private static long? ReadMemInfoField(string field)
        {
            try
            {
                const string p = "/proc/meminfo";
                if (!File.Exists(p)) return null;
                foreach (string line in File.ReadLines(p))
                {
                    if (!line.StartsWith(field, StringComparison.Ordinal)) continue;
                    string rest = line.Substring(field.Length).Trim();
                    int sp = rest.IndexOf(' ');
                    string num = sp > 0 ? rest.Substring(0, sp) : rest;
                    if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb))
                        return kb * 1024L;
                    return null;
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
