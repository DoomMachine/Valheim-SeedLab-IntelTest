using System;
using System.Collections.Generic;
using SeedLab.Runtime.Estimation;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Hardware;
using SeedLab.Runtime.SelfTest;
using SeedLab.Runtime.Storage;
using SeedLab.Runtime.Throttle;

namespace SeedLab.Runtime
{
    /// <summary>
    /// Everything a host has to decide before a run starts, in one options object so the CLI's flags and
    /// the web UI's form map onto the same thing.
    /// </summary>
    public sealed class RuntimeOptions
    {
        /// <summary><c>--mode</c>. Balanced unless the user says otherwise.</summary>
        public ResourceMode Mode { get; set; } = ResourceModes.Default;

        /// <summary><c>--threads N</c>: replaces the mode's share, still memory-capped.</summary>
        public int? Threads { get; set; }

        /// <summary><c>--cache-dir</c>.</summary>
        public string? CacheDirectory { get; set; }

        /// <summary><c>--ignore-running-game</c> sets this false.</summary>
        public bool AutoThrottle { get; set; } = true;

        /// <summary>Extra or replacement process names to watch for.</summary>
        public IReadOnlyList<string>? GameProcessNames { get; set; }

        public TimeSpan ThrottlePollInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary><c>--skip-self-test</c> sets this false. The result then says the run is unverified.</summary>
        public bool SelfTest { get; set; } = true;

        /// <summary><c>--accept-unverified-platform</c>.</summary>
        public bool AcceptUnverifiedPlatform { get; set; }

        /// <summary>Re-run the self-test even if this machine has a valid stamp.</summary>
        public bool ForceSelfTest { get; set; }

        /// <summary>Reap abandoned scratch directories and orphaned temp files at start. Leave on.</summary>
        public bool ReapOnStart { get; set; } = true;

        /// <summary>
        /// Where the layer's one-line announcements go. Defaults to nowhere. Every line also goes to
        /// the session log.
        /// </summary>
        public Action<string>? Log { get; set; }

        /// <summary>
        /// Write the session log, <c>&lt;cache root&gt;\logs\vseed.log</c> (<see cref="SessionLog"/>).
        /// On by default; a host that starts a second context in the same process for its own use may
        /// turn it off.
        /// </summary>
        public bool WriteSessionLog { get; set; } = true;

        /// <summary>
        /// The command line as the user typed it, for the session log's first lines. Null means the
        /// process's own (<see cref="Environment.CommandLine"/>), which is what a host that has
        /// nothing better should leave it at.
        /// </summary>
        public string? CommandLine { get; set; }

        /// <summary>
        /// The program and its version for the session log, "vseed 0.1.0". Null means the entry
        /// assembly's name and informational version.
        /// </summary>
        public string? Program { get; set; }

        /// <summary>Probe overrides, for tests and for "plan as if on a smaller machine".</summary>
        public HardwareProbeOptions? Probe { get; set; }

        /// <summary>For tests: a process lister that does not look at the real machine.</summary>
        public IProcessLister? ProcessLister { get; set; }

        public EstimateThresholds? Thresholds { get; set; }
    }

    /// <summary>
    /// The one object the CLI and the web server both hold for the life of a command or a search:
    /// the hardware probe, the cache root, the scratch directory, the auto-throttle, the estimator and
    /// the self-test outcome.
    ///
    /// <para>Start it once, print <see cref="StartupLines"/>, call <see cref="RequireVerifiedMachine"/>
    /// where a wrong answer would matter, plan workers with <see cref="PlanWorkers"/>, and dispose it at
    /// the end so the scratch directory and the process priority go back.</para>
    /// </summary>
    public sealed class RuntimeContext : IDisposable
    {
        private readonly List<string> _startupLines = new List<string>();
        private readonly List<AccessResult> _accessChecks = new List<AccessResult>();
        private readonly SessionLog? _previousLog;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private PriorityScope? _priority;
        private ScratchDirectory? _scratch;
        private bool _disposed;

