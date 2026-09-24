using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace SeedLab.MachineReport
{
    /// <summary>One instruction-set level: the switches a child process is started with.</summary>
    public sealed class Level
    {
        public Level(string id, string title, string maxTier, params (string Name, string Value)[] knobs)
        {
            Id = id; Title = title; MaxTier = maxTier; Knobs = knobs;
        }

        public string Id { get; }
        public string Title { get; }

        /// <summary>The highest SIMD tier the child may report for the switches to have taken effect.</summary>
        public string MaxTier { get; }

        public (string Name, string Value)[] Knobs { get; }

        public string KnobText()
        {
            if (Knobs.Length == 0) return "no switches";
            List<string> l = new List<string>();
            foreach ((string n, string v) in Knobs) l.Add(n + "=" + v);
            return string.Join(" ", l);
        }

        /// <summary>
        /// The four levels. .NET 10 ignores DOTNET_EnableAVX512F (measured on 10.0.12: every
        /// Avx512* class stays supported); DOTNET_EnableAVX512 is the switch that turns AVX-512 off,
        /// so both are set and the child reports what it really got.
        /// </summary>
        public static readonly Level[] All =
        {
            new Level("as-found", "as found: every instruction set .NET uses on this CPU", "avx512"),
            new Level("no-avx512", "AVX-512 switched off", "avx2",
                ("DOTNET_EnableAVX512", "0"), ("DOTNET_EnableAVX512F", "0")),
            new Level("no-avx2", "AVX2 switched off", "avx", ("DOTNET_EnableAVX2", "0")),
            new Level("scalar", "every hardware intrinsic switched off (scalar code only)", "scalar",
                ("DOTNET_EnableHWIntrinsic", "0")),
        };

        public static int TierRank(string tier) => tier switch
        {
            "scalar" => 0,
            "sse2" => 1,
            "avx" => 2,
            "avx2" => 3,
            "avx512" => 4,
            _ => 99,
        };
    }

    /// <summary>
    /// Starts this same program as a child process with a controlled environment, relays its
    /// progress lines, and reads the JSON it leaves in the work folder.
    /// </summary>
    public static class Children
    {
        public sealed class Result
        {
            public int ExitCode = -1;
            public bool TimedOut;
            public JsonObject? Json;
            public string Stderr = "";
            public double Seconds;
        }

        private static Process? s_current;

        /// <summary>Stops the running child, if any (Ctrl+C).</summary>
        public static void KillCurrent()
        {
            try { s_current?.Kill(entireProcessTree: true); }
            catch (Exception) { }
        }

        public static Result Run(IList<string> args, (string Name, string Value)[] knobs, string outFile,
                                 TimeSpan timeout, Action<string> relay)
        {
            Result res = new Result();
            Stopwatch sw = Stopwatch.StartNew();
            string self = Environment.ProcessPath ?? Path.Combine(Package.AppDir, "SeedLab.MachineReport.exe");
            ProcessStartInfo psi = new ProcessStartInfo(self)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Package.Root,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Started through dotnet.exe (a development run): hand it the dll as well.
            if (Path.GetFileName(self).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(Path.Combine(Package.AppDir, "SeedLab.MachineReport.dll"));
            foreach (string a in args) psi.ArgumentList.Add(a);

            // A clean runtime environment: nothing the tester's machine sets for .NET is inherited,
            // then the bundled runtime and this level's switches are set.
            List<string> drop = new List<string>();
            foreach (string k in psi.Environment.Keys) if (MachineFacts.IsRuntimeSetting(k)) drop.Add(k);
            foreach (string k in drop) psi.Environment.Remove(k);
            psi.Environment["DOTNET_ROOT"] = Package.DotnetDir;
            psi.Environment["DOTNET_ROOT_X64"] = Package.DotnetDir;
            foreach ((string n, string v) in knobs) psi.Environment[n] = v;

            StringBuilder err = new StringBuilder();
            using Process p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.StartsWith("@", StringComparison.Ordinal)) relay(Package.Sanitize(e.Data.Substring(1)));
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (err)
                {
                    if (err.Length < 20000) err.Append(e.Data).Append('\n');
                }
            };

            try
            {
                p.Start();
                s_current = p;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)))
                {
                    res.TimedOut = true;
                    try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                    p.WaitForExit(10000);
                }
                else
                {
                    p.WaitForExit();   // drains the redirected streams
                }

                res.ExitCode = res.TimedOut ? -1 : p.ExitCode;
            }
            catch (Exception e)
            {
                err.Append("could not start the check process: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            finally
            {
                s_current = null;
            }

            lock (err) res.Stderr = Package.Sanitize(err.ToString());
            try
            {
                if (File.Exists(outFile)) res.Json = JsonNode.Parse(File.ReadAllText(outFile)) as JsonObject;
            }
            catch (Exception e)
            {
                res.Stderr += "\nthe check process's result file could not be read: " + e.GetType().Name;
            }

            res.Seconds = sw.Elapsed.TotalSeconds;
            return res;
        }
    }
}
