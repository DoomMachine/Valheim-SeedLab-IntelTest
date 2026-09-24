using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SeedLab.Runtime.Hardware
{
    /// <summary>
    /// What a probe could learn about the volume a given path lives on, through <see cref="DriveInfo"/>
    /// alone. Every field is what the BCL reported; nothing here is inferred.
    /// </summary>
    public sealed class VolumeInfo
    {
        private VolumeInfo(string queriedPath, string name, string rootPath, string format,
                           long totalBytes, long freeBytes, bool ready, string source)
        {
            QueriedPath = queriedPath;
            Name = name;
            RootPath = rootPath;
            Format = format;
            TotalBytes = totalBytes;
            FreeBytes = freeBytes;
            IsReady = ready;
            Source = source;
        }

        /// <summary>The path the caller asked about (it need not exist yet).</summary>
        public string QueriedPath { get; }

        /// <summary>The drive name as the BCL reports it, e.g. <c>E:\</c> or <c>/</c>.</summary>
        public string Name { get; }

        public string RootPath { get; }

        /// <summary>File system name, e.g. NTFS or ext4; empty when the drive would not answer.</summary>
        public string Format { get; }

        public long TotalBytes { get; }

        /// <summary>
        /// <see cref="DriveInfo.AvailableFreeSpace"/> - the space available to THIS user, which is the
        /// number a quota or a reservation actually lets us write, not <c>TotalFreeSpace</c>.
        /// </summary>
        public long FreeBytes { get; }

        public bool IsReady { get; }

        /// <summary>How the volume was matched: "root", "longest-prefix" or "unavailable".</summary>
        public string Source { get; }

        public double FreeFraction => TotalBytes > 0 ? (double)FreeBytes / TotalBytes : 0.0;

        /// <summary>
        /// The volume holding <paramref name="path"/>. On Windows this is the path root; on Unix, where
        /// every mount point is a prefix of some path, it is the mounted drive whose root is the longest
        /// prefix of the absolute path - so <c>/home/x</c> on its own mount is not confused with <c>/</c>.
        /// Never throws: an unreadable or missing volume comes back with <see cref="IsReady"/> false and
        /// zero sizes, because a probe that throws is a probe that stops a search from starting.
        /// </summary>
        public static VolumeInfo For(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) path = Directory.GetCurrentDirectory();

            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { full = path; }

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception) { drives = Array.Empty<DriveInfo>(); }

            DriveInfo? best = null;
            int bestLen = -1;
            string how = "unavailable";

            foreach (DriveInfo d in drives)
            {
                string root;
                try { root = d.RootDirectory.FullName; }
                catch (Exception) { continue; }
                if (root.Length == 0) continue;

                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && root.Length > bestLen)
                {
                    best = d;
                    bestLen = root.Length;
                    how = root.Length <= 3 ? "root" : "longest-prefix";
                }
            }

            if (best == null)
            {
                return new VolumeInfo(full, "", "", "", 0, 0, false, "unavailable");
            }

            try
            {
                bool ready = best.IsReady;
                return new VolumeInfo(full, best.Name, best.RootDirectory.FullName,
                                      ready ? best.DriveFormat : "",
                                      ready ? best.TotalSize : 0L,
                                      ready ? best.AvailableFreeSpace : 0L,
                                      ready, how);
            }
            catch (Exception)
            {
                return new VolumeInfo(full, best.Name, "", "", 0, 0, false, "unavailable");
            }
        }

        /// <summary>All ready volumes, for a "what have I got" report. Never throws.</summary>
        public static IReadOnlyList<VolumeInfo> All()
        {
            List<VolumeInfo> list = new List<VolumeInfo>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception) { return list; }

            foreach (DriveInfo d in drives)
            {
                try
                {
                    if (!d.IsReady) continue;
                    list.Add(new VolumeInfo(d.RootDirectory.FullName, d.Name, d.RootDirectory.FullName,
                                            d.DriveFormat, d.TotalSize, d.AvailableFreeSpace, true, "root"));
                }
                catch (Exception) { /* a drive that will not answer is simply not listed */ }
            }
            return list;
        }

        public override string ToString()
        {
            if (!IsReady) return QueriedPath + " -> volume unavailable";
            return Name + " (" + Format + ") " + Bytes.Human(FreeBytes) + " free of " + Bytes.Human(TotalBytes)
                   + " (" + (FreeFraction * 100.0).ToString("0.#", CultureInfo.InvariantCulture) + " %)";
        }
    }
}
