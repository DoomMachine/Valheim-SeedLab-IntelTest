using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SeedLab.Runtime.Storage
{
    /// <summary>The owner stamp written into every scratch directory.</summary>
    public sealed class ScratchOwner
    {
        public const string FileName = "owner.txt";

        public ScratchOwner(int pid, DateTime startedUtc, string purpose, DateTime createdUtc)
        {
            Pid = pid;
            StartedUtc = startedUtc;
            Purpose = purpose ?? "";
            CreatedUtc = createdUtc;
        }

        public int Pid { get; }

        /// <summary>The OWNING PROCESS's start time, not the directory's: this is what defeats pid reuse.</summary>
        public DateTime StartedUtc { get; }

        public string Purpose { get; }
        public DateTime CreatedUtc { get; }

        public string Serialise()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("pid=").Append(Pid.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("started=").Append(StartedUtc.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("created=").Append(CreatedUtc.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("purpose=").Append(Purpose.Replace('\n', ' ')).Append('\n');
            return sb.ToString();
        }

        public static ScratchOwner? TryRead(string directory)
        {
            try
            {
                string f = Path.Combine(directory, FileName);
                if (!File.Exists(f)) return null;

                int pid = -1;
                DateTime started = DateTime.MinValue, created = DateTime.MinValue;
                string purpose = "";

                foreach (string line in File.ReadAllLines(f))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq);
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "pid": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid); break;
                        case "started": DateTime.TryParse(v, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out started); break;
                        case "created": DateTime.TryParse(v, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out created); break;
                        case "purpose": purpose = v; break;
                    }
                }
                if (pid <= 0) return null;
                return new ScratchOwner(pid, started.ToUniversalTime(), purpose, created.ToUniversalTime());
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// A per-process working directory under the cache root, named
    /// <c>pid-&lt;pid&gt;-&lt;process start, UTC&gt;</c>, deleted on dispose and - crucially - reaped by the
    /// NEXT launch when this process is killed instead of exiting.
    ///
    /// <para>The name carries both halves of the identity so the reaper never deletes a live run's
    /// directory after a pid has been reused; the same pair is written into <c>owner.txt</c> so the
    /// check survives a renamed folder.</para>
    /// </summary>
    public sealed class ScratchDirectory : IDisposable
    {
        private bool _disposed;

        private ScratchDirectory(string path, ScratchOwner owner)
        {
            Path = path;
            Owner = owner;
        }

        public string Path { get; }
        public ScratchOwner Owner { get; }

        /// <summary>A path inside the scratch directory; the directory is created if needed.</summary>
        public string File(string name)
        {
            Directory.CreateDirectory(Path);
            return System.IO.Path.Combine(Path, name);
        }

        public long SizeBytes => DiskUsageReport.DirectorySize(Path);

        internal static ScratchDirectory Create(CacheRoot root, string purpose)
        {
            int pid = ProcessLiveness.CurrentPid;
            DateTime start = ProcessLiveness.CurrentStartTimeUtc;
            string name = "pid-" + pid.ToString(CultureInfo.InvariantCulture)
                          + "-" + start.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

            string path = System.IO.Path.Combine(root.ScratchParent, name);
            Directory.CreateDirectory(path);

            ScratchOwner owner = new ScratchOwner(pid, start, purpose, DateTime.UtcNow);
            try { DurableWrite.Text(System.IO.Path.Combine(path, ScratchOwner.FileName), owner.Serialise()); }
            catch (Exception) { /* a scratch dir without a stamp is reaped on sight, which is safe */ }

            return new ScratchDirectory(path, owner);
        }

        /// <summary>Deletes the directory and everything in it. Safe to call twice; never throws.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (Exception) { /* the next launch's reaper will get it */ }
        }

        public override string ToString() => Path;
    }
}
