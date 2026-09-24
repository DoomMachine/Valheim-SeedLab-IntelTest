using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace SeedLab.Runtime.Storage
{
    /// <summary>
    /// How long a file operation keeps trying before it gives up: a list of waits between attempts.
    ///
    /// <para><b>Why two of them</b> (2026-09-24). Any open handle on a file makes the rename that
    /// replaces it fail - a virus scanner, a backup or sync tool, a search indexer or an editor that
    /// merely reads the file is enough, whatever share mode it asked for. Measured against a reader
    /// that opened the checkpoint every 50 ms: 9 saves in 1,287 failed, and every one of them
    /// succeeded when only the rename was tried again 10 ms later. <see cref="Quick"/> is for the saves
    /// a run makes every interval, where the next interval is another chance anyway; <see cref="Patient"/>
    /// is for a save nothing will repeat (the last checkpoint of a run, a survivor list), where waiting
    /// a quarter of a minute is cheap next to losing the work.</para>
    /// </summary>
    public sealed class RetrySchedule
    {
        public RetrySchedule(string name, params int[] waitMilliseconds)
        {
            Name = name ?? "";
            List<TimeSpan> waits = new List<TimeSpan>();
            TimeSpan total = TimeSpan.Zero;
            foreach (int ms in waitMilliseconds ?? Array.Empty<int>())
            {
                TimeSpan t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
                waits.Add(t);
                total += t;
            }

            Waits = waits;
            Total = total;
        }

        /// <summary>About 1.6 s: 10, 25, 50, 100, 200, 400, 800 ms. For saves a run repeats every interval.</summary>
        public static readonly RetrySchedule Quick = new RetrySchedule("quick", 10, 25, 50, 100, 200, 400, 800);

        /// <summary>About 15 s: <see cref="Quick"/>, then one attempt a second. For saves nothing repeats.</summary>
        public static readonly RetrySchedule Patient = new RetrySchedule("patient",
            10, 25, 50, 100, 200, 400, 800, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000, 1000);

        /// <summary>One attempt and no wait: the failure is diagnosed at once.</summary>
        public static readonly RetrySchedule Once = new RetrySchedule("once");

        public string Name { get; }

        /// <summary>The wait before each retry. Attempts = waits + 1.</summary>
        public IReadOnlyList<TimeSpan> Waits { get; }

        public int Attempts => Waits.Count + 1;

        /// <summary>The longest the schedule can wait in all, not counting the attempts themselves.</summary>
        public TimeSpan Total { get; }

        public override string ToString() => Name + " (" + Attempts + " attempts over "
                                             + Total.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s)";
    }

    /// <summary>What <see cref="FileRetry"/> reports about one attempt, for a log or a test.</summary>
    public sealed class RetryAttempt
    {
        public RetryAttempt(string path, string action, int attempt, int attempts, Exception? error, TimeSpan wait,
                            TimeSpan elapsed, bool succeeded)
        {
            Path = path;
            Action = action;
            Attempt = attempt;
            Attempts = attempts;
            Error = error;
            Wait = wait;
            Elapsed = elapsed;
            Succeeded = succeeded;
        }

        public string Path { get; }
        public string Action { get; }

        /// <summary>1 for the first try.</summary>
        public int Attempt { get; }

        /// <summary>How many the schedule allows.</summary>
        public int Attempts { get; }

        /// <summary>What this attempt failed with, or null when it succeeded.</summary>
        public Exception? Error { get; }

        /// <summary>The wait before the next attempt; zero on the last one.</summary>
        public TimeSpan Wait { get; }

        /// <summary>Since the first failure.</summary>
        public TimeSpan Elapsed { get; }

        public bool Succeeded { get; }
    }

    /// <summary>
    /// Runs a file operation again while it fails for a reason that goes away by itself - another
    /// program holding the file - and, when the schedule runs out, turns the failure into a
    /// <see cref="FileAccessException"/> whose message names the file and says what probably happened.
    ///
    /// <para><b>Why the message has to be made here</b> (2026-09-24). The rename that replaces a file
    /// throws "Access to the path is denied." with no path in it, and the same exception, the same
    /// HResult and the same text for three different causes: another program has the file open, the
    /// file is marked read-only, or the folder's permissions forbid it. A run died of exactly that
    /// sentence and nobody could tell which file or why. So after the retries, <see cref="Diagnose"/>
    /// looks at the file itself and says which of the three it is.</para>
    ///
    /// <para>Every first failure and every outcome also goes to <see cref="SessionLog.Current"/>,
    /// where the exception's type and HResult are kept for whoever reads the log.</para>
    /// </summary>
    public static class FileRetry
    {
        /// <summary>E_ACCESSDENIED: a held destination, a read-only file, or a permission. Same code for all three.</summary>
        public const int AccessDenied = unchecked((int)0x80070005);

        /// <summary>ERROR_SHARING_VIOLATION: another handle's share mode forbids this open.</summary>
        public const int SharingViolation = unchecked((int)0x80070020);

        /// <summary>ERROR_LOCK_VIOLATION: another process has locked a region of the file.</summary>
        public const int LockViolation = unchecked((int)0x80070021);

        /// <summary>
        /// True for the failures another program causes by holding a file: an access denial (which is
        /// also what a read-only file and a permission give - <see cref="Diagnose"/> tells them apart
        /// once the retries are spent), a sharing violation and a lock violation. Anything else - a
        /// full disk, a missing folder, an already diagnosed failure - is not retried.
        /// </summary>
        public static bool IsTransient(Exception ex)
        {
            if (ex == null || ex is FileAccessException) return false;
            if (ex is UnauthorizedAccessException) return ex.HResult == AccessDenied;
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is PathTooLongException) return false;
            if (ex is IOException) return ex.HResult == SharingViolation || ex.HResult == LockViolation;
            return false;
        }

        /// <summary>
        /// Runs <paramref name="operation"/> on the schedule (<see cref="RetrySchedule.Quick"/> when
        /// none is given). A transient failure is retried; once the schedule is spent, the failure is
        /// diagnosed and thrown as a <see cref="FileAccessException"/>. Any other failure is thrown
        /// unchanged at once.
        /// </summary>
        /// <param name="path">The file the operation is about - the one the message will name.</param>
        /// <param name="action">A verb for the message: "save", "write", "delete", "open".</param>
        /// <param name="tempPath">SeedLab's own temporary copy, when the operation renames one onto
        /// <paramref name="path"/>: a scanner holding THAT is diagnosed too.</param>
        public static T Run<T>(string path, string action, Func<T> operation, RetrySchedule? retry = null,
                               Action<RetryAttempt>? onAttempt = null, string? tempPath = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            RetrySchedule schedule = retry ?? RetrySchedule.Quick;
            Stopwatch? since = null;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    T result = operation();
                    if (since != null)
                    {
                        Report(onAttempt, new RetryAttempt(path, action, attempt, schedule.Attempts, null, TimeSpan.Zero,
                                                           since.Elapsed, true));
                        SessionLog.Current?.Info("retry    " + action + " " + path + ": succeeded on attempt " + attempt
                                                 + " of " + schedule.Attempts + ", " + Ms(since.Elapsed) + " after the first failure");
                    }

                    return result;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    since ??= Stopwatch.StartNew();
                    bool last = attempt >= schedule.Attempts;
                    TimeSpan wait = last ? TimeSpan.Zero : schedule.Waits[attempt - 1];
                    Report(onAttempt, new RetryAttempt(path, action, attempt, schedule.Attempts, ex, wait, since.Elapsed, false));

                    if (attempt == 1)
                    {
                        SessionLog.Current?.Info("retry    " + action + " " + path + ": attempt 1 of " + schedule.Attempts
                                                 + " failed (" + Describe(ex) + ")"
                                                 + (last ? "" : "; trying again on the " + schedule.Name + " schedule"));
                    }

                    if (last)
                    {
                        FileDiagnosis d = Diagnose(path, ex, tempPath, action, attempt, since.Elapsed);
                        SessionLog.Current?.Warn("retry    " + action + " " + path + ": gave up after " + attempt
                                                 + " attempt" + (attempt == 1 ? "" : "s") + " over " + Ms(since.Elapsed)
                                                 + " - " + d.Problem + "; last error " + Describe(ex));
                        throw new FileAccessException(d, ex);
                    }

                    Thread.Sleep(wait);
                }
            }
        }

        /// <summary>As <see cref="Run{T}"/>, for an operation with no result.</summary>
        public static void Run(string path, string action, Action operation, RetrySchedule? retry = null,
                               Action<RetryAttempt>? onAttempt = null, string? tempPath = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            Run<bool>(path, action, () =>
            {
                operation();
                return true;
            }, retry, onAttempt, tempPath);
        }

        /// <summary>
        /// Says why <paramref name="path"/> could not be changed, by looking at it: the read-only
        /// attribute first (it is certain), then an exclusive open of the file - which fails with a
        /// sharing violation while ANY other handle is open, and with an access denial when the folder's
        /// permissions forbid writing - and, for a file that does not exist, a probe file in its folder.
        ///
        /// <para><b>Why an exclusive probe</b> (2026-09-24). A reader that opened the file sharing
        /// read, write and delete still makes the rename fail, and a probe that shares the same way
        /// opens beside it and sees nothing; only a probe that shares nothing collides with every
        /// holder. It opens and closes at once, changes nothing, and never truncates.</para>
        ///
        /// <para>Never throws. The message always names the file.</para>
        /// </summary>
        public static FileDiagnosis Diagnose(string path, Exception? error = null, string? tempPath = null,
                                             string action = "change", int attempts = 1, TimeSpan waited = default)
        {
            string full = Full(path);
            FileProblem problem;
            string? heldTemp = null;
            try
            {
                if (File.Exists(full))
                {
                    problem = (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0
                        ? FileProblem.ReadOnly
                        : ProbeFile(full);
                }
                else
                {
                    string? dir = Path.GetDirectoryName(full);
                    problem = string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)
                        ? FileProblem.FolderMissing
                        : ProbeFolder(dir!);
                }

                if (problem == FileProblem.None && tempPath != null && File.Exists(tempPath)
                    && ProbeFile(Full(tempPath)) == FileProblem.InUse)
                {
                    problem = FileProblem.InUse;
                    heldTemp = Full(tempPath);
                }
            }
            catch (Exception)
            {
                problem = FileProblem.None;
            }

            if (problem == FileProblem.None) problem = FileProblem.Unknown;
            return new FileDiagnosis(full, problem, action, heldTemp, AccessCheck.StartCheckFor(full),
                                     attempts, waited, error == null ? "" : Describe(error));
        }

        /// <summary>ERROR_DISK_FULL: the volume has no room for what was being written.</summary>
        public const int DiskFullError = unchecked((int)0x80070070);

        /// <summary>ERROR_HANDLE_DISK_FULL: the same, as some writes report it.</summary>
        public const int HandleDiskFull = unchecked((int)0x80070027);

        /// <summary>
        /// True for "there is not enough space on the disk". Not transient - waiting does not free
        /// space - and not a bug either: until 2026-09-24 it reached the terminal as an IOException's
        /// type name followed by "this is a bug".
        /// </summary>
        public static bool IsDiskFull(Exception? ex) =>
            ex is IOException && !(ex is FileAccessException) && (ex.HResult == DiskFullError || ex.HResult == HandleDiskFull);

        /// <summary>
        /// The diagnosis for a file failure that reached a host WITHOUT passing through
        /// <see cref="Run{T}"/> - a raw sharing violation, an access denial - so that the host can
        /// still say which file and why in plain words rather than print "this is a bug". A
        /// <see cref="FileAccessException"/> gives back its own diagnosis. Anything else gives a
        /// diagnosis only when the exception's message names the file (the sharing violation's
        /// "The process cannot access the file '...'" does; the rename's "Access to the path is
        /// denied." does not), and null otherwise - the host then says what it can and points at the
        /// session log, which has the exception.
        ///
        /// <para>Never throws. It looks at the file, as <see cref="Diagnose"/> does, once.</para>
        /// </summary>
        public static FileDiagnosis? DiagnoseEscaped(Exception? ex, string action = "use")
        {
            if (ex == null) return null;
            if (ex is FileAccessException fa) return fa.Diagnosis;

            // A full drive is said as one, of the file the message names (a write's does: "There is not
            // enough space on the disk. : 'C:\...'"). Looking at the file would tell nothing more.
            if (IsDiskFull(ex))
            {
                string? full = QuotedPath(ex.Message);
                return full == null
                    ? null
                    : new FileDiagnosis(Full(full), FileProblem.DiskFull, "write", null, null, 1, TimeSpan.Zero, Describe(ex));
            }

            bool accessShaped = ex is UnauthorizedAccessException
                                || (ex is IOException && (ex.HResult == SharingViolation || ex.HResult == LockViolation));
            if (!accessShaped) return null;

            string? path = QuotedPath(ex.Message);
            return path == null ? null : Diagnose(path, ex, null, action);
        }

        /// <summary>The first '...'-quoted rooted path in a .NET file error message, or null.</summary>
        private static string? QuotedPath(string? message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            int open = message.IndexOf('\'');
            while (open >= 0 && open + 1 < message.Length)
            {
                int close = message.IndexOf('\'', open + 1);
                if (close < 0) return null;
                string inner = message.Substring(open + 1, close - open - 1);
                try
                {
                    if (inner.Length > 0 && Path.IsPathRooted(inner)) return inner;
                }
                catch (Exception)
                {
                }

                open = message.IndexOf('\'', close + 1);
            }

            return null;
        }

        /// <summary>An exclusive, non-truncating open and close. None when nothing holds the file.</summary>
        internal static FileProblem ProbeFile(string full)
        {
            try
            {
                using (new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }

                return FileProblem.None;
            }
            catch (UnauthorizedAccessException)
            {
                return (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0 ? FileProblem.ReadOnly : FileProblem.NoPermission;
            }
            catch (FileNotFoundException)
            {
                return FileProblem.None;
            }
            catch (DirectoryNotFoundException)
            {
                return FileProblem.FolderMissing;
            }
            catch (IOException ex) when (ex.HResult == SharingViolation || ex.HResult == LockViolation)
            {
                return FileProblem.InUse;
            }
            catch (Exception)
            {
                return FileProblem.Unknown;
            }
        }

        /// <summary>
        /// Creates and at once deletes a SeedLab temp file in <paramref name="directory"/>
        /// (<see cref="FileOptions.DeleteOnClose"/>, so even a kill leaves nothing). None when that works.
        /// </summary>
        internal static FileProblem ProbeFolder(string directory)
        {
            string probe = Path.Combine(directory, DurableWrite.TempPrefix
                                                   + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                                                   + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-access-probe");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                                      FileOptions.DeleteOnClose))
                {
                }

                return FileProblem.None;
            }
            catch (UnauthorizedAccessException)
            {
                return FileProblem.NoPermission;
            }
            catch (DirectoryNotFoundException)
            {
                return FileProblem.FolderMissing;
            }
            catch (Exception)
            {
                return FileProblem.Unknown;
            }
        }

        /// <summary>"UnauthorizedAccessException 0x80070005: Access to the path is denied." - for the log only.</summary>
        public static string Describe(Exception ex) =>
            ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ": " + ex.Message;

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

        private static string Ms(TimeSpan t) =>
            t.TotalMilliseconds < 1000
                ? ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms"
                : t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

        private static void Report(Action<RetryAttempt>? onAttempt, RetryAttempt a)
        {
            if (onAttempt == null) return;
            try
            {
                onAttempt(a);
            }
            catch (Exception)
            {
                // A reporting hook must not turn a save into a failure.
            }
        }
    }
}
