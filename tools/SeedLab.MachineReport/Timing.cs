using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Text.Json.Nodes;
using SeedLab.WorldGen;

namespace SeedLab.MachineReport
{
    /// <summary>
    /// A short timing run: how fast this machine does the two costs every SeedLab search is made of.
    ///
    /// <list type="bullet">
    /// <item><b>biome grid</b> - a world's biome map at 256 x 256 points, 80 m apart (the whole world
    /// disc), on a fresh generator per seed that skips pre-generation - the cheapest whole-world
    /// question a search asks. Reported as grid points per second and seeds per second.</item>
    /// <item><b>pre-generation</b> - building a world's lakes, rivers and streams, which every question
    /// about heights, rivers or locations pays once per seed. Reported as seeds per second.</item>
    /// </list>
    /// Each configuration is measured several times and the median is reported beside the spread.
    /// </summary>
    public static class Timing
    {
        public const int GridN = 256;
        public const float GridStep = 80f;

        public static JsonObject Run(double seconds, int reps, Action<string> progress)
        {
            int all = Math.Max(1, Environment.ProcessorCount);
            int half = Math.Max(1, all / 2);
            List<int> biomeThreads = Distinct(1, half, all);
            List<int> pregenThreads = Distinct(1, all);

            progress("warming up (so the measured code is fully compiled)");
            Measure(Work.BiomeGrid, 1, Math.Min(2.0, seconds));
            Measure(Work.Pregen, 1, Math.Min(2.0, seconds));

            JsonArray rows = new JsonArray();
            foreach (int t in biomeThreads) rows.Add(Config(Work.BiomeGrid, t, seconds, reps, progress));
            foreach (int t in pregenThreads) rows.Add(Config(Work.Pregen, t, seconds, reps, progress));

            return new JsonObject
            {
                ["secondsPerRep"] = seconds,
                ["reps"] = reps,
                ["logicalProcessors"] = all,
                ["biomeGrid"] = GridN + "x" + GridN + " points, " + GridStep + " m apart, fresh generator per seed, no pre-generation",
                ["pregeneration"] = "new WorldGeneratorPort(seed): lakes, rivers, streams and river points",
                ["rows"] = rows,
            };
        }

        private enum Work { BiomeGrid, Pregen }

        private static List<int> Distinct(params int[] xs)
        {
            List<int> l = new List<int>();
            foreach (int x in xs) if (!l.Contains(x)) l.Add(x);
            return l;
        }

        private static JsonObject Config(Work w, int threads, double seconds, int reps, Action<string> progress)
        {
            string name = w == Work.BiomeGrid ? "biome grid" : "pre-generation";
            progress(name + ", " + threads + (threads == 1 ? " thread" : " threads") + " (" + reps + " x " + seconds + " s)");
            List<double> seedRates = new List<double>();
            JsonArray samples = new JsonArray();
            for (int r = 0; r < reps; r++)
            {
                (long seeds, double secs) = Measure(w, threads, seconds);
                double rate = seeds / secs;
                seedRates.Add(rate);
                samples.Add(Math.Round(rate, 3));
            }

            seedRates.Sort();
            double median = seedRates[seedRates.Count / 2];
            JsonObject o = new JsonObject
            {
                ["work"] = name,
                ["threads"] = threads,
                ["seedsPerSecondMedian"] = Math.Round(median, 3),
                ["seedsPerSecondMin"] = Math.Round(seedRates[0], 3),
                ["seedsPerSecondMax"] = Math.Round(seedRates[seedRates.Count - 1], 3),
                ["seedsPerSecondSamples"] = samples,
            };
            if (w == Work.BiomeGrid)
                o["pointsPerSecondMedian"] = Math.Round(median * GridN * GridN, 0);
            return o;
        }

        private static int s_nextSeed;
        private static long s_sink;

        /// <summary>
        /// Runs <paramref name="threads"/> workers until <paramref name="seconds"/> have passed; each
        /// finishes the seed it is on. Returns the seeds completed and the wall time they took.
        /// </summary>
        private static (long seeds, double secs) Measure(Work w, int threads, double seconds)
        {
            long done = 0;
            Stopwatch sw = Stopwatch.StartNew();
            long deadline = (long)(seconds * Stopwatch.Frequency);
            Thread[] pool = new Thread[threads];
            for (int k = 0; k < threads; k++)
            {
                pool[k] = new Thread(() =>
                {
                    long local = 0, sink = 0;
                    while (sw.ElapsedTicks < deadline)
                    {
                        int seed = SeedFor(Interlocked.Increment(ref s_nextSeed));
                        sink += w == Work.BiomeGrid ? BiomeGrid(seed) : Pregen(seed);
                        local++;
                    }

                    Interlocked.Add(ref done, local);
                    Interlocked.Add(ref s_sink, sink);
                }) { IsBackground = true };
            }

            foreach (Thread t in pool) t.Start();
            foreach (Thread t in pool) t.Join();
            return (done, sw.Elapsed.TotalSeconds);
        }

        /// <summary>A fixed pseudo-random spread of seeds (SplitMix64), so every machine times the same worlds.</summary>
        private static int SeedFor(int i)
        {
            ulong z = unchecked((ulong)i * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return unchecked((int)(uint)z);
        }

        private static long BiomeGrid(int seed)
        {
            WorldGeneratorPort g = new WorldGeneratorPort(seed, 2, menu: false, deferPregeneration: true);
            long acc = 0;
            for (int r = 0; r < GridN; r++)
            {
                float z = (float)(r - GridN / 2) * GridStep + GridStep * 0.5f;
                for (int c = 0; c < GridN; c++)
                {
                    float x = (float)(c - GridN / 2) * GridStep + GridStep * 0.5f;
                    acc += (int)g.GetBiome(x, z);
                }
            }

            return acc;
        }

        private static long Pregen(int seed)
        {
            WorldGeneratorPort g = new WorldGeneratorPort(seed, 2, menu: false);
            return g.GetRivers().Count;
        }
    }
}
