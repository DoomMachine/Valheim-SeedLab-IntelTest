using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SeedLab.Runtime.Storage
{
    public enum SessionLogLevel
    {
        Info = 0,
        Warn = 1,
        Error = 2,
    }

    /// <summary>
    /// One session's log: <c>&lt;cache root&gt;\logs\vseed.log</c>, rewritten from empty at the start of
    /// every session, the way the game's BepInEx writes its <c>LogOutput.log</c>.
    ///
    /// <para><b>The model, and where it differs</b> (2026-09-24, BepInEx 5.4.23.3's DiskLogListener
    /// decompiled). BepInEx opens its log with <c>FileMode.Create</c> (so every start empties it),
    /// <c>FileAccess.Write</c> and <c>FileShare.Read</c>; when that fails because another game instance
    /// holds it, it tries <c>LogOutput.log.1</c> .. <c>.4</c>, and after five names it runs with no disk
    /// log. This does the same, with three differences the user's request and a crash both need:</para>
    /// <list type="bullet">
    /// <item><b>Stale numbered logs are deleted.</b> BepInEx never deletes a <c>.N</c> file, so one
    /// left by an old overlap stays forever. The user asked for the log to be purged every session, so
    /// after opening its own, a session deletes every numbered log no live session holds.</item>
    /// <item><b>Every line is flushed.</b> BepInEx flushes on a 2 s timer, so a crash loses up to two
    /// seconds - the part a crash report needs. Here each line goes to the operating system as it is
    /// written, which a crashed or killed process cannot take back. (It is not forced to the disk
    /// itself; a power cut can still lose the tail.)</item>
    /// <item><b>It never throws.</b> BepInEx catches only the sharing failure; a read-only log file
    /// throws out of its constructor. Here any failure to open a name moves on to the next one, and
    /// any failure to write stops the log quietly - a log must never be why a run fails.</item>
    /// </list>
    ///
    /// <para><b>Share mode.</b> Read only, deliberately not Delete: a second session cannot empty or
    /// delete a live session's log, and so falls back to the next name. Anything that reads the log
    /// while a session runs - a person, <c>vseed clean</c> - has to open it sharing read AND write.</para>
    ///
    /// <para><b>Line format:</b> <c>yyyy-MM-dd HH:mm:ss.fff zzz  LEVEL  text</c>, local time with its
    /// UTC offset; continuation lines are indented to the text column.</para>
    /// </summary>
    public sealed class SessionLog : IDisposable
    {
        /// <summary>The log's name inside the logs folder.</summary>
        public const string FileName = "vseed.log";

        /// <summary>How many numbered names follow the plain one: vseed.log.1 .. vseed.log.4.</summary>
        public const int Fallbacks = 4;

        private readonly object _gate = new object();
        private StreamWriter? _writer;

        private SessionLog(string directory, string? path, StreamWriter? writer, string? problem, int index,
                           IReadOnlyList<string> deleted)
        {
            Directory = directory;
            Path = path;
            _writer = writer;
            Problem = problem;
            Index = index;
            DeletedStale = deleted;
            Started = DateTimeOffset.Now;
        }

        /// <summary>
        /// The log of the session this process is running, for code too deep to be handed one - the
        /// retry helper, the access checks. Set by <see cref="RuntimeContext.Start"/> and put back when
        /// that context is disposed. Null outside a session: every write to it is then skipped.
        /// </summary>
        public static SessionLog? Current { get; set; }

        /// <summary>The folder the log lives in.</summary>
        public string Directory { get; }

        /// <summary>The file this session writes, or null when no name could be opened.</summary>
        public string? Path { get; }

        /// <summary>True while lines are being written to a file.</summary>
        public bool IsOpen
        {
            get
            {
                lock (_gate) return _writer != null;
            }
        }

        /// <summary>0 for vseed.log, 1..4 for a numbered fallback, -1 for no log.</summary>
        public int Index { get; }

        /// <summary>
        /// Why this session writes a numbered log, or none at all - "vseed.log is in use by another
        /// session" - or null when it writes vseed.log as normal.
        /// </summary>
        public string? Problem { get; }

        /// <summary>Numbered logs from earlier sessions that this one deleted on opening.</summary>
        public IReadOnlyList<string> DeletedStale { get; }

        public DateTimeOffset Started { get; }

        /// <summary>
        /// Opens this session's log in <paramref name="directory"/> (creating the folder), emptying the
        /// previous session's. Never throws: when nothing can be opened the result writes nowhere and
        /// <see cref="Problem"/> says why.
        /// </summary>
        public static SessionLog Open(string directory, string fileName = FileName)
        {
            string dir = directory ?? "";
            try
            {
                dir = System.IO.Path.GetFullPath(dir);
                System.IO.Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                return new SessionLog(dir, null, null,
                    "the log folder " + dir + " could not be created (" + FileRetry.Describe(ex) + ")", -1,
                    Array.Empty<string>());
            }

            List<string> refused = new List<string>();
            for (int i = 0; i <= Fallbacks; i++)
            {
                string path = NameAt(dir, fileName, i);
                FileStream? fs = null;
                try
                {
                    fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    StreamWriter w = new StreamWriter(fs, DurableWrite.Utf8NoBom) { AutoFlush = true, NewLine = "\n" };
                    List<string> deleted = DeleteStale(dir, fileName, i);
                    string? problem = i == 0
                        ? null
                        : string.Join(", ", refused) + ", so this session writes " + System.IO.Path.GetFileName(path);
                    SessionLog log = new SessionLog(dir, path, w, problem, i, deleted);
                    log.Info("vseed session log. This file is emptied at the start of every session. While another session "
                             + "still has " + fileName + " open, the new one writes " + fileName + ".1 (up to ." + Fallbacks
                             + ") instead, and numbered logs no session is using are deleted when the next session starts.");
                    return log;
                }
                catch (Exception ex)
                {
                    try
                    {
                        fs?.Dispose();
                    }
                    catch (Exception)
                    {
                    }

                    refused.Add(System.IO.Path.GetFileName(path)
                                + (ex is UnauthorizedAccessException ? " is read-only or not allowed"
                                   : ex.HResult == FileRetry.SharingViolation || ex.HResult == FileRetry.LockViolation
                                       ? " is in use by another session"
                                       : " could not be opened"));
                }
            }

            return new SessionLog(dir, null, null,
                string.Join(", ", refused) + ", so this session keeps no log", -1, Array.Empty<string>());
        }

        /// <summary>A log that writes nowhere, for a session that is not to keep one; <see cref="Problem"/> is <paramref name="why"/>.</summary>
        public static SessionLog Disabled(string directory, string why) =>
            new SessionLog(directory ?? "", null, null, why, -1, Array.Empty<string>());

        /// <summary>vseed.log for 0, vseed.log.N for N.</summary>
        public static string NameAt(string directory, string fileName, int index) =>
            System.IO.Path.Combine(directory, index == 0 ? fileName : fileName + "." + index.ToString(CultureInfo.InvariantCulture));

        public void Info(string text) => Write(SessionLogLevel.Info, text);

        public void Warn(string text) => Write(SessionLogLevel.Warn, text);

        public void Error(string text) => Write(SessionLogLevel.Error, text);

        /// <summary>An exception's type, message and stack trace - for the log only, never for the screen.</summary>
        public void Exception(string what, Exception ex)
        {
            if (ex == null) return;
            Write(SessionLogLevel.Error, what + ": " + ex.GetType().FullName + " 0x"
                                         + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ": " + ex.Message
                                         + "\n" + ex);
        }

        /// <summary>One entry. Several lines are kept together, indented under the first. Never throws.</summary>
        public void Write(SessionLogLevel level, string text)
        {
            lock (_gate)
            {
                if (_writer == null) return;
                try
                {
                    string prefix = Timestamp(DateTimeOffset.Now) + "  " + LevelName(level) + "  ";
                    string indent = new string(' ', prefix.Length);
                    string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
                    StringBuilder sb = new StringBuilder(prefix.Length + (text?.Length ?? 0) + 8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        sb.Append(i == 0 ? prefix : indent).Append(lines[i]).Append('\n');
                    }

                    _writer.Write(sb.ToString());
                }
                catch (Exception)
                {
                    // A full disk or a vanished drive: stop logging rather than fail whatever was being logged.
                    Close();
                }
            }
        }

        /// <summary>"2026-09-24 14:03:12.345 +03:00": local time with its UTC offset, as every line starts.</summary>
        public static string Timestamp(DateTimeOffset t) =>
            t.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

        private static string LevelName(SessionLogLevel l) => l switch
        {
            SessionLogLevel.Warn => "WARN ",
            SessionLogLevel.Error => "ERROR",
            _ => "INFO ",
        };

        /// <summary>Closes the file. Safe to call twice; never throws.</summary>
        public void Dispose()
        {
            lock (_gate) Close();
        }

        private void Close()
        {
            try
            {
                _writer?.Dispose();
            }
            catch (Exception)
            {
            }

            _writer = null;
        }

        /// <summary>
        /// Deletes the numbered logs other than <paramref name="own"/>. A live session holds its log
        /// without sharing delete, so its file refuses and is left alone; the plain vseed.log is never
        /// deleted here, because the next session that can open it empties it anyway.
        /// </summary>
        private static List<string> DeleteStale(string dir, string fileName, int own)
        {
            List<string> deleted = new List<string>();
            for (int i = 1; i <= Fallbacks; i++)
            {
                if (i == own) continue;
                string p = NameAt(dir, fileName, i);
                try
                {
                    if (!File.Exists(p)) continue;
                    File.Delete(p);
                    deleted.Add(p);
                }
                catch (Exception)
                {
                    // In use by a live session, or not ours to delete.
                }
            }

            return deleted;
        }
    }
}