        private RuntimeContext(RuntimeOptions options, HardwareInfo hardware, CacheRoot cache,
                               AutoThrottle throttle, Estimator estimator, MachineSelfTest selfTest,
                               SessionLog log, SessionLog? previousLog)
        {
            Options = options;
            Hardware = hardware;
            Cache = cache;
            Throttle = throttle;
            Estimator = estimator;
            SelfTest = selfTest;
            SessionLog = log;
            _previousLog = previousLog;
        }

        public RuntimeOptions Options { get; }
        public HardwareInfo Hardware { get; }
        public CacheRoot Cache { get; }
        public AutoThrottle Throttle { get; }
        public Estimator Estimator { get; }
        public MachineSelfTest SelfTest { get; }

        /// <summary>
        /// This session's log (<c>&lt;cache root&gt;\logs\vseed.log</c>). Never null; when no file
        /// could be opened, or <see cref="RuntimeOptions.WriteSessionLog"/> is off, it writes nowhere
        /// and <see cref="Storage.SessionLog.Problem"/> says why. A host adds its own lines to it: the
        /// command's outcome, a warning it printed, an exception's stack trace.
        /// </summary>
        public SessionLog SessionLog { get; }

        /// <summary>The access checks of the cache root and its folders, made at start.</summary>
        public IReadOnlyList<AccessResult> AccessChecks => _accessChecks;

        /// <summary>The command's exit code, when the host sets it: <see cref="Dispose"/> writes it on the last line.</summary>
        public int? ExitCode { get; set; }

        /// <summary>The self-test result, or null when <see cref="RuntimeOptions.SelfTest"/> is off.</summary>
        public SelfTestOutcome? SelfTestOutcome { get; private set; }

        public ReapReport? Reaped { get; private set; }

        /// <summary>The mode actually in force, after the auto-throttle has had its say.</summary>
        public ResourceMode EffectiveMode => Throttle.EffectiveMode;

        /// <summary>
        /// Probes the machine, opens the cache root and the session log, checks access to the cache's
        /// folders, reaps what a killed process left behind, looks for a running game, and runs the
        /// self-test. Cheap: every part of it is milliseconds.
        ///
        /// <para><b>The session log comes first</b> (2026-09-24), straight after the cache root it
        /// lives in, so everything after it - the access checks, the reap, the self-test, the throttle
        /// and priority lines, and whatever the host adds - is in it. A run that later fails to save a
        /// file can then be read against what the start of the session found.</para>
        /// </summary>
        public static RuntimeContext Start(RuntimeOptions? options = null)
        {
            RuntimeOptions o = options ?? new RuntimeOptions();
            Action<string>? hostLog = o.Log;

            HardwareInfo hw = HardwareProbe.Probe(o.Probe);
            CacheRoot cache = CacheRoot.Open(new CacheRootOptions { Override = o.CacheDirectory });

            SessionLog slog = o.WriteSessionLog
                ? SessionLog.Open(cache.Logs)
                : SessionLog.Disabled(cache.Logs, "this host turned the session log off");
            SessionLog? previous = SessionLog.Current;
            if (slog.IsOpen) SessionLog.Current = slog;

            // No log at all is said once, where the host's announcements go (stderr in the terminal);
            // the session itself goes on - a log is never a reason to refuse a run.
            if (!slog.IsOpen && o.WriteSessionLog) hostLog?.Invoke("note: this session keeps no log - " + slog.Problem);

            try
            {
                return StartLogged(o, hostLog, hw, cache, slog, previous);
            }
            catch (Exception ex)
            {
                slog.Exception("the session could not start", ex);
                if (ReferenceEquals(SessionLog.Current, slog)) SessionLog.Current = previous;
                slog.Dispose();
                throw;
            }
        }

