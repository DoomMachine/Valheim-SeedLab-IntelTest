using System;
using System.Globalization;
using System.IO;

namespace SeedLab.Runtime.Storage
{
    /// <summary>Why a file could not be changed, as far as looking at it can tell.</summary>
    public enum FileProblem
    {
        /// <summary>Nothing is wrong with it (an access check that passed).</summary>
        None = 0,

        /// <summary>Another program has it open.</summary>
        InUse = 1,

        /// <summary>The file carries the read-only attribute.</summary>
        ReadOnly = 2,

        /// <summary>The folder's security settings do not let this account change it.</summary>
        NoPermission = 3,

        /// <summary>The folder it lives in is gone (deleted, renamed, a drive unplugged).</summary>
        FolderMissing = 4,

        /// <summary>A file that was expected is not there (a read check).</summary>
        Missing = 5,

        /// <summary>It failed for the whole wait, and nothing holds it now.</summary>
        Unknown = 6,

        /// <summary>
        /// The drive it is on is full (2026-09-24). Not something a retry waits out, and not a bug:
        /// it used to reach the terminal as "IOException: There is not enough space on the disk"
        /// followed by "this is a bug".
        /// </summary>
        DiskFull = 7,
    }

    /// <summary>The one spelling of a <see cref="FileProblem"/> in JSON, for the terminal's --json and the page alike.</summary>
    public static class FileProblems
    {
        /// <summary>
        /// "in_use", "read_only", "no_permission", ... The terminal used these while the page sent the
        /// enum's own name ("InUse") for the same field, so a script reading both needed two spellings
        /// (review of 2026-09-24).
        /// </summary>
        public static string Name(FileProblem p) => p switch
        {
            FileProblem.InUse => "in_use",
            FileProblem.ReadOnly => "read_only",
            FileProblem.NoPermission => "no_permission",
            FileProblem.FolderMissing => "folder_missing",
            FileProblem.Missing => "missing",
            FileProblem.DiskFull => "disk_full",
            FileProblem.None => "none",
            _ => "unknown",
        };
    }

    /// <summary>
    /// What the session's access check had said about a path that failed later: when it passed,
    /// whether that was the file itself or only its folder, and whether the same check fails now
    /// (<see cref="AccessCheck.StartCheckFor"/>).
    ///
    /// <para><b>Why all three</b> (2026-09-24). The first version of the note said "It passed
    /// SeedLab's access check at T, so something changed after that" whenever the file's FOLDER had
    /// passed - a checkpoint of a run without --resume, the snapshots, a map's temp file - and also
    /// when the file itself had passed a check that cannot see the problem at all: a program that has
    /// the file open while letting others write to it passes the check and still blocks the rename.
    /// Three real runs with the holder in place BEFORE vseed started were each told that something
    /// had changed. The claim is now made only when the same check, made again, fails.</para>
    /// </summary>
    public sealed class StartCheck
    {
        public StartCheck(DateTimeOffset when, bool fileItself, bool failsNow)
        {
            When = when;
            FileItself = fileItself;
            FailsNow = failsNow;
        }

        /// <summary>When the check passed.</summary>
        public DateTimeOffset When { get; }

        /// <summary>True when the file itself was opened by the check; false when only its folder was probed.</summary>
        public bool FileItself { get; }

        /// <summary>The same check, made again when the failure was diagnosed, fails: what it looks at has changed since.</summary>
        public bool FailsNow { get; }
    }

    /// <summary>
    /// A failure to change a file, said so that someone who is not a programmer can act on it: which
    /// file, what probably happened, and what to do. The exception's type and HResult are kept in
    /// <see cref="Detail"/> for the session log and never put in <see cref="Message"/>.
    /// </summary>
    public sealed class FileDiagnosis
    {
        public FileDiagnosis(string path, FileProblem problem, string action, string? heldTemp,
                             StartCheck? startCheck, int attempts, TimeSpan waited, string detail)
        {
            Path = path;
            Problem = problem;
            Action = string.IsNullOrEmpty(action) ? "change" : action;
            HeldTemp = heldTemp;
            StartCheck = startCheck;
            Attempts = attempts;
            Waited = waited;
            Detail = detail ?? "";
        }

        /// <summary>The file, as a full path.</summary>
        public string Path { get; }

        public FileProblem Problem { get; }

        /// <summary>The verb of the failed operation: "save", "write", "delete", "open".</summary>
        public string Action { get; }

        /// <summary>SeedLab's own temporary copy, when it was THAT another program held.</summary>
        public string? HeldTemp { get; }

        /// <summary>What the session's access check had said about this file or its folder, or null when it never looked.</summary>
        public StartCheck? StartCheck { get; }

        /// <summary>When this file (or its folder) passed the session's access check, or null.</summary>
        public DateTimeOffset? AllowedAtStart => StartCheck?.When;

        public int Attempts { get; }
        public TimeSpan Waited { get; }

        /// <summary>The last exception's type, HResult and text. For the session log, not for a sentence.</summary>
        public string Detail { get; }

