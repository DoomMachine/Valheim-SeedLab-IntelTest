using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SeedLab.Runtime.Hardware
{
    /// <summary>
    /// What SeedLab knows about the machine it is running on, probed through the BCL only:
    /// <see cref="Environment.ProcessorCount"/>, <see cref="GC.GetGCMemoryInfo()"/>,
    /// <see cref="System.IO.DriveInfo"/>, <see cref="RuntimeInformation"/> and
    /// <see cref="System.Runtime.Intrinsics.X86"/>. No WMI, no registry, no P/Invoke, nothing that only
    /// exists on Windows.
    ///
    /// <para>Every field that could not be established is <c>null</c> or carries a source string saying
    /// so. The layer never guesses a number it could not measure - a made-up physical core count would
    /// silently size every run on every machine that is not this one.</para>
    /// </summary>
    public sealed class HardwareInfo
    {
        internal HardwareInfo(
            int logicalCores, int? physicalCores, string physicalCoreSource,
            long totalMemoryBytes, long availableMemoryBytes, string memorySource, bool memoryIsProcessLimit,
            Architecture processArchitecture, Architecture osArchitecture,
            string runtimeIdentifier, string osDescription, string frameworkDescription,
            CpuFeatures features, DateTime probedUtc)
        {
            LogicalCores = logicalCores;
            PhysicalCores = physicalCores;
            PhysicalCoreSource = physicalCoreSource;
            TotalMemoryBytes = totalMemoryBytes;
            AvailableMemoryBytes = availableMemoryBytes;
            MemorySource = memorySource;
            MemoryIsProcessLimit = memoryIsProcessLimit;
            ProcessArchitecture = processArchitecture;
            OSArchitecture = osArchitecture;
            RuntimeIdentifier = runtimeIdentifier;
            OSDescription = osDescription;
            FrameworkDescription = frameworkDescription;
            Features = features;
            ProbedUtc = probedUtc;
        }

        /// <summary>Hardware threads visible to this process, honouring affinity and container limits.</summary>
        public int LogicalCores { get; }

        /// <summary>
        /// Physical cores, or <c>null</c> when the BCL cannot tell us. It can be read from
        /// <c>/sys/devices/system/cpu</c> on Linux; on Windows and macOS there is no BCL API for it
        /// short of WMI, sysctl or P/Invoke, all of which are out of bounds here, so we report null
        /// rather than dividing the logical count by a guessed SMT factor.
        /// <see cref="HardwareProbeOptions.PhysicalCoresOverride"/> and the
        /// <c>SEEDLAB_PHYSICAL_CORES</c> environment variable let a user supply it.
        /// </summary>
        public int? PhysicalCores { get; }

        /// <summary>"sysfs-topology", "env:SEEDLAB_PHYSICAL_CORES", "override" or "unavailable".</summary>
        public string PhysicalCoreSource { get; }

        /// <summary>
        /// Physical RAM, or the container/job memory limit when one is in force
        /// (<c>GCMemoryInfo.TotalAvailableMemoryBytes</c>).
        /// </summary>
        public long TotalMemoryBytes { get; }

        /// <summary>
        /// Memory that could be handed out right now without pushing the machine into paging:
        /// <c>TotalAvailableMemoryBytes - MemoryLoadBytes</c>, or <c>MemAvailable</c> from
        /// <c>/proc/meminfo</c> where that exists (it is the kernel's own, better answer).
        /// </summary>
        public long AvailableMemoryBytes { get; }

        /// <summary>"gc-memory-info", "proc-meminfo" or "override".</summary>
        public string MemorySource { get; }

        /// <summary>True when <see cref="TotalMemoryBytes"/> is a container or job-object limit rather
        /// than the machine's RAM - the number to plan against either way, but worth saying out loud.</summary>
        public bool MemoryIsProcessLimit { get; }

        public Architecture ProcessArchitecture { get; }
        public Architecture OSArchitecture { get; }
        public string RuntimeIdentifier { get; }
        public string OSDescription { get; }
        public string FrameworkDescription { get; }

        /// <summary>The instruction sets the JIT says it may use on this CPU.</summary>
        public CpuFeatures Features { get; }

        public DateTime ProbedUtc { get; }

        /// <summary>True when the machine can run the AVX2 batched Perlin path.</summary>
        public bool Avx2 => Features.Avx2;

        /// <summary>Free space on the volume holding <paramref name="path"/>. Never throws.</summary>
        public VolumeInfo VolumeFor(string path) => VolumeInfo.For(path);

        /// <summary>Re-reads only the memory figures; cores and ISA cannot change under us.</summary>
        public HardwareInfo WithFreshMemory() => HardwareProbe.Probe(HardwareProbeOptions.Default);

        /// <summary>A stable one-line identity of the execution environment, for the self-test stamp.</summary>
        public string PlatformKey =>
            RuntimeIdentifier + "|" + ProcessArchitecture + "|" + Features.Key;

        public IReadOnlyList<string> Lines()
        {
            List<string> l = new List<string>
            {
                "cores       " + LogicalCores + " logical"
                    + (PhysicalCores.HasValue
                        ? ", " + PhysicalCores.Value + " physical (" + PhysicalCoreSource + ")"
                        : ", physical count unavailable on this OS through the BCL"),
                "memory      " + Bytes.Human(AvailableMemoryBytes) + " available of "
                    + Bytes.Human(TotalMemoryBytes)
                    + (MemoryIsProcessLimit ? " (process/container limit)" : "")
                    + " [" + MemorySource + "]",
                "platform    " + RuntimeIdentifier + ", " + OSDescription,
                "runtime     " + FrameworkDescription + ", process " + ProcessArchitecture
                    + " on " + OSArchitecture,
                "simd        " + Features.Describe()
            };
            return l;
        }

        public string Format()
        {
            StringBuilder sb = new StringBuilder();
            foreach (string s in Lines()) sb.Append("  ").AppendLine(s);
            return sb.ToString();
        }

        public override string ToString() =>
            LogicalCores + " logical cores, " + Bytes.Human(AvailableMemoryBytes) + " available RAM, "
            + RuntimeIdentifier + (Avx2 ? ", AVX2" : ", no AVX2");
    }

    /// <summary>
    /// The instruction sets the JIT reports for this process. <see cref="Fma"/> is recorded and then
    /// deliberately left alone: a fused multiply-add would round once where the game rounds twice, and
    /// SeedLab's whole value is that it does not do that.
    /// </summary>
    public sealed class CpuFeatures
    {
        public CpuFeatures(bool sse2, bool avx, bool avx2, bool avx512F, bool fma, bool advSimd,
                           int vectorByteWidth, bool vector256Accelerated, bool vector512Accelerated)
        {
            Sse2 = sse2; Avx = avx; Avx2 = avx2; Avx512F = avx512F; Fma = fma; AdvSimd = advSimd;
            VectorByteWidth = vectorByteWidth;
            Vector256Accelerated = vector256Accelerated;
            Vector512Accelerated = vector512Accelerated;
        }

        public bool Sse2 { get; }
        public bool Avx { get; }
        public bool Avx2 { get; }
        public bool Avx512F { get; }

        /// <summary>Present on the CPU. SeedLab must never emit an FMA in generator arithmetic.</summary>
        public bool Fma { get; }

        /// <summary>The arm64 SIMD set, so an arm64 report is not simply blank.</summary>
        public bool AdvSimd { get; }

        public int VectorByteWidth { get; }
        public bool Vector256Accelerated { get; }
        public bool Vector512Accelerated { get; }

        public string Key =>
            (Sse2 ? "sse2 " : "") + (Avx ? "avx " : "") + (Avx2 ? "avx2 " : "")
            + (Avx512F ? "avx512f " : "") + (Fma ? "fma " : "") + (AdvSimd ? "advsimd " : "")
            + "v" + VectorByteWidth;

        public string Describe()
        {
            List<string> have = new List<string>();
            if (Sse2) have.Add("SSE2");
            if (Avx) have.Add("AVX");
            if (Avx2) have.Add("AVX2");
            if (Avx512F) have.Add("AVX-512F");
            if (Fma) have.Add("FMA (present, never used: it would change the last bit)");
            if (AdvSimd) have.Add("AdvSIMD");
            string s = have.Count == 0 ? "no vector ISA detected" : string.Join(", ", have);
            return s + "; Vector<byte> is " + VectorByteWidth + " bytes wide";
        }
    }
}
