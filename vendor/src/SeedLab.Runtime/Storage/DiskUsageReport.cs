using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SeedLab.Runtime.Hardware;

namespace SeedLab.Runtime.Storage
{
    /// <summary>One line of the usage report.</summary>
    public sealed class DiskUsageEntry
    {
        public DiskUsageEntry(string name, string path, long bytes, int files, bool exists)
        {
            Name = name; Path = path; Bytes = bytes; Files = files; Exists = exists;
        }

        public string Name { get; }
        public string Path { get; }
        public long Bytes { get; }
        public int Files { get; }
        public bool Exists { get; }
    }

    /// <summary>
    /// "What is SeedLab using on disk, and where" - the report behind <c>vseed clean --dry-run</c> and
    /// the web UI's storage panel. It walks only SeedLab's own directories plus any extra path the
    /// caller names (a results file, an output folder), never the user's wider disk.
    /// </summary>
    public sealed class DiskUsageReport
    {
        private DiskUsageReport(string rootPath, IReadOnlyList<DiskUsageEntry> entries, VolumeInfo volume)
        {
            RootPath = rootPath;
            Entries = entries;
            Volume = volume;
        }

        public string RootPath { get; }
        public IReadOnlyList<DiskUsageEntry> Entries { get; }

        /// <summary>The volume the cache root sits on, with its free space.</summary>
        public VolumeInfo Volume { get; }

        public long TotalBytes
        {
            get
            {
                long t = 0;
                foreach (DiskUsageEntry e in Entries) t += e.Bytes;
                return t;
            }
        }

        public int TotalFiles
        {
            get
            {
                int t = 0;
                foreach (DiskUsageEntry e in Entries) t += e.Files;
                return t;
            }
        }

        public static DiskUsageReport Measure(CacheRoot root, params string[] extraPaths)
        {
            List<DiskUsageEntry> entries = new List<DiskUsageEntry>();
            foreach ((string name, string dir) in root.Categories)
            {
                (long bytes, int files) = Walk(dir);
                entries.Add(new DiskUsageEntry(name, dir, bytes, files, Directory.Exists(dir)));
            }

            if (extraPaths != null)
            {
                foreach (string p in extraPaths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    if (File.Exists(p))
                    {
                        long len = 0;
                        try { len = new FileInfo(p).Length; } catch (Exception) { }
                        entries.Add(new DiskUsageEntry("output", p, len, 1, true));
                    }
                    else
                    {
                        (long bytes, int files) = Walk(p);
                        entries.Add(new DiskUsageEntry("output", p, bytes, files, Directory.Exists(p)));
                    }
                }
            }

            return new DiskUsageReport(root.Path, entries, VolumeInfo.For(root.Path));
        }

        public static long DirectorySize(string dir) => Walk(dir).bytes;

        private static (long bytes, int files) Walk(string dir)
        {
            long bytes = 0;
            int files = 0;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return (0, 0);
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { bytes += new FileInfo(f).Length; files++; }
                    catch (Exception) { /* vanished mid-walk */ }
                }
            }
            catch (Exception) { /* unreadable subtree: report what we could count */ }
            return (bytes, files);
        }

        public string Format()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("  cache root  ").Append(RootPath).Append('\n');
            foreach (DiskUsageEntry e in Entries)
            {
                sb.Append("  ").Append(e.Name.PadRight(12))
                  .Append(Bytes.Human(e.Bytes).PadLeft(10))
                  .Append("  ").Append(e.Files.ToString()).Append(" file").Append(e.Files == 1 ? "" : "s")
                  .Append("  ").Append(e.Path).Append('\n');
            }
            sb.Append("  total       ").Append(Bytes.Human(TotalBytes).PadLeft(10))
              .Append("  ").Append(TotalFiles).Append(" files\n");
            sb.Append("  volume      ").Append(Volume).Append('\n');
            return sb.ToString();
        }
    }
}
