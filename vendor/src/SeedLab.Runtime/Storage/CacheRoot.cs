using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SeedLab.Runtime.Hardware;

namespace SeedLab.Runtime.Storage
{
    /// <summary>Where the cache root came from, so the user can be told which knob moves it.</summary>
    public enum CacheRootSource
    {
        Override = 0,
        Environment = 1,
        LocalApplicationData = 2,
        XdgCacheHome = 3,
        MacCaches = 4,
        Fallback = 5
    }

    public sealed class CacheRootOptions
    {
        public static CacheRootOptions Default => new CacheRootOptions();

        /// <summary>An explicit <c>--cache-dir</c>. Beats everything else.</summary>
        public string? Override { get; set; }

        /// <summary>Read <c>SEEDLAB_CACHE_DIR</c>. On by default.</summary>
        public bool ReadEnvironment { get; set; } = true;

        /// <summary>Create the directories on open. Off for a pure "where would it be" query.</summary>
        public bool Create { get; set; } = true;

        /// <summary>The folder name under the per-OS cache location.</summary>
        public string ApplicationFolder { get; set; } = "SeedLab";
    }

    /// <summary>
    /// The ONE wipeable place SeedLab is allowed to leave things: checkpoints, rendered maps, web tile
    /// caches, run manifests, per-process scratch, the self-test stamp and the session log. Nothing is written beside the
    /// user's working directory by default - that was defect 5 and defect 6 in the audit.
    ///
    /// <para>Per OS: <c>%LOCALAPPDATA%\SeedLab</c> on Windows, <c>$XDG_CACHE_HOME/seedlab</c> (or
    /// <c>~/.cache/seedlab</c>) on Linux, <c>~/Library/Caches/SeedLab</c> on macOS, with
    /// <c>SEEDLAB_CACHE_DIR</c> and an explicit option overriding all of it. Deleting the whole root at
    /// any time must never lose anything the user asked to keep.</para>
    /// </summary>
    public sealed class CacheRoot
    {
        private CacheRoot(string path, CacheRootSource source, string sourceDetail)
        {
            Path = path;
            Source = source;
            SourceDetail = sourceDetail;
        }

        public string Path { get; }
        public CacheRootSource Source { get; }

        /// <summary>The env var or folder the root came from, for the "what am I using" report.</summary>
        public string SourceDetail { get; }

        public string Checkpoints => Sub("checkpoints");
        public string Runs => Sub("runs");
        public string Maps => Sub("maps");
        public string Tiles => Sub("tiles");
        public string ScratchParent => Sub("scratch");
        public string SelfTest => Sub("selftest");

        /// <summary>
        /// The session log (<see cref="SessionLog"/>): <c>vseed.log</c>, emptied at the start of every
        /// session, and <c>vseed.log.1</c> .. <c>.4</c> while sessions overlap (2026-09-24).
        /// </summary>
        public string Logs => Sub("logs");

        private string Sub(string name) => System.IO.Path.Combine(Path, name);

        /// <summary>The categories a usage report and <c>vseed clean</c> both walk.</summary>
        public IReadOnlyList<(string Name, string Dir)> Categories => new[]
        {
            ("checkpoints", Checkpoints),
            ("runs", Runs),
            ("maps", Maps),
            ("tiles", Tiles),
            ("scratch", ScratchParent),
            ("selftest", SelfTest),
            ("logs", Logs)
        };

        public static CacheRoot Open(CacheRootOptions? options = null)
        {
            CacheRootOptions o = options ?? CacheRootOptions.Default;
            (string path, CacheRootSource source, string detail) = Resolve(o);
            CacheRoot root = new CacheRoot(path, source, detail);

            if (o.Create)
            {
                try
                {
                    Directory.CreateDirectory(path);
                    foreach ((string _, string dir) in root.Categories) Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    throw new IOException("SeedLab could not create its cache directory at " + path
                        + " (" + ex.GetType().Name + "). Set SEEDLAB_CACHE_DIR to a writable folder.", ex);
                }
            }
            return root;
        }

