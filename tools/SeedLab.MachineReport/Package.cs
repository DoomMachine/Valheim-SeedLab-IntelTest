using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SeedLab.MachineReport
{
    /// <summary>
    /// The package folder and everything the tool may touch inside it. The tool reads and writes
    /// nothing outside this folder: <c>work\</c> is its scratch space (deleted at the end) and
    /// <c>seedlab-machine-report.txt</c> is its only lasting output.
    /// </summary>
    public static class Package
    {
        /// <summary>The folder holding this program: <c>&lt;package&gt;\app</c>.</summary>
        public static readonly string AppDir;

        /// <summary>The package folder: the parent of <see cref="AppDir"/>.</summary>
        public static readonly string Root;

        static Package()
        {
            string app = Native.LongPath(Path.GetFullPath(AppContext.BaseDirectory))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            AppDir = app;
            Root = Path.GetDirectoryName(app) ?? app;
        }

        public static string DotnetDir => Path.Combine(Root, "dotnet");
        public static string NativesDir => Path.Combine(Root, "natives");
        public static string ReferenceFile => Path.Combine(Root, "reference", "fingerprints.json");
        public static string WorkDir => Path.Combine(Root, "work");
        public static string ReportFile => Path.Combine(Root, "seedlab-machine-report.txt");
        public static string ManifestFile => Path.Combine(Root, "package-files.sha256");

        /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> or lies under it.</summary>
        public static bool IsInside(string? path, string dir)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = Normalize(path);
            string d = Normalize(dir);
            if (string.Equals(p, d, StringComparison.OrdinalIgnoreCase)) return true;
            return p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { full = path; }
            return Native.LongPath(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// A path as the report may show it: relative to the package, or a fixed phrase when it is
        /// outside - the tester's folder names never reach the report.
        /// </summary>
        public static string Show(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "(none)";
            if (!IsInside(path, Root)) return "(outside this package)";
            string rel = Path.GetRelativePath(Normalize(Root), Normalize(path));
            return rel == "." ? "(the package folder)" : rel;
        }

        // ---- sanitising ------------------------------------------------------------------------

        // The rest of a path, once one has started. Folder names may hold spaces and apostrophes
        // (C:\Users\Bob Smith\, O'Brien), so the path runs on until a character no Windows path can
        // hold - " < > | * ? - or the end of the line: more text than the path may be withheld, never
        // less. In JSON-escaped text a \\ pair is one backslash and \" ends the path, so the
        // replacement never breaks the JSON around it.
        private const string PathTail = @"(?:\\\\|[^""<>|*?\r\n\\]|\\(?!""))*";

        // A drive path, C:\... or C:/..., in plain text or JSON-escaped (C:\\...). It may follow a
        // JSON escape such as \n directly.
        private static readonly Regex DrivePath = new Regex(
            @"(?i)(?:(?<![a-z0-9])|(?<=\\[nrt]))[a-z]:(?:\\\\|\\|/)" + PathTail, RegexOptions.Compiled);

        // A network path, \\server\share\..., in plain text or JSON-escaped (\\\\server\\share). The
        // look-behind keeps a relative path out of it: in JSON, dotnet\shared becomes dotnet\\shared,
        // whose double backslash follows a letter, not the start of a path.
        private static readonly Regex UncPath = new Regex(
            @"(?<![\\\w])\\\\\\\\[\w.$-]+\\\\" + PathTail + @"|(?<![\\\w])\\\\[\w.$-]+\\" + PathTail,
            RegexOptions.Compiled);

        private static List<string>? _rootForms;

        private static List<string> RootForms()
        {
            if (_rootForms != null) return _rootForms;
            List<string> forms = new List<string>();
            void Add(string? f)
            {
                if (string.IsNullOrEmpty(f)) return;
                foreach (string v in new[] { f, f.Replace('\\', '/'), f.Replace("\\", "\\\\") })
                    if (!forms.Contains(v)) forms.Add(v);
            }

            Add(Root);
            Add(Native.ShortPath(Root));
            Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")).TrimEnd('\\', '/'));
            // Longest first, so a longer spelling is replaced before a prefix of it.
            forms.Sort((a, b) => b.Length.CompareTo(a.Length));
            _rootForms = forms;
            return forms;
        }

        /// <summary>
        /// Every piece of text that can reach the report passes through here: the package folder
        /// becomes <c>&lt;package&gt;</c>, and any other absolute path that is left (a drive path
        /// or a network path) is withheld. The report never needs one.
        /// </summary>
        public static string Sanitize(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string s = text;
            foreach (string f in RootForms())
            {
                s = ReplaceIgnoreCase(s, f, "<package>");
            }

            s = DrivePath.Replace(s, "<path outside the package>");
            s = UncPath.Replace(s, "<path outside the package>");
            return s;
        }

        private static string ReplaceIgnoreCase(string s, string find, string with)
        {
            if (find.Length == 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            int i = 0;
            while (true)
            {
                int k = s.IndexOf(find, i, StringComparison.OrdinalIgnoreCase);
                if (k < 0) break;
                sb.Append(s, i, k - i).Append(with);
                i = k + find.Length;
            }

            sb.Append(s, i, s.Length - i);
            return sb.ToString();
        }

        // ---- the package manifest ---------------------------------------------------------------

        public sealed class ManifestResult
        {
            public bool Present;
            public int Listed;
            public int Matching;
            public List<string> Missing = new List<string>();
            public List<string> Changed = new List<string>();
            public List<string> Unlisted = new List<string>();
            public bool Ok => Present && Listed > 0 && Matching == Listed && Unlisted.Count == 0;
        }

        /// <summary>
        /// Checks every file the build listed in <c>package-files.sha256</c>, and looks for files in
        /// <c>app\</c> and <c>dotnet\</c> the build did not put there (for example a quarantined
        /// or replaced DLL). Read-only.
        /// </summary>
        public static ManifestResult CheckManifest()
        {
            ManifestResult r = new ManifestResult();
            if (!File.Exists(ManifestFile)) return r;
            r.Present = true;
            HashSet<string> listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(ManifestFile))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int sp = line.IndexOf(' ');
                if (sp != 64) continue;
                string want = line.Substring(0, 64);
                string rel = line.Substring(sp + 1).TrimStart('*', ' ').Replace('/', Path.DirectorySeparatorChar);
                listed.Add(rel);
                r.Listed++;
                string full = Path.Combine(Root, rel);
                if (!File.Exists(full))
                {
                    r.Missing.Add(rel);
                    continue;
                }

                string got = Sha256File(full);
                if (string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) r.Matching++;
                else r.Changed.Add(rel);
            }

            foreach (string sub in new[] { "app", "dotnet", "natives", "reference" })
            {
                string d = Path.Combine(Root, sub);
                if (!Directory.Exists(d)) continue;
                foreach (string f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(Root, f);
                    if (!listed.Contains(rel)) r.Unlisted.Add(rel);
                }
            }

            return r;
        }

        public static string Sha256File(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        // ---- which runtime is this ---------------------------------------------------------------

        public sealed class RuntimeProof
        {
            public string Version = "";
            public string Framework = "";
            public string RuntimeDir = "";
            public bool RuntimeDirInside;
            public List<(string Module, bool Inside, string Where)> Modules = new List<(string, bool, string)>();
            public string UcrtLoadedFrom = "";
            public bool Bundled;
        }

        private static readonly string[] RuntimeModules =
            { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clrjit.dll", "System.Private.CoreLib.dll" };

        /// <summary>
        /// Proves which .NET runtime this process is running on: the directory CoreLib was loaded
        /// from, and the directory of every native runtime module in the process. "Bundled" means all
        /// of them are inside the package's <c>dotnet\</c> folder.
        /// </summary>
        public static RuntimeProof ProveRuntime()
        {
            RuntimeProof p = new RuntimeProof
            {
                Version = Environment.Version.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
            };
            string corelib = typeof(object).Assembly.Location;
            string dir = Path.GetDirectoryName(corelib) ?? "";
            p.RuntimeDir = Show(dir);
            p.RuntimeDirInside = IsInside(dir, DotnetDir);

            Dictionary<string, string> found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using Process me = Process.GetCurrentProcess();
                foreach (ProcessModule m in me.Modules)
                {
                    string name = m.ModuleName ?? "";
                    string file = m.FileName ?? "";
                    if (Array.Exists(RuntimeModules, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                        found[name] = file;
                    if (string.Equals(name, "ucrtbase.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        string sys = Environment.SystemDirectory;
                        p.UcrtLoadedFrom = IsInside(file, sys) ? "System32" : (IsInside(file, Root) ? "the package" : "elsewhere");
                    }
                }
            }
            catch (Exception)
            {
                // Module listing can be refused by security software; CoreLib's location still decides.
            }

            bool allInside = p.RuntimeDirInside;
            foreach (string name in RuntimeModules)
            {
                if (string.Equals(name, "System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase))
                {
                    p.Modules.Add((name, p.RuntimeDirInside, p.RuntimeDir));
                    continue;
                }

                if (found.TryGetValue(name, out string? file))
                {
                    bool inside = IsInside(file, DotnetDir);
                    allInside &= inside;
                    p.Modules.Add((name, inside, Show(Path.GetDirectoryName(file))));
                }
                else if (!string.Equals(name, "clrjit.dll", StringComparison.OrdinalIgnoreCase))
                {
                    // hostfxr, hostpolicy and coreclr are always loaded by an apphost launch.
                    allInside = false;
                    p.Modules.Add((name, false, "(not seen in the process)"));
                }
            }

            p.Bundled = allInside;
            return p;
        }
    }
}