        /// <summary>What probably happened, as a clause: "another program has it open (...)".</summary>
        public string Cause => Problem switch
        {
            FileProblem.InUse when HeldTemp != null =>
                "another program held SeedLab's own temporary copy of it (" + HeldTemp
                + ") - usually a virus scanner checking the new file",
            // The last item since 2026-09-24: two searches given the same results file are the likeliest
            // way to meet this, and "another program" alone sends the reader looking for a virus scanner.
            FileProblem.InUse =>
                "another program has it open - a virus scanner, a backup or sync tool (OneDrive, Dropbox), "
                + "a search indexer, an editor or a viewer, or another vseed or SeedLab page that is still running",
            FileProblem.ReadOnly => "the file is marked read-only",
            FileProblem.NoPermission =>
                "this account is not allowed to change files in that folder (the folder's security settings)",
            FileProblem.FolderMissing =>
                "the folder it belongs in does not exist any more - it was deleted or renamed, or the drive is disconnected",
            FileProblem.Missing => "the file is not there",
            FileProblem.DiskFull => "the drive it is on is full",
            FileProblem.None => "nothing is wrong with it",
            _ => "it stayed busy for all " + Seconds(Waited) + " SeedLab waited, and nothing holds it now - most "
                 + "likely another program had it open for a moment",
        };

        /// <summary>What to do about it, as a sentence.</summary>
        public string Advice => Problem switch
        {
            FileProblem.InUse when HeldTemp != null =>
                "Wait a moment and try again; if it keeps happening, tell the virus scanner to skip the folder "
                + (System.IO.Path.GetDirectoryName(HeldTemp) ?? HeldTemp) + ".",
            FileProblem.InUse =>
                "Close that program, or wait until it lets go of the file, and try again. If it is a sync tool, "
                + "pause it or keep SeedLab's files outside the synced folder.",
            FileProblem.ReadOnly =>
                "Clear its read-only setting (on Windows: right-click the file, Properties, untick Read-only) "
                + "or delete the file, then try again.",
            FileProblem.NoPermission =>
                "Use a folder you own - for SeedLab's own files, pass --cache-dir or set SEEDLAB_CACHE_DIR - or ask "
                + "whoever manages this computer.",
            FileProblem.FolderMissing => "Reconnect the drive or recreate the folder, then try again.",
            FileProblem.Missing => "Check the path, or run the step that creates the file first.",
            FileProblem.DiskFull =>
                "Free some space on that drive - 'vseed clean' shows what SeedLab itself keeps in its cache folder - "
                + "then try again.",
            FileProblem.None => "",
            _ => "Try again.",
        };

        /// <summary>
        /// What the start-of-session access check has to say about this failure - only what is true:
        /// <list type="bullet">
        /// <item>the same check fails now: "It passed SeedLab's access check at 14:03:12, so something
        /// changed after that." (or "Its folder passed ...");</item>
        /// <item>the file itself passed and passes still, and another program has it open: that
        /// program is one the check cannot see, so it may have had the file open all along;</item>
        /// <item>otherwise nothing - a check of the folder says nothing about a file in it.</item>
        /// </list>
        /// </summary>
        public string StartNote
        {
            get
            {
                if (StartCheck == null) return "";
                string at = StartCheck.When.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                if (StartCheck.FailsNow)
                {
                    return (StartCheck.FileItself ? "It" : "Its folder") + " passed SeedLab's access check at " + at
                           + ", so something changed after that.";
                }

                if (StartCheck.FileItself && Problem == FileProblem.InUse && HeldTemp == null)
                {
                    return "It passed SeedLab's access check at " + at + ", but a program that has the file open while "
                           + "letting other programs write to it passes that check and still stops SeedLab replacing it - "
                           + "so that program may have had it open since before then.";
                }

                return "";
            }
        }

        /// <summary>
        /// The whole sentence for the user: "SeedLab could not save C:\...\x.ckpt: another program has it
        /// open (...). Close that program ... and try again."
        /// </summary>
        public string Message =>
            "SeedLab could not " + Action + " " + Path + ": " + Cause + "."
            + (StartNote.Length > 0 ? " " + StartNote : "")
            + (Advice.Length > 0 ? " " + Advice : "");

        public override string ToString() => Message;

        private static string Seconds(TimeSpan t) =>
            t.TotalSeconds < 1
                ? ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms"
                : t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>
    /// A file SeedLab needed to change stayed unavailable for the whole retry schedule. Its message is
    /// <see cref="FileDiagnosis.Message"/>: the file, the probable cause and what to do - never an
    /// HResult or a type name. The original exception is the inner one, for the log.
    ///
    /// <para>An <see cref="IOException"/>, so every existing <c>catch (IOException)</c> still sees it.</para>
    /// </summary>
    public sealed class FileAccessException : IOException
    {
        public FileAccessException(FileDiagnosis diagnosis, Exception? inner = null)
            : base((diagnosis ?? throw new ArgumentNullException(nameof(diagnosis))).Message, inner)
        {
            Diagnosis = diagnosis;
        }

        public FileDiagnosis Diagnosis { get; }

        /// <summary>The file, as a full path.</summary>
        public string Path => Diagnosis.Path;

        public FileProblem Problem => Diagnosis.Problem;
    }
}