        private static (string, CacheRootSource, string) Resolve(CacheRootOptions o)
        {
            if (!string.IsNullOrWhiteSpace(o.Override))
                return (System.IO.Path.GetFullPath(o.Override!), CacheRootSource.Override, "--cache-dir");

            if (o.ReadEnvironment)
            {
                string? env = Environment.GetEnvironmentVariable("SEEDLAB_CACHE_DIR");
                if (!string.IsNullOrWhiteSpace(env))
                    return (System.IO.Path.GetFullPath(env!), CacheRootSource.Environment, "SEEDLAB_CACHE_DIR");
            }

            if (OperatingSystem.IsWindows())
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                    return (System.IO.Path.Combine(local, o.ApplicationFolder),
                            CacheRootSource.LocalApplicationData, "LOCALAPPDATA");
            }
            else if (OperatingSystem.IsMacOS())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                    return (System.IO.Path.Combine(home, "Library", "Caches", o.ApplicationFolder),
                            CacheRootSource.MacCaches, "~/Library/Caches");
            }
            else
            {
                string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
                if (!string.IsNullOrWhiteSpace(xdg))
                    return (System.IO.Path.Combine(xdg!, o.ApplicationFolder.ToLowerInvariant()),
                            CacheRootSource.XdgCacheHome, "XDG_CACHE_HOME");

                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                    return (System.IO.Path.Combine(home, ".cache", o.ApplicationFolder.ToLowerInvariant()),
                            CacheRootSource.XdgCacheHome, "~/.cache");
            }

            return (System.IO.Path.Combine(System.IO.Path.GetTempPath(), o.ApplicationFolder),
                    CacheRootSource.Fallback, "temp directory");
        }

        /// <summary>
        /// A scratch directory owned by this process, named with the pid and the process start time so
        /// that a later launch can tell "still running" from "was killed".
        /// </summary>
        public ScratchDirectory CreateScratch(string purpose) => ScratchDirectory.Create(this, purpose);

        /// <summary>
        /// Deletes scratch directories whose owning process is gone, and SeedLab temp files left behind
        /// by dead processes. Called at the start of every run: a killed process cannot clean up after
        /// itself, so the NEXT launch does it.
        /// </summary>
        public ReapReport ReapAbandoned(Func<int, DateTime, bool>? isSameProcess = null)
        {
            isSameProcess ??= ProcessLiveness.IsSameProcess;
            List<string> removed = new List<string>();
            List<string> kept = new List<string>();
            long bytes = 0;

            if (Directory.Exists(ScratchParent))
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(ScratchParent); }
                catch (Exception) { dirs = Array.Empty<string>(); }

                foreach (string dir in dirs)
                {
                    ScratchOwner? owner = ScratchOwner.TryRead(dir);
                    bool mine = owner != null && owner.Pid == Environment.ProcessId;
                    bool live = owner != null && !mine && isSameProcess(owner.Pid, owner.StartedUtc);

                    if (mine || live) { kept.Add(dir); continue; }

                    long size = DiskUsageReport.DirectorySize(dir);
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        removed.Add(dir);
                        bytes += size;
                    }
                    catch (Exception) { kept.Add(dir); }
                }
            }

            int temps = 0;
            foreach ((string _, string dir) in Categories) temps += DurableWrite.CleanOrphans(dir);
            temps += DurableWrite.CleanOrphans(Path);

            return new ReapReport(removed, kept, bytes, temps);
        }

        /// <summary>"What am I using on disk", by category.</summary>
        public DiskUsageReport MeasureUsage(params string[] extraPaths) =>
            DiskUsageReport.Measure(this, extraPaths);

        public override string ToString() => Path + " (" + SourceDetail + ")";
    }

    public sealed class ReapReport
    {
        public ReapReport(IReadOnlyList<string> removedScratch, IReadOnlyList<string> keptScratch,
                          long bytesReclaimed, int tempFilesRemoved)
        {
            RemovedScratch = removedScratch;
            KeptScratch = keptScratch;
            BytesReclaimed = bytesReclaimed;
            TempFilesRemoved = tempFilesRemoved;
        }

        public IReadOnlyList<string> RemovedScratch { get; }

        /// <summary>Directories left alone because a live process still owns them.</summary>
        public IReadOnlyList<string> KeptScratch { get; }

        public long BytesReclaimed { get; }
        public int TempFilesRemoved { get; }

        public bool DidAnything => RemovedScratch.Count > 0 || TempFilesRemoved > 0;

        /// <summary>One line, or "" when there was nothing to do - silence is correct when nothing happened.</summary>
        public string Line() =>
            !DidAnything ? "" :
            "reaped " + RemovedScratch.Count + " abandoned scratch "
            + (RemovedScratch.Count == 1 ? "directory" : "directories")
            + (TempFilesRemoved > 0 ? " and " + TempFilesRemoved + " orphaned temp file"
               + (TempFilesRemoved == 1 ? "" : "s") : "")
            + ", " + Bytes.Human(BytesReclaimed) + " reclaimed";
    }
}
