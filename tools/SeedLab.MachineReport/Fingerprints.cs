using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.MachineReport
{
    /// <summary>One world fingerprint: what is sampled, where, and how the bytes are laid out.</summary>
    public sealed class FingerprintDef
    {
        public FingerprintDef(string id, string kind, int seed, int size, float step)
        {
            Id = id; Kind = kind; Seed = seed; Size = size; Step = step;
        }

        public string Id { get; }

        /// <summary>"biome+base", "height" or "rivers".</summary>
        public string Kind { get; }

        public int Seed { get; }
        public int Size { get; }
        public float Step { get; }

        public string Describe() => Kind switch
        {
            "biome+base" => "GetBiome + base height, " + Size + "x" + Size + " grid at " + F(Step) + " m, seed " + Seed,
            "height" => "GetHeight after pre-generation, " + Size + "x" + Size + " grid at " + F(Step) + " m, seed " + Seed,
            "rivers" => "pre-generated lakes, rivers, streams and river points, seed " + Seed,
            _ => Kind,
        };

        /// <summary>The byte layout the SHA-256 is taken over, so the file can be reproduced without this source.</summary>
        public string Layout => Kind switch
        {
            "biome+base" =>
                "rows z = w(r), r = 0..N-1; within a row x = w(c), c = 0..N-1; w(i) = (i - N/2) * step + step/2 "
                + "(float, exact); per cell: int32 LE (int)GetBiome(x, z), then int32 LE float bits of "
                + "GetBaseHeight(x, z) (WorldGeneratorPort.GetBaseHeightPublic); generator built with "
                + "deferPregeneration: true",
            "height" =>
                "rows z = w(r), within a row x = w(c), w(i) = (i - N/2) * step + step/2; per cell: int32 LE "
                + "float bits of GetHeight(x, z) on a pre-generated world, each worker on its own Fork()",
            "rivers" =>
                "all little-endian: lake count, each lake x,y float bits; river count, each river p0.x p0.y "
                + "p1.x p1.y center.x center.y widthMin widthMax curveWidth curveWavelength float bits; the "
                + "same for streams (GetStreams); river-point cell count, cells sorted by (x, y), each: x, y, "
                + "point count, each point p.x p.y w w2 float bits",
            _ => "",
        };

        public string Canonical =>
            Id + "|" + Kind + "|" + Seed.ToString(CultureInfo.InvariantCulture) + "|"
            + Size.ToString(CultureInfo.InvariantCulture) + "|" + F(Step) + "|" + Layout;

        private static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whole-world fingerprints, the same shape SeedLab's SIMD work was proven with: biome plus base
    /// height over a 1024^2 grid at 24 m for three seeds, and pre-generated heights over 512^2 for
    /// two, plus the pre-generated river network itself. Every cell's value goes into a SHA-256, so
    /// one differing bit anywhere in a world changes the fingerprint.
    /// </summary>
    public static class Fingerprints
    {
        public static readonly FingerprintDef[] All =
        {
            new FingerprintDef("biome-base-dev", "biome+base", -1772362158, 1024, 24f),
            new FingerprintDef("biome-base-holdout", "biome+base", 319486907, 1024, 24f),
            new FingerprintDef("biome-base-12345", "biome+base", 12345, 1024, 24f),
            new FingerprintDef("height-dev", "height", -1772362158, 512, 48f),
            new FingerprintDef("height-holdout", "height", 319486907, 512, 48f),
            new FingerprintDef("rivers-dev", "rivers", -1772362158, 0, 0f),
            new FingerprintDef("rivers-holdout", "rivers", 319486907, 0, 0f),
        };

        /// <summary>A hash of every definition, so a reference made with different definitions is recognised.</summary>
        public static string DefinitionsHash()
        {
            StringBuilder sb = new StringBuilder();
            foreach (FingerprintDef d in All) sb.Append(d.Canonical).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        }

        public static string Compute(FingerprintDef d, int threads) => d.Kind switch
        {
            "biome+base" => BiomeBase(d, threads),
            "height" => Height(d, threads),
            "rivers" => Rivers(d),
            _ => throw new InvalidOperationException("unknown fingerprint kind " + d.Kind),
        };

        private static float W(int i, int n, float step) => (float)(i - n / 2) * step + step * 0.5f;

        /// <summary>
        /// Fills rows on <paramref name="threads"/> threads, each with its own generator handle (a
        /// handle is single-threaded), taking the next row from a shared counter. Which thread
        /// computes a row cannot change its bytes: every value is a pure function of the seed and the
        /// coordinates.
        /// </summary>
        private static void Rows(int n, int threads, Func<int, Action<int>> makeWorker)
        {
            int next = -1;
            int t = Math.Max(1, Math.Min(threads, n));
            Exception? failure = null;
            Thread[] pool = new Thread[t];
            for (int k = 0; k < t; k++)
            {
                Action<int> row = makeWorker(k);
                pool[k] = new Thread(() =>
                {
                    try
                    {
                        int r;
                        while ((r = Interlocked.Increment(ref next)) < n) row(r);
                    }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref failure, e, null);
                    }
                }) { IsBackground = true };
            }

            foreach (Thread th in pool) th.Start();
            foreach (Thread th in pool) th.Join();
            if (failure != null) throw new InvalidOperationException("a fingerprint worker failed: " + failure.Message, failure);
        }

        private static string BiomeBase(FingerprintDef d, int threads)
        {
            int n = d.Size;
            byte[] buf = new byte[checked(n * n * 8)];
            Rows(n, threads, _ =>
            {
                WorldGeneratorPort g = new WorldGeneratorPort(d.Seed, 2, menu: false, deferPregeneration: true);
                return r =>
                {
                    float z = W(r, n, d.Step);
                    Span<byte> row = buf.AsSpan(r * n * 8, n * 8);
                    for (int c = 0; c < n; c++)
                    {
                        float x = W(c, n, d.Step);
                        Biome b = g.GetBiome(x, z);
                        float h = g.GetBaseHeightPublic(x, z);
                        BinaryPrimitives.WriteInt32LittleEndian(row.Slice(c * 8), (int)b);
                        BinaryPrimitives.WriteInt32LittleEndian(row.Slice(c * 8 + 4), BitConverter.SingleToInt32Bits(h));
                    }
                };
            });
            return Hex(SHA256.HashData(buf));
        }

        private static string Height(FingerprintDef d, int threads)
        {
            int n = d.Size;
            byte[] buf = new byte[checked(n * n * 4)];
            WorldGeneratorPort parent = new WorldGeneratorPort(d.Seed, 2, menu: false);
            // Forks are made here, on one thread, before any worker starts; the parent itself is never
            // queried, so no fork can inherit a river cache left behind by pre-generation.
            int t = Math.Max(1, Math.Min(threads, n));
            WorldGeneratorPort[] forks = new WorldGeneratorPort[t];
            for (int k = 0; k < t; k++) forks[k] = parent.Fork();
            Rows(n, t, k =>
            {
                WorldGeneratorPort g = forks[k];
                return r =>
                {
                    float z = W(r, n, d.Step);
                    Span<byte> row = buf.AsSpan(r * n * 4, n * 4);
                    for (int c = 0; c < n; c++)
                    {
                        float x = W(c, n, d.Step);
                        BinaryPrimitives.WriteInt32LittleEndian(row.Slice(c * 4), BitConverter.SingleToInt32Bits(g.GetHeight(x, z)));
                    }
                };
            });
            return Hex(SHA256.HashData(buf));
        }

        private static string Rivers(FingerprintDef d)
        {
            WorldGeneratorPort g = new WorldGeneratorPort(d.Seed, 2, menu: false);
            using MemoryStream ms = new MemoryStream();
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                IReadOnlyList<Vec2>? lakes = g.GetLakes();
                w.Write(lakes?.Count ?? 0);
                if (lakes != null)
                {
                    foreach (Vec2 p in lakes) { Bits(w, p.x); Bits(w, p.y); }
                }

                foreach (IReadOnlyList<WorldGeneratorPort.River> list in new[] { g.GetRivers(), g.GetStreams() })
                {
                    w.Write(list.Count);
                    foreach (WorldGeneratorPort.River r in list)
                    {
                        Bits(w, r.p0.x); Bits(w, r.p0.y); Bits(w, r.p1.x); Bits(w, r.p1.y);
                        Bits(w, r.center.x); Bits(w, r.center.y);
                        Bits(w, r.widthMin); Bits(w, r.widthMax); Bits(w, r.curveWidth); Bits(w, r.curveWavelength);
                    }
                }

                IReadOnlyDictionary<Vec2i, WorldGeneratorPort.RiverPoint[]> pts = g.GetRiverPoints();
                List<Vec2i> keys = new List<Vec2i>(pts.Keys);
                keys.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
                w.Write(keys.Count);
                foreach (Vec2i k in keys)
                {
                    WorldGeneratorPort.RiverPoint[] arr = pts[k];
                    w.Write(k.x); w.Write(k.y); w.Write(arr.Length);
                    foreach (WorldGeneratorPort.RiverPoint p in arr)
                    {
                        Bits(w, p.p.x); Bits(w, p.p.y); Bits(w, p.w); Bits(w, p.w2);
                    }
                }
            }

            return Hex(SHA256.HashData(ms.ToArray()));
        }

        private static void Bits(BinaryWriter w, float f) => w.Write(BitConverter.SingleToInt32Bits(f));

        private static string Hex(byte[] h) => Convert.ToHexString(h).ToLowerInvariant();
    }
}
