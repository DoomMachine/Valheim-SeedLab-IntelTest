using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SeedLab.Runtime.Storage
{
    /// <summary>What an access check looked at.</summary>
    public enum AccessKind
    {
        /// <summary>A folder SeedLab creates files in.</summary>
        Folder = 0,

        /// <summary>A file SeedLab will write or replace.</summary>
        Write = 1,

        /// <summary>A file SeedLab will read.</summary>
        Read = 2,
    }

    /// <summary>One access check: the path, what was checked, when, and whether it passed.</summary>
    public sealed class AccessResult
    {
        public AccessResult(string path, AccessKind kind, FileProblem problem, DateTimeOffset when, string note,
                            bool examined = false)
        {
            Path = path;
            Kind = kind;
            Problem = problem;
            When = when;
            Note = note ?? "";
            Examined = examined;
        }

        public string Path { get; }
        public AccessKind Kind { get; }

        /// <summary>
        /// The path itself was there and the check looked at it - a file opened, a folder probed. False
        /// for one that did not exist yet, whose nearest folder above was probed instead, so that a later
        /// failure does not claim the path passed a check that never looked at it.
        /// </summary>
        public bool Examined { get; }

        /// <summary><see cref="FileProblem.None"/> when the check passed.</summary>
        public FileProblem Problem { get; }

        public bool Ok => Problem == FileProblem.None;
        public DateTimeOffset When { get; }

        /// <summary>A short remark for the log ("will be created", "does not exist yet").</summary>
        public string Note { get; }

        /// <summary>The sentence for the user when the check failed, naming the file; "" when it passed.</summary>
        public string Message => Ok
            ? ""
            : new FileDiagnosis(Path, Problem, Kind == AccessKind.Read ? "read" : "write to", null, null, 1,
                                TimeSpan.Zero, "").Message;

        /// <summary>"ok    folder  C:\...\checkpoints" - the session log's line for it.</summary>
        public string Line() =>
            (Ok ? "ok    " : "FAIL  ") + (Kind == AccessKind.Folder ? "folder" : Kind == AccessKind.Write ? "write " : "read  ")
            + "  " + Path + (Note.Length > 0 ? "  (" + Note + ")" : "")
            + (Ok ? "" : " - " + Problem);
    }

    /// <summary>
    /// "Can SeedLab do what it is about to do to these paths?" - asked once, at the start of a session
    /// or a run, and remembered, so that a failure later can say whether the path was fine at the
    /// start and is not now (<see cref="StartCheckFor"/>).
    ///
    /// <para><b>What a start check can and cannot do</b> (2026-09-24). It catches the conditions that
    /// last - a read-only file, a folder this account may not write, a results file a spreadsheet has
    /// open - before a single seed is scanned. It cannot predict a virus scanner or a sync tool that
    /// opens a file for a moment an hour later; that is what <see cref="FileRetry"/> is for. So a check
    /// that passed is a fact about that moment, and the ledger keeps the moment.</para>
    ///
    /// <para>Every check is written to <see cref="SessionLog.Current"/>. None of them changes a file:
    /// a folder is probed with a temp file that deletes itself on close, a file is opened without
    /// truncation and closed at once. Never throws.</para>
    /// </summary>
    public static class AccessCheck
    {
        private static readonly object Gate = new object();
        private static readonly List<AccessResult> Ledger = new List<AccessResult>();

        /// <summary>
        /// A folder SeedLab will create files in: created if it does not exist, then probed with a
        /// temp file that deletes itself on close.
        ///
        /// <para>With <paramref name="create"/> false a missing folder is NOT created: the nearest
        /// folder above it that exists is probed instead, since that is where it will be created. That
        /// is for a check that must leave nothing behind - a <c>--dry-run</c>, a run that is then
        /// refused, a results folder a server promises to create only when a run names a file
        /// (2026-09-24).</para>
        /// </summary>
        public static AccessResult Directory(string path, bool create = true)
        {
            string full = Full(path);
            string note = "";
            FileProblem problem;
            try
            {
                if (!System.IO.Directory.Exists(full) && !create)
                {
                    string? above = NearestExistingFolder(Path.GetDirectoryName(full));
                    note = above == null ? "no folder on its path exists" : "does not exist yet; " + above + " was checked";
                    problem = above == null ? FileProblem.FolderMissing : FileRetry.ProbeFolder(above);
                    return Record(new AccessResult(full, AccessKind.Folder, problem, DateTimeOffset.Now, note));
                }

                if (!System.IO.Directory.Exists(full))
                {
                    System.IO.Directory.CreateDirectory(full);
                    note = "created";
                }

                problem = FileRetry.ProbeFolder(full);
            }
            catch (UnauthorizedAccessException)
            {
                problem = FileProblem.NoPermission;
            }
            catch (Exception)
            {
                problem = System.IO.Directory.Exists(full) ? FileProblem.Unknown : FileProblem.FolderMissing;
            }

            return Record(new AccessResult(full, AccessKind.Folder, problem, DateTimeOffset.Now, note, examined: true));
        }

        /// <summary>
        /// A file SeedLab will write. An existing one is opened for reading and writing, sharing
        /// everything and truncating nothing - which fails when another program holds it without
        /// letting others write (a spreadsheet with the file open), when it is read-only, or when the
        /// folder's permissions forbid it. A missing one is checked by probing the nearest folder that
        /// exists; no folder is created.
        /// </summary>
        public static AccessResult FileForWrite(string path)
        {
            string full = Full(path);
            string note = "";
            FileProblem problem;
            bool opened = false;
            if (File.Exists(full))
            {
                problem = ProbeForWrite(full);
                opened = true;
            }
            else
            {
                string? dir = NearestExistingFolder(Path.GetDirectoryName(full));
                note = dir == null ? "no folder on its path exists" : "does not exist yet";
                problem = dir == null ? FileProblem.FolderMissing : FileRetry.ProbeFolder(dir);
            }

            return Record(new AccessResult(full, AccessKind.Write, problem, DateTimeOffset.Now, note, examined: opened));
        }

        /// <summary>
        /// The write check itself, on an existing file, recording nothing: the read-only attribute, then
        /// an open for reading and writing that shares everything and truncates nothing.
        /// </summary>
        private static FileProblem ProbeForWrite(string full)
        {
            try
            {
                if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) return FileProblem.ReadOnly;
                using (new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                }

                return FileProblem.None;
            }
            catch (UnauthorizedAccessException)
            {
                return FileProblem.NoPermission;
            }
            catch (FileNotFoundException)
            {
                return FileProblem.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return FileProblem.FolderMissing;
            }
            catch (IOException ex) when (ex.HResult == FileRetry.SharingViolation || ex.HResult == FileRetry.LockViolation)
            {
                return FileProblem.InUse;
            }
            catch (Exception)
            {
                return FileProblem.Unknown;
            }
        }

        /// <summary>A file SeedLab will read: opened for reading, sharing everything, and closed.</summary>
        public static AccessResult FileForRead(string path)
        {
            string full = Full(path);
            FileProblem problem;
            try
            {
                if (!File.Exists(full))
                {
                    problem = FileProblem.Missing;
                }
                else
                {
                    using (new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                    }

                    problem = FileProblem.None;
                }
            }
            catch (UnauthorizedAccessException)
            {
                problem = FileProblem.NoPermission;
            }
            catch (IOException ex) when (ex.HResult == FileRetry.SharingViolation || ex.HResult == FileRetry.LockViolation)
            {
                problem = FileProblem.InUse;
            }
            catch (Exception)
            {
                problem = FileProblem.Unknown;
            }

            return Record(new AccessResult(full, AccessKind.Read, problem, DateTimeOffset.Now, "",
                                           examined: problem != FileProblem.Missing));
        }

        /// <summary>
        /// What this process's access checks said about <paramref name="path"/>, for a failure that
        /// comes later: the first write check that opened the file itself and passed, or else the first
        /// check of its folder that passed - and whether that same check, made again NOW, fails. Null
        /// when neither was ever checked. Records nothing in the ledger; never throws.
        ///
        /// <para><b>Why "made again now"</b> (2026-09-24). "It passed at 14:03:12, so something changed
        /// since" is only true when the check could have seen the problem. A folder that passed says
        /// nothing about a file in it, and the write check opens a file sharing everything - so a
        /// program that has it open while letting others write passes the check, before and after, and
        /// still blocks the rename. Repeating the check is the one test that tells "changed" from "the
        /// check could not see it" without guessing.</para>
        /// </summary>
        public static StartCheck? StartCheckFor(string path)
        {
            string full = Full(path);
            string? dir = null;
            try
            {
                dir = Path.GetDirectoryName(full);
            }
            catch (Exception)
            {
            }

            DateTimeOffset? own = null, folder = null;
            lock (Gate)
            {
                foreach (AccessResult r in Ledger)
                {
                    if (!r.Ok) continue;
                    if (!r.Examined) continue;
                    if (own == null && r.Kind == AccessKind.Write && Same(r.Path, full)) own = r.When;
                    if (folder == null && dir != null && r.Kind == AccessKind.Folder && Same(r.Path, dir)) folder = r.When;
                }
            }

            try
            {
                if (own != null)
                {
                    bool fails = File.Exists(full)
                        ? ProbeForWrite(full) != FileProblem.None
                        : dir == null || FolderFailsNow(dir);
                    return new StartCheck(own.Value, true, fails);
                }

                if (folder != null) return new StartCheck(folder.Value, false, FolderFailsNow(dir!));
            }
            catch (Exception)
            {
                // A note is a courtesy; a failure to make one says nothing.
            }

            return null;
        }

        private static bool FolderFailsNow(string dir) =>
            !System.IO.Directory.Exists(dir) || FileRetry.ProbeFolder(dir) != FileProblem.None;

        /// <summary>Every check this process has made, in order.</summary>
        public static IReadOnlyList<AccessResult> Checked
        {
            get
            {
                lock (Gate) return Ledger.ToArray();
            }
        }

        /// <summary>
        /// One line for a set of checks: "file access checked: 8 paths OK", or which ones failed and
        /// why, naming each.
        /// </summary>
        public static string Summary(IEnumerable<AccessResult> results)
        {
            int ok = 0, all = 0;
            StringBuilder failed = new StringBuilder();
            foreach (AccessResult r in results)
            {
                all++;
                if (r.Ok)
                {
                    ok++;
                    continue;
                }

                failed.Append(failed.Length == 0 ? "" : "; ").Append(r.Path).Append(": ").Append(Problem(r.Problem));
            }

            if (ok == all) return "file access checked: " + all + " path" + (all == 1 ? "" : "s") + " OK";
            return "file access checked: " + ok + " of " + all + " paths OK - " + failed;
        }

        private static string Problem(FileProblem p) => p switch
        {
            FileProblem.InUse => "in use by another program",
            FileProblem.ReadOnly => "marked read-only",
            FileProblem.NoPermission => "not allowed by the folder's security settings",
            FileProblem.FolderMissing => "its folder does not exist",
            FileProblem.Missing => "not there",
            FileProblem.DiskFull => "its drive is full",
            _ => "could not be checked",
        };

        private static AccessResult Record(AccessResult r)
        {
            lock (Gate) Ledger.Add(r);
            SessionLog? log = SessionLog.Current;
            if (log != null)
            {
                if (r.Ok) log.Info("access   " + r.Line());
                else log.Warn("access   " + r.Line());
            }

            return r;
        }

        private static string? NearestExistingFolder(string? dir)
        {
            while (!string.IsNullOrEmpty(dir))
            {
                if (System.IO.Directory.Exists(dir)) return dir;
                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        private static bool Same(string a, string b) =>
            string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static string Full(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path ?? "";
            }
        }
    }
}
