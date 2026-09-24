using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SeedLab.Runtime.Storage
{
    /// <summary>
    /// Reading a file another part of SeedLab may be writing, renaming or deleting at the same moment:
    /// a checkpoint, its kept-results snapshot, a survivor list, the session log.
    ///
    /// <para><b>Why the share mode matters</b> (2026-09-24). <c>File.ReadAllText</c> shares read only,
    /// so it fails against a writer that holds the file open for writing - the session log, a live
    /// results file - and while it holds the file it stops that writer deleting it. Sharing read, write
    /// and delete lets the writer carry on. It does NOT let the writer's rename replace the file while
    /// it is open: nothing a reader asks for can, which is why the writers retry
    /// (<see cref="DurableWrite.Replace"/>) and why these helpers hold the file only for the read.</para>
    /// </summary>
    public static class SharedRead
    {
        /// <summary>Read, write and delete: the most a reader can let a writer do.</summary>
        public const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

        public static FileStream Open(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, Share);

        public static string AllText(string path)
        {
            using FileStream fs = Open(path);
            using StreamReader r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return r.ReadToEnd();
        }

        public static string[] AllLines(string path)
        {
            using FileStream fs = Open(path);
            using StreamReader r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            List<string> lines = new List<string>();
            string? line;
            while ((line = r.ReadLine()) != null) lines.Add(line);
            return lines.ToArray();
        }
    }
}
