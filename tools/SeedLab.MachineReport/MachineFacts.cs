using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SeedLab.MachineReport
{
    /// <summary>
    /// The hardware and Windows facts the report records. Deliberately NOT read: the computer name,
    /// the user name, any serial number or product key, network adapters and addresses, the
    /// registered owner, install dates, and any path outside the package.
    /// </summary>
    public static class MachineFacts
    {
        private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        private const string OsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        public static JsonObject Collect()
        {
            JsonObject o = new JsonObject();

            string cpuName = (ReadReg(CpuKey, "ProcessorNameString") as string ?? "").Trim();
            o["cpuName"] = cpuName.Length > 0 ? cpuName : "(not available)";
            if (ReadReg(CpuKey, "~MHz") is int mhz) o["cpuNominalMHz"] = mhz;

            o["logicalProcessors"] = Environment.ProcessorCount;
            Native.CoreTopology? t = Native.Cores();
            if (t != null)
            {
                JsonObject cores = new JsonObject
                {
                    ["physical"] = t.PhysicalCores,
                    ["logical"] = t.LogicalProcessors,
                    ["withSmt"] = t.CoresWithSmt,
                    ["hybrid"] = t.CoresByEfficiencyClass.Count > 1,
                };
                JsonObject byClass = new JsonObject();
                foreach (KeyValuePair<int, int> kv in t.CoresByEfficiencyClass)
                {
                    byClass[kv.Key.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["cores"] = kv.Value,
                        ["logical"] = t.LogicalByEfficiencyClass.TryGetValue(kv.Key, out int l) ? l : 0,
                    };
                }

                cores["byEfficiencyClass"] = byClass;
                o["cores"] = cores;
            }

            long? ram = Native.TotalPhysicalMemory();
            if (ram.HasValue) o["ramBytes"] = ram.Value;

            // Windows version: product, release and build only.
            string product = ReadReg(OsKey, "ProductName") as string ?? "";
            string edition = ReadReg(OsKey, "EditionID") as string ?? "";
            string display = ReadReg(OsKey, "DisplayVersion") as string ?? (ReadReg(OsKey, "ReleaseId") as string ?? "");
            string build = ReadReg(OsKey, "CurrentBuild") as string ?? (ReadReg(OsKey, "CurrentBuildNumber") as string ?? "");
            int? ubr = ReadReg(OsKey, "UBR") is int u ? u : null;
            int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out int buildNo);
            // Windows 11 still says "Windows 10" in ProductName; the build number is what tells them apart.
            string name = product;
            if (buildNo >= 22000 && name.StartsWith("Windows 10", StringComparison.Ordinal))
                name = "Windows 11" + name.Substring("Windows 10".Length);
            o["windows"] = new JsonObject
            {
                ["name"] = name,
                ["productNameInRegistry"] = product,
                ["edition"] = edition,
                ["release"] = display,
                ["build"] = build + (ubr.HasValue ? "." + ubr.Value.ToString(CultureInfo.InvariantCulture) : ""),
                ["osDescription"] = RuntimeInformation.OSDescription,
                ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
                ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            };

            o["ucrtbase"] = UcrtVersion();

            (bool? ac, bool? battery) = Native.Power();
            o["power"] = new JsonObject
            {
                ["onMainsPower"] = ac.HasValue ? JsonValue.Create(ac.Value) : null,
                ["hasBattery"] = battery.HasValue ? JsonValue.Create(battery.Value) : null,
            };

            o["inheritedDotnetSettings"] = InheritedSettings();
            return o;
        }

        /// <summary>
        /// Settings in this process's environment that could change how .NET runs code
        /// (DOTNET_*, COMPlus_*, CORECLR_*). Names only - except the instruction-set switches, whose
        /// values are 0/1 and matter to the analysis. The children never inherit any of them.
        /// </summary>
        public static JsonArray InheritedSettings()
        {
            JsonArray a = new JsonArray();
            List<string> names = new List<string>();
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                string k = e.Key as string ?? "";
                if (!IsRuntimeSetting(k)) continue;
                if (IsOurs(k)) continue;
                names.Add(k);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string k in names)
            {
                string v = Environment.GetEnvironmentVariable(k) ?? "";
                bool showValue = k.IndexOf("Enable", StringComparison.OrdinalIgnoreCase) >= 0 && v.Length <= 8;
                a.Add(showValue ? k + "=" + v : k);
            }

            return a;
        }

        public static bool IsRuntimeSetting(string name) =>
            name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase);

        /// <summary>The two the launcher sets itself.</summary>
        public static bool IsOurs(string name) =>
            string.Equals(name, "DOTNET_ROOT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "DOTNET_ROOT_X64", StringComparison.OrdinalIgnoreCase);

        private static string UcrtVersion()
        {
            try
            {
                string p = Path.Combine(Environment.SystemDirectory, "ucrtbase.dll");
                if (!File.Exists(p)) return "(not found)";
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(p);
                // The version STRING, not the numeric parts: Windows reports the numeric file version of
                // its own DLLs as 6.2.x to a program without a Windows 10 compatibility manifest (seen
                // here: 6.2.19041.3636 against the true 10.0.19041.3636), and this apphost has none.
                string s = (v.FileVersion ?? "").Trim();
                int sp = s.IndexOf(' ');
                if (sp > 0) s = s.Substring(0, sp);
                return s.Length > 0
                    ? s
                    : v.FileMajorPart + "." + v.FileMinorPart + "." + v.FileBuildPart + "." + v.FilePrivatePart;
            }
            catch (Exception)
            {
                return "(not readable)";
            }
        }

        private static object? ReadReg(string subKey, string name)
        {
            try
            {
                using RegistryKey? k = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
                return k?.GetValue(name);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
