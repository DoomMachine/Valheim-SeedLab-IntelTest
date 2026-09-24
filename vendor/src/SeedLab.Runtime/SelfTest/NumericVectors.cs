using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SeedLab.Runtime.SelfTest
{
    /// <summary>
    /// The built-in self-test suite: the recorded results of <see cref="NumericCases"/>, embedded in the
    /// assembly.
    ///
    /// <para>They were recorded on the machine whose output the acceptance gates compare against the
    /// game itself, so reproducing them is evidence that this machine's libm and float evaluation agree
    /// with the ones SeedLab was verified on. It is not evidence about the generator - that needs a
    /// generator-level suite, which the host registers (see
    /// <see cref="MachineSelfTest.RequireGeneratorSuiteOnUnverifiedPlatform"/>).</para>
    ///
    /// <para>To re-record after adding a case, run the runtime tests with <c>--emit-vectors</c> and
    /// commit the file it writes. Recording on a machine that has NOT passed the full gates would throw
    /// away the only thing the vectors are for.</para>
    /// </summary>
    public sealed class NumericVectors : ISelfTestSuite
    {
        public const string ResourceName = "SeedLab.Runtime.SelfTest.selftest-vectors.txt";
        public const string FormatVersion = "seedlab-selftest-vectors 1";

        private readonly string _text;

        private NumericVectors(string text)
        {
            _text = text;
        }

        public string Name => "numerics";

        public string Describes =>
            "Math.Sin/Cos/Tan/Atan/Atan2/Pow/Sqrt/Exp/Log and float evaluation, bit for bit: "
            + "if these differ, a biome can flip at a threshold";

        /// <summary>Loads the embedded vectors. Throws when the assembly was built without them.</summary>
        public static NumericVectors Embedded()
        {
            using Stream? s = typeof(NumericVectors).Assembly.GetManifestResourceStream(ResourceName);
            if (s == null)
            {
                throw new InvalidOperationException(
                    "SeedLab.Runtime was built without its self-test vectors (" + ResourceName
                    + "). Rebuild the project; the vectors are an EmbeddedResource in its csproj.");
            }
            using StreamReader r = new StreamReader(s, Encoding.UTF8);
            return new NumericVectors(r.ReadToEnd());
        }

        public static NumericVectors FromText(string text) => new NumericVectors(text);

        /// <summary>SHA-256 of the vector text: part of the self-test stamp, so new vectors force a re-check.</summary>
        public string VectorsHash
        {
            get
            {
                byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(_text.Replace("\r\n", "\n")));
                return Convert.ToHexString(h).Substring(0, 16).ToLowerInvariant();
            }
        }

        public SelfTestSuiteResult Run(CancellationToken cancel)
        {
            Stopwatch sw = Stopwatch.StartNew();
            int checks = 0, failures = 0;
            string first = "";

            Dictionary<string, string> libm = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            Parse(_text, libm, values);

            foreach (NumericCases.LibmCase c in NumericCases.Libm())
            {
                cancel.ThrowIfCancellationRequested();
                if (!libm.TryGetValue(c.Key, out string? expected)) continue;
                checks++;
                string actual = Hex.Of(c.Evaluate());
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    failures++;
                    if (first.Length == 0)
                        first = "Math." + c.Fn + "(" + Fmt(c.A) + (c.Fn == "Atan2" || c.Fn == "Pow" ? ", " + Fmt(c.B) : "")
                                + ") gave 0x" + actual + ", recorded 0x" + expected;
                }
            }

            foreach (NumericCases.ValueCase v in NumericCases.Values())
            {
                cancel.ThrowIfCancellationRequested();
                if (!values.TryGetValue(v.Name, out string? expected)) continue;
                checks++;
                string actual = v.IsFloat ? Hex.Of(v.EvalFloat!()) : Hex.Of(v.EvalDouble!());
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    failures++;
                    if (first.Length == 0)
                        first = v.Name + " gave 0x" + actual + ", recorded 0x" + expected;
                }
            }

            // A vector file that covers fewer cases than the code defines is itself a failure: it would
            // silently stop testing whatever was added.
            int defined = NumericCases.Libm().Count + NumericCases.Values().Count;
            if (checks < defined)
            {
                failures++;
                if (first.Length == 0)
                    first = "the recorded vectors cover " + checks + " of " + defined
                            + " defined cases - re-record them with 'dotnet run --project tests/SeedLab.Runtime.Tests -- --emit-vectors'";
                checks = defined;
            }

            sw.Stop();
            return new SelfTestSuiteResult(Name, checks, failures, first, sw.Elapsed);
        }

        private static string Fmt(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        private static void Parse(string text, Dictionary<string, string> libm, Dictionary<string, string> values)
        {
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;

                if (parts[0] == "libm" && parts.Length == 5)
                    libm[parts[1] + " " + parts[2] + " " + parts[3]] = parts[4];
                else if ((parts[0] == "f32" || parts[0] == "f64") && parts.Length == 3)
                    values[parts[1]] = parts[2];
            }
        }

        /// <summary>
        /// Computes every case NOW and returns the file to record. Used by the tests' --emit-vectors
        /// mode; never called during a normal run, because a self-test that records what it finds proves
        /// nothing.
        /// </summary>
        public static string Record()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# ").Append(FormatVersion).Append('\n');
            sb.Append("# recorded ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(" on ").Append(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)
              .Append(", ").Append(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription).Append('\n');
            sb.Append("# libm <fn> <arg-a bits> <arg-b bits> <result bits>   |   f32/f64 <case> <result bits>\n");

            foreach (NumericCases.LibmCase c in NumericCases.Libm())
                sb.Append("libm ").Append(c.Key).Append(' ').Append(Hex.Of(c.Evaluate())).Append('\n');

            foreach (NumericCases.ValueCase v in NumericCases.Values())
            {
                sb.Append(v.IsFloat ? "f32 " : "f64 ").Append(v.Name).Append(' ')
                  .Append(v.IsFloat ? Hex.Of(v.EvalFloat!()) : Hex.Of(v.EvalDouble!())).Append('\n');
            }
            return sb.ToString();
        }
    }
}
