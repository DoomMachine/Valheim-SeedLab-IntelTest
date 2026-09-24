using System;
using System.IO;
using System.Text;

namespace SeedLab.Runtime.Storage
{
    /// <summary>
    /// Temp-and-rename for every durable write in SeedLab: checkpoints and their kept-results
    /// snapshots, manifests, survivor lists, the self-test stamp, rendered maps.
    ///
    /// <para>The audit found a hard kill leaving a torn file and an orphaned <c>.tmp</c>. The rule here
    /// is: write the whole thing to a sibling temp file in the SAME directory (so the rename cannot
    /// cross a volume and stop being atomic), flush it to the device with <c>Flush(true)</c>, then
    /// <see cref="File.Move(string,string,bool)"/> over the target. A reader then sees either the old
    /// file or the new one, never half of either.</para>
    ///
    /// <para><b>The rename is retried, and a failure is explained</b> (2026-09-24). Any open handle
    /// on the target - a virus scanner, a sync tool, an editor, whatever it shares - makes the rename
    /// fail with a bare "Access to the path is denied." that names no file. <see cref="Replace"/> is
    /// that rename for every temp-and-rename site in SeedLab, each keeping its own temp name: it
    /// retries on a <see cref="RetrySchedule"/> and then throws a <see cref="FileAccessException"/>
    /// that names the file and the probable cause (<see cref="FileRetry"/>).</para>
    ///
    /// <para>The temp name carries our pid so a crash leaves something the reaper can recognise as ours
    /// (<see cref="CleanOrphans"/>). Directory entries themselves are not fsynced - the BCL exposes no
    /// way to - so on a power cut the rename may be lost; the file content never is.</para>
    /// </summary>
    public static class DurableWrite
    {
        public const string TempPrefix = ".seedlab-tmp-";

        /// <summary>UTF-8 without a BOM, the project's file convention.</summary>
        public static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void Text(string path, string text, RetrySchedule? retry = null) =>
            Stream(path, s =>
            {
                byte[] bytes = Utf8NoBom.GetBytes(text ?? "");
                s.Write(bytes, 0, bytes.Length);
            }, retry);

        public static void Bytes(string path, byte[] data, RetrySchedule? retry = null) =>
            Stream(path, s => s.Write(data ?? Array.Empty<byte>(), 0, data?.Length ?? 0), retry);

        /// <summary>
        /// Runs <paramref name="write"/> against a temp file and renames it over <paramref name="path"/>.
        /// If <paramref name="write"/> throws, the temp file is removed and the original is untouched.
        /// </summary>
        /// <param name="retry">How long creating the temp file and the rename keep trying while another
        /// program holds a file; <see cref="RetrySchedule.Quick"/> when not given.</param>
        /// <param name="tempPath">A site's own temp name (<c>&lt;checkpoint&gt;.tmp</c>, which that
        /// site's orphan sweep knows), overwritten if a killed run left one behind. By default a fresh
        /// <c>.seedlab-tmp-&lt;pid&gt;-...</c> sibling, which <see cref="CleanOrphans"/> reaps.</param>
        /// <param name="onAttempt">Hears every attempt of the temp file's creation and of the rename,
        /// so a caller can say "waiting" on the first failure rather than sit silent for the schedule.</param>
        /// <exception cref="FileAccessException">Another program kept a file busy for the whole schedule,
        /// or the file or its folder cannot be written; the message names the file and says why.</exception>
        public static void Stream(string path, Action<Stream> write, RetrySchedule? retry = null, string? tempPath = null,
                                  Action<RetryAttempt>? onAttempt = null)
        {
            string temp = WriteTemp(path, write, retry, tempPath, onAttempt);
            try
            {
                Replace(temp, path, retry, onAttempt);
            }
            catch (Exception)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>
        /// The first half of <see cref="Stream"/>: runs <paramref name="write"/> against a temp file
        /// beside <paramref name="path"/>, flushes it to the device, and returns the temp file's full
        /// path - complete, and not yet renamed. The caller renames it with <see cref="Replace"/> and
        /// deletes it if it gives up. If <paramref name="write"/> throws, the temp file is removed.
        ///
        /// <para><b>Why a separate half</b> (2026-09-24). A rendered map is most of a command's time:
        /// when the rename onto a file an image viewer holds keeps failing, the terminal can offer to
        /// try the rename again with the finished image still in the temp file, instead of deleting it
        /// with the failure and making the user render it again.</para>
        /// </summary>
        public static string WriteTemp(string path, Action<Stream> write, RetrySchedule? retry = null, string? tempPath = null,
                                       Action<RetryAttempt>? onAttempt = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
            if (write == null) throw new ArgumentNullException(nameof(write));

            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full) ?? ".";
            Directory.CreateDirectory(dir);

            string temp = tempPath != null
                ? Path.GetFullPath(tempPath)
                : Path.Combine(dir,
                    TempPrefix + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + Path.GetFileName(full));
            FileMode mode = tempPath != null ? FileMode.Create : FileMode.CreateNew;

            try
            {
                using (FileStream fs = FileRetry.Run(temp, "write",
                           () => new FileStream(temp, mode, FileAccess.Write, FileShare.None,
                                                bufferSize: 1 << 16, FileOptions.SequentialScan),
                           retry, onAttempt))
                {
                    write(fs);
                    fs.Flush(flushToDisk: true);
                }

                return temp;
            }
            catch (Exception)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>
        /// The rename step of a temp-and-rename: <paramref name="temp"/> over <paramref name="target"/>,
        /// tried again on <paramref name="retry"/> (<see cref="RetrySchedule.Quick"/> when not given)
        /// while another program holds either file, then diagnosed. On failure the temp file is left
        /// where it is; the caller decides whether to delete it or try again.
        /// </summary>
        /// <exception cref="FileAccessException">The rename kept failing; the message names the file and why.</exception>
        public static void Replace(string temp, string target, RetrySchedule? retry = null,
                                   Action<RetryAttempt>? onAttempt = null)
        {
            string full = Path.GetFullPath(target);
            string source = Path.GetFullPath(temp);
            FileRetry.Run(full, "save", () => File.Move(source, full, overwrite: true), retry, onAttempt, source);
        }

        /// <summary>
        /// Deletes SeedLab temp files left in a directory by a process that is no longer alive.
        /// Called on launch, because "clean up on exit" is exactly what a killed process cannot do.
        /// Returns how many were removed.
        /// </summary>
        public static int CleanOrphans(string directory, Func<int, bool>? isAlive = null)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;
            isAlive ??= ProcessLiveness.IsAlive;

            int removed = 0;
            string[] files;
            try { files = Directory.GetFiles(directory, TempPrefix + "*"); }
            catch (Exception) { return 0; }

            foreach (string f in files)
            {
                int pid = PidFromTempName(Path.GetFileName(f));
                if (pid > 0 && pid != Environment.ProcessId && isAlive(pid)) continue;
                if (pid == Environment.ProcessId) continue;
                try { File.Delete(f); removed++; }
                catch (Exception) { /* in use by someone else; leave it */ }
            }
            return removed;
        }

        internal static int PidFromTempName(string name)
        {
            if (!name.StartsWith(TempPrefix, StringComparison.Ordinal)) return -1;
            int i = TempPrefix.Length;
            int j = i;
            while (j < name.Length && name[j] >= '0' && name[j] <= '9') j++;
            if (j == i) return -1;
            return int.TryParse(name.Substring(i, j - i), out int pid) ? pid : -1;
        }
    }
}
