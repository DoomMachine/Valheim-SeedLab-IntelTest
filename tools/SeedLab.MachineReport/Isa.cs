using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json.Nodes;

namespace SeedLab.MachineReport
{
    /// <summary>
    /// What this process's JIT will use: every x86 instruction-set class .NET knows about, with its
    /// <c>IsSupported</c> answer, the vector widths, and the CPU's own identification from CPUID.
    /// These answers change with the DOTNET_Enable* switches, which is the point of the levels.
    /// </summary>
    public static class Isa
    {
        /// <summary>
        /// Every public class in System.Runtime.Intrinsics.X86 with a static IsSupported, by name
        /// (nested classes as Outer.Inner, e.g. Avx512F.VL). Found by reflection, so instruction sets
        /// newer than this code (Avx10v2 and later) are listed too.
        /// </summary>
        public static SortedDictionary<string, bool> All()
        {
            SortedDictionary<string, bool> d = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            Type[] types;
            try { types = typeof(X86Base).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null)!; }

            foreach (Type t in types)
            {
                if (t.Namespace != "System.Runtime.Intrinsics.X86") continue;
                if (!IsVisible(t)) continue;
                PropertyInfo? p = t.GetProperty("IsSupported",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p == null || p.PropertyType != typeof(bool)) continue;
                string name = t.Name;
                for (Type? o = t.DeclaringType; o != null; o = o.DeclaringType) name = o.Name + "." + name;
                try { d[name] = (bool)p.GetValue(null)!; }
                catch (Exception) { /* a class that will not answer simply is not listed */ }
            }

            return d;
        }

        private static bool IsVisible(Type t)
        {
            for (Type? x = t; x != null; x = x.DeclaringType)
            {
                if (x.DeclaringType == null ? !x.IsPublic : !x.IsNestedPublic) return false;
            }

            return true;
        }

        /// <summary>The instruction sets named in the report's summary line, in order.</summary>
        public static readonly string[] Headline =
        {
            "Sse", "Sse2", "Sse3", "Ssse3", "Sse41", "Sse42", "Popcnt", "Lzcnt", "Bmi1", "Bmi2",
            "Avx", "Avx2", "Fma", "AvxVnni", "Avx512F", "Avx512F.VL", "Avx512BW", "Avx512CD",
            "Avx512DQ", "Avx512Vbmi", "Avx512Vbmi2", "Avx10v1", "Avx10v2",
        };

        public static JsonObject Vectors()
        {
            return new JsonObject
            {
                ["vector128Accelerated"] = Vector128.IsHardwareAccelerated,
                ["vector256Accelerated"] = Vector256.IsHardwareAccelerated,
                ["vector512Accelerated"] = Vector512.IsHardwareAccelerated,
                ["vectorTBytes"] = Vector<byte>.Count,
            };
        }

        /// <summary>
        /// The one-word description of how far up the SIMD ladder this process goes, used to tell the
        /// levels apart: avx512, avx2, avx, sse or scalar.
        /// </summary>
        public static string Tier(IReadOnlyDictionary<string, bool> f)
        {
            bool Has(string n) => f.TryGetValue(n, out bool v) && v;
            if (Has("Avx512F")) return "avx512";
            if (Has("Avx2")) return "avx2";
            if (Has("Avx")) return "avx";
            if (Has("Sse2")) return "sse2";
            return "scalar";
        }

        /// <summary>
        /// The CPU's identification from CPUID: vendor, family/model/stepping and the raw feature
        /// registers. Leaf 1 EBX (which carries the current core's APIC id) and the processor serial
        /// number leaf are deliberately not read. Null when CPUID is unavailable to this process
        /// (DOTNET_EnableHWIntrinsic=0 turns X86Base off).
        /// </summary>
        public static JsonObject? Cpuid()
        {
            if (!X86Base.IsSupported) return null;
            try
            {
                (int maxLeaf, int b0, int c0, int d0) = X86Base.CpuId(0, 0);
                string vendor = Ascii(b0) + Ascii(d0) + Ascii(c0);
                JsonObject o = new JsonObject { ["vendor"] = vendor };

                if (maxLeaf >= 1)
                {
                    (int a1, _, int c1, int d1) = X86Base.CpuId(1, 0);
                    int baseFamily = (a1 >> 8) & 0xF;
                    int extFamily = (a1 >> 20) & 0xFF;
                    int baseModel = (a1 >> 4) & 0xF;
                    int extModel = (a1 >> 16) & 0xF;
                    int family = baseFamily == 0xF ? baseFamily + extFamily : baseFamily;
                    int model = baseFamily == 0x6 || baseFamily == 0xF ? (extModel << 4) + baseModel : baseModel;
                    o["family"] = family;
                    o["model"] = model;
                    o["stepping"] = a1 & 0xF;
                    o["signature"] = Hex(a1);
                    o["leaf1_ecx"] = Hex(c1);
                    o["leaf1_edx"] = Hex(d1);
                }

                if (maxLeaf >= 7)
                {
                    (int a7, int b7, int c7, int d7) = X86Base.CpuId(7, 0);
                    o["leaf7_ebx"] = Hex(b7);
                    o["leaf7_ecx"] = Hex(c7);
                    o["leaf7_edx"] = Hex(d7);
                    o["hybridFlag"] = (d7 & (1 << 15)) != 0;
                    if (a7 >= 1)
                    {
                        (int a71, _, _, int d71) = X86Base.CpuId(7, 1);
                        o["leaf7_1_eax"] = Hex(a71);
                        o["leaf7_1_edx"] = Hex(d71);
                    }
                }

                if (maxLeaf >= 0x24)
                {
                    (_, int b24, _, _) = X86Base.CpuId(0x24, 0);
                    o["leaf24_ebx"] = Hex(b24);
                }

                (int maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
                if ((uint)maxExt >= 0x80000001)
                {
                    (_, _, int ce, int de) = X86Base.CpuId(unchecked((int)0x80000001), 0);
                    o["ext1_ecx"] = Hex(ce);
                    o["ext1_edx"] = Hex(de);
                }

                o["maxLeaf"] = Hex(maxLeaf);
                return o;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Ascii(int v)
        {
            StringBuilder sb = new StringBuilder(4);
            for (int i = 0; i < 4; i++)
            {
                char ch = (char)((v >> (8 * i)) & 0xFF);
                sb.Append(ch >= ' ' && ch < 127 ? ch : '?');
            }

            return sb.ToString();
        }

        private static string Hex(int v) => "0x" + v.ToString("X8", CultureInfo.InvariantCulture);

        public static JsonObject ToJson(IReadOnlyDictionary<string, bool> f)
        {
            JsonObject o = new JsonObject();
            foreach (KeyValuePair<string, bool> kv in f) o[kv.Key] = kv.Value;
            return o;
        }
    }
}
