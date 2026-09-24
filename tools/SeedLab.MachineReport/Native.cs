using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace SeedLab.MachineReport
{
    /// <summary>
    /// The few Windows calls the tool makes. Every one of them only READS the state of this machine;
    /// none changes a setting or writes anything.
    /// </summary>
    internal static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        /// <summary>The long form of a path (expands 8.3 names); the input when that fails.</summary>
        public static string LongPath(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint n = GetLongPathNameW(path, sb, (uint)sb.Capacity);
                if (n > sb.Capacity)
                {
                    sb = new StringBuilder((int)n + 1);
                    n = GetLongPathNameW(path, sb, (uint)sb.Capacity);
                }

                return n > 0 && n < sb.Capacity ? sb.ToString() : path;
            }
            catch (Exception)
            {
                return path;
            }
        }

        /// <summary>The 8.3 form of a path, or null. Used only to recognise the package folder in text.</summary>
        public static string? ShortPath(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint n = GetShortPathNameW(path, sb, (uint)sb.Capacity);
                return n > 0 && n < sb.Capacity ? sb.ToString() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public sealed class CoreTopology
        {
            public int PhysicalCores;
            public int LogicalProcessors;
            public int CoresWithSmt;

            /// <summary>Physical cores per EfficiencyClass (higher = faster core; one class = not hybrid).</summary>
            public SortedDictionary<int, int> CoresByEfficiencyClass = new SortedDictionary<int, int>();

            /// <summary>Logical processors per EfficiencyClass.</summary>
            public SortedDictionary<int, int> LogicalByEfficiencyClass = new SortedDictionary<int, int>();
        }

        /// <summary>
        /// Physical cores, SMT and performance/efficiency core classes, from
        /// GetLogicalProcessorInformationEx(RelationProcessorCore). Null when Windows refuses.
        /// </summary>
        public static CoreTopology? Cores()
        {
            const int RelationProcessorCore = 0;
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint len = 0;
                GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
                if (len == 0) return null;
                buf = Marshal.AllocHGlobal((int)len);
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return null;

                CoreTopology t = new CoreTopology();
                int off = 0;
                while (off + 8 <= len)
                {
                    int rel = Marshal.ReadInt32(buf, off);
                    int size = Marshal.ReadInt32(buf, off + 4);
                    if (size <= 0) break;
                    if (rel == RelationProcessorCore)
                    {
                        // PROCESSOR_RELATIONSHIP at +8: Flags(1) EfficiencyClass(1) Reserved(20)
                        // GroupCount(2) then GROUP_AFFINITY[GroupCount] (16 bytes each) at +24.
                        byte flags = Marshal.ReadByte(buf, off + 8);
                        int eff = Marshal.ReadByte(buf, off + 9);
                        int groups = (ushort)Marshal.ReadInt16(buf, off + 8 + 22);
                        int logical = 0;
                        for (int g = 0; g < groups; g++)
                        {
                            long mask = Marshal.ReadInt64(buf, off + 8 + 24 + g * 16);
                            logical += BitOperations.PopCount((ulong)mask);
                        }

                        t.PhysicalCores++;
                        t.LogicalProcessors += logical;
                        if ((flags & 1) != 0) t.CoresWithSmt++;
                        t.CoresByEfficiencyClass.TryGetValue(eff, out int c);
                        t.CoresByEfficiencyClass[eff] = c + 1;
                        t.LogicalByEfficiencyClass.TryGetValue(eff, out int l);
                        t.LogicalByEfficiencyClass[eff] = l + logical;
                    }

                    off += size;
                }

                return t.PhysicalCores > 0 ? t : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>Installed physical memory in bytes, or null.</summary>
        public static long? TotalPhysicalMemory()
        {
            try
            {
                MemoryStatusEx m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref m) ? (long)m.ullTotalPhys : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>(on mains power?, has a battery?) - null when unknown.</summary>
        public static (bool? onAc, bool? hasBattery) Power()
        {
            try
            {
                if (!GetSystemPowerStatus(out SystemPowerStatus s)) return (null, null);
                bool? ac = s.ACLineStatus == 1 ? true : s.ACLineStatus == 0 ? false : (bool?)null;
                bool? battery = s.BatteryFlag == 255 ? (bool?)null : (s.BatteryFlag & 128) == 0;
                return (ac, battery);
            }
            catch (Exception)
            {
                return (null, null);
            }
        }

        /// <summary>Whole-machine CPU busy share over <paramref name="ms"/> milliseconds, 0..100, or null.</summary>
        public static double? CpuBusyPercent(int ms)
        {
            try
            {
                if (!GetSystemTimes(out long i0, out long k0, out long u0)) return null;
                System.Threading.Thread.Sleep(ms);
                if (!GetSystemTimes(out long i1, out long k1, out long u1)) return null;
                long idle = i1 - i0;
                long total = (k1 - k0) + (u1 - u0);   // kernel time includes idle time
                if (total <= 0) return null;
                return Math.Clamp(100.0 * (total - idle) / total, 0.0, 100.0);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