        private static RuntimeContext StartLogged(RuntimeOptions o, Action<string>? hostLog, HardwareInfo hw,
                                                  CacheRoot cache, SessionLog slog, SessionLog? previous)
        {
            // Every announcement goes to the host AND the log, so the log holds what the user saw.
            Action<string> log = line =>
            {
                if (!string.IsNullOrEmpty(line)) slog.Info("runtime  " + line);
                hostLog?.Invoke(line);
            };

            WriteHeader(slog, o, hw, cache);

            GameWatchOptions watch = new GameWatchOptions
            {
                Enabled = o.AutoThrottle,
                PollInterval = o.ThrottlePollInterval
            };
            if (o.GameProcessNames != null && o.GameProcessNames.Count > 0)
            {
                string[] names = new string[o.GameProcessNames.Count];
                for (int i = 0; i < names.Length; i++) names[i] = o.GameProcessNames[i];
                watch.ProcessNames = names;
            }

            AutoThrottle throttle = new AutoThrottle(o.Mode, watch, log, o.ProcessLister);
            MachineSelfTest st = new MachineSelfTest(cache)
            {
                Enabled = o.SelfTest,
                AcceptUnverifiedPlatform = o.AcceptUnverifiedPlatform
            };

            RuntimeContext ctx = new RuntimeContext(o, hw, cache, throttle,
                new Estimator(o.Thresholds), st, slog, previous);

            // The cache root and every folder in it, before anything is written there. A folder that
            // fails is said now, by name, rather than as a bare "Access to the path is denied." from
            // whichever save first trips over it - and the ledger keeps the time of every pass, so a
            // later failure can say the folder was fine at the start.
            ctx._accessChecks.Add(AccessCheck.Directory(cache.Path));
            foreach ((string _, string dir) in cache.Categories) ctx._accessChecks.Add(AccessCheck.Directory(dir));
            string summary = AccessCheck.Summary(ctx._accessChecks);
            slog.Info("access   " + summary);
            foreach (AccessResult r in ctx._accessChecks)
            {
                if (r.Ok) continue;
                ctx._startupLines.Add("warning: " + r.Message);
                hostLog?.Invoke("warning: " + r.Message);
            }

            if (o.ReapOnStart)
            {
                ctx.Reaped = cache.ReapAbandoned();
                string line = ctx.Reaped.Line();
                if (line.Length > 0) { ctx._startupLines.Add(line); log(line); }
                else slog.Info("reap     nothing left behind by an earlier run");
            }

            ctx.SelfTestOutcome = st.Verify(hw, o.ForceSelfTest);
            if (ctx.SelfTestOutcome.Status != SelfTestStatus.PassedCached)
                ctx._startupLines.Add(ctx.SelfTestOutcome.Message);
            slog.Write(ctx.SelfTestOutcome.Ok || ctx.SelfTestOutcome.Status == SelfTestStatus.Skipped
                           ? SessionLogLevel.Info : SessionLogLevel.Error,
                       "selftest " + ctx.SelfTestOutcome.Status + ": " + ctx.SelfTestOutcome.Message);

            throttle.Start();
            ctx._priority = PriorityScope.Apply(ResourceModes.Priority(throttle.EffectiveMode));
            if (ctx._priority.Note.Length > 0) { ctx._startupLines.Add(ctx._priority.Note); log(ctx._priority.Note); }
            slog.Info("mode     " + ResourceModes.Describe(throttle.EffectiveMode)
                      + (throttle.IsThrottled ? "  [auto-throttled by " + throttle.ThrottledBy + "]" : ""));

            throttle.Changed += change =>
            {
                try
                {
                    ctx._priority?.Dispose();
                    ctx._priority = PriorityScope.Apply(ResourceModes.Priority(change.To));
                }
                catch (Exception) { }
            };

            return ctx;
        }

        /// <summary>
        /// The session log's first lines: when, what, how it was started, on what, and where it keeps
        /// its files. The line prefix already carries the local time and its UTC offset; the first
        /// line repeats it with the UTC time, so two logs from different machines can be lined up.
        /// </summary>
        private static void WriteHeader(SessionLog slog, RuntimeOptions o, HardwareInfo hw, CacheRoot cache)
        {
            if (!slog.IsOpen) return;
            DateTimeOffset now = DateTimeOffset.Now;
            slog.Info("session  started " + SessionLog.Timestamp(now) + " (UTC "
                      + now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + ")");
            slog.Info("program  " + (o.Program ?? DefaultProgram()));
            slog.Info("command  " + (o.CommandLine ?? Environment.CommandLine));
            slog.Info("process  pid " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                      + "; " + hw.OSDescription + " (" + hw.RuntimeIdentifier + "); " + hw.FrameworkDescription
                      + "; " + hw.ProcessArchitecture);
            foreach (string line in hw.Lines()) slog.Info("machine  " + line);
            slog.Info("cache    " + cache.Path + " (" + cache.SourceDetail + ")");
            slog.Info("log      " + slog.Path + (slog.Problem != null ? " - " + slog.Problem : ""));
            foreach (string gone in slog.DeletedStale)
            {
                slog.Info("log      deleted " + gone + ", a numbered log no session was using");
            }
        }

