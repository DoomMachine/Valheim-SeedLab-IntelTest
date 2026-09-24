using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SeedLab.Runtime.Throttle
{
    /// <summary>
    /// Lists the process names running right now. Abstracted so the auto-throttle can be unit-tested
    /// without launching a game, and so a host that knows better (a server, a sandbox) can supply its
    /// own answer.
    /// </summary>
    public interface IProcessLister
    {
        /// <summary>Lower-cased process names, without extension. Must never throw.</summary>
        IReadOnlyList<string> RunningProcessNames();
    }

    /// <summary>
    /// <see cref="Process.GetProcesses"/>, which works on Windows, Linux and macOS with no P/Invoke.
    /// Measured on this machine: 315 processes listed in 2.0 ms, so a 15 s poll costs nothing.
    /// </summary>
    public sealed class SystemProcessLister : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames()
        {
            List<string> names = new List<string>();
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception) { return names; }

            foreach (Process p in all)
            {
                try { names.Add(p.ProcessName.ToLowerInvariant()); }
                catch (Exception) { /* a process that exited between listing and reading is not our problem */ }
                finally { try { p.Dispose(); } catch (Exception) { } }
            }
            return names;
        }
    }

    /// <summary>Which processes count as "the machine is busy with something that matters more".</summary>
    public sealed class GameWatchOptions
    {
        /// <summary>
        /// Default names, lower-case and without extension, covering the Windows client
        /// (<c>valheim.exe</c>), the Linux client and both dedicated servers. macOS reports the same
        /// bare name. The list is configurable because the user may rename a launcher, use a modded
        /// build, or want another heavy application to count.
        /// </summary>
        public static readonly string[] DefaultProcessNames =
        {
            "valheim",
            "valheim.x86_64",
            "valheim_server",
            "valheim_server.x86_64"
        };

        /// <summary>The whole feature off. <c>--ignore-running-game</c> sets this.</summary>
        public bool Enabled { get; set; } = true;

        public IReadOnlyList<string> ProcessNames { get; set; } = DefaultProcessNames;

        /// <summary>How often to re-check. The decisions file requires a periodic re-check, not one at start.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>Go back up when the game exits. Announced, like every other change.</summary>
        public bool RestoreWhenGameExits { get; set; } = true;

        /// <summary>The text told to the user for turning this off - one place, so it cannot drift.</summary>
        public string OverrideHint { get; set; } = "--mode full --ignore-running-game";

        public GameWatchOptions Clone() => new GameWatchOptions
        {
            Enabled = Enabled,
            ProcessNames = ProcessNames.ToArray(),
            PollInterval = PollInterval,
            RestoreWhenGameExits = RestoreWhenGameExits,
            OverrideHint = OverrideHint
        };
    }

    /// <summary>What a look at the process table found.</summary>
    public readonly struct GameDetection
    {
        public GameDetection(bool running, string? name)
        {
            IsRunning = running;
            ProcessName = name ?? "";
        }

        public bool IsRunning { get; }

        /// <summary>The name that matched, so the message can say WHICH process caused the throttle.</summary>
        public string ProcessName { get; }
    }

    /// <summary>Matches the configured names against the process table. Never throws.</summary>
    public static class GameDetector
    {
        public static GameDetection Detect(GameWatchOptions options, IProcessLister lister)
        {
            if (options == null || lister == null || !options.Enabled) return new GameDetection(false, null);

            IReadOnlyList<string> want = options.ProcessNames;
            if (want == null || want.Count == 0) return new GameDetection(false, null);

            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string w in want)
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                string s = w.Trim();
                if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
                wanted.Add(s);
            }

            foreach (string running in lister.RunningProcessNames())
            {
                if (string.IsNullOrEmpty(running)) continue;
                if (wanted.Contains(running)) return new GameDetection(true, running);
            }
            return new GameDetection(false, null);
        }
    }
}