        /// <summary>"vseed 0.1.0+8eee037", from the entry assembly; the runtime layer's own when there is none.</summary>
        private static string DefaultProgram()
        {
            try
            {
                System.Reflection.Assembly a = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(RuntimeContext).Assembly;
                string version =
                    (Attribute.GetCustomAttribute(a, typeof(System.Reflection.AssemblyInformationalVersionAttribute))
                        as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion
                    ?? a.GetName().Version?.ToString() ?? "?";
                return (a.GetName().Name ?? "?") + " " + version;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>The per-process scratch directory, created on first use and deleted on dispose.</summary>
        public ScratchDirectory Scratch(string purpose = "run") =>
            _scratch ??= Cache.CreateScratch(purpose);

        /// <summary>Fail closed. Call this before anything whose answer a user would act on.</summary>
        public void RequireVerifiedMachine() => SelfTestOutcome?.EnsureUsable();

        /// <summary>Workers and priority for a tier and grid, at the mode in force right now.</summary>
        public WorkerPlan PlanWorkers(WorkTier tier, long gridCells, long reservedBytes = 0) =>
            WorkerPlanner.Plan(EffectiveMode, WorkerFootprints.For(tier, gridCells), Hardware,
                new WorkerPlanOptions { RequestedWorkers = Options.Threads, ReservedBytes = reservedBytes });

        /// <summary>Workers for a footprint the engine measured itself.</summary>
        public WorkerPlan PlanWorkers(WorkerFootprint footprint, long reservedBytes = 0) =>
            WorkerPlanner.Plan(EffectiveMode, footprint, Hardware,
                new WorkerPlanOptions { RequestedWorkers = Options.Threads, ReservedBytes = reservedBytes });

        /// <summary>
        /// Fills in the machine-dependent half of an estimate (threads, free space, available RAM,
        /// footprint) so the caller only supplies the query's half.
        /// </summary>
        public EstimateInputs Inputs(EstimateInputs query, WorkerPlan plan, string outputPath)
        {
            EstimateInputs i = query.Clone();
            i.Threads = plan.Workers;
            i.Footprint = plan.Footprint;
            i.AvailableMemoryBytes = Hardware.AvailableMemoryBytes;
            i.FreeSpaceBytes = VolumeInfo.For(string.IsNullOrWhiteSpace(outputPath) ? Cache.Path : outputPath).FreeBytes;
            return i;
        }

        /// <summary>What to print at the top of a run: the machine, the cache, the self-test, the throttle.</summary>
        public IReadOnlyList<string> StartupLines()
        {
            List<string> l = new List<string>(Hardware.Lines());
            l.Add("cache       " + Cache.Path + " (" + Cache.SourceDetail + ")");
            l.Add("log         " + (SessionLog.Path != null
                      ? SessionLog.Path + (SessionLog.Problem != null ? " (" + SessionLog.Problem + ")" : "")
                      : "none - " + (SessionLog.Problem ?? "no log was opened")));
            l.Add("mode        " + ResourceModes.Describe(EffectiveMode)
                  + (Throttle.IsThrottled ? "  [auto-throttled by " + Throttle.ThrottledBy + "]" : ""));
            l.AddRange(_startupLines);
            return l;
        }

        /// <summary>
        /// Stops the throttle, deletes the scratch directory, restores the priority, and ends the
        /// session log with how long the session took (and <see cref="ExitCode"/>, when the host set it).
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Throttle.Dispose(); } catch (Exception) { }
            try { _scratch?.Dispose(); } catch (Exception) { }
            try { _priority?.Dispose(); } catch (Exception) { }

            SessionLog.Info("session  ended after "
                            + _clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"
                            + (ExitCode.HasValue
                                ? " with exit code " + ExitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                : ""));
            if (ReferenceEquals(Storage.SessionLog.Current, SessionLog)) Storage.SessionLog.Current = _previousLog;
            SessionLog.Dispose();
        }
    }
}
