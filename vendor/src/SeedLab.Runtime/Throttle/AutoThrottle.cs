using System;
using System.Threading;
using SeedLab.Runtime.Execution;

namespace SeedLab.Runtime.Throttle
{
    /// <summary>Why the effective mode changed, and what it changed to.</summary>
    public sealed class ModeChange
    {
        public ModeChange(ResourceMode from, ResourceMode to, string reason, string message)
        {
            From = from;
            To = to;
            Reason = reason;
            Message = message;
        }

        public ResourceMode From { get; }
        public ResourceMode To { get; }

        /// <summary>"game-started", "game-exited" or "manual".</summary>
        public string Reason { get; }

        /// <summary>The single line to show the user. Never empty: no speed change is ever silent.</summary>
        public string Message { get; }
    }

    /// <summary>
    /// Drops to <see cref="ResourceMode.Background"/> while Valheim is running, and says so, once.
    ///
    /// <para>Decided behaviour: announce every change in ONE line that names the process and the
    /// override; re-check on a timer rather than only at start; and let the whole feature be turned off
    /// (<c>--ignore-running-game</c>). It never raises the mode above what the user asked for.</para>
    ///
    /// <para>Poll it manually with <see cref="Poll"/> (the search loop can call it between slices, which
    /// is what the CLI should do) or let <see cref="Start"/> run a timer. Both are safe to mix; changes
    /// are serialised on an internal lock and <see cref="EffectiveMode"/> is volatile-read.</para>
    /// </summary>
    public sealed class AutoThrottle : IDisposable
    {
        private readonly object _gate = new object();
        private readonly GameWatchOptions _options;
        private readonly IProcessLister _lister;
        private readonly Action<string> _announce;
        private Timer? _timer;
        private int _effective;
        private bool _throttled;
        private bool _announcedOnce;
        private bool _disposed;

        public AutoThrottle(ResourceMode requested, GameWatchOptions? options = null,
                            Action<string>? announce = null, IProcessLister? lister = null)
        {
            RequestedMode = requested;
            _effective = (int)requested;
            _options = (options ?? new GameWatchOptions()).Clone();
            _lister = lister ?? new SystemProcessLister();
            _announce = announce ?? (_ => { });
        }

        /// <summary>The mode the user asked for - what we return to when the game exits.</summary>
        public ResourceMode RequestedMode { get; }

        /// <summary>The mode to run at right now.</summary>
        public ResourceMode EffectiveMode => (ResourceMode)Volatile.Read(ref _effective);

        /// <summary>True while a detected game is holding us in background mode.</summary>
        public bool IsThrottled { get { lock (_gate) return _throttled; } }

        /// <summary>The process that caused the current throttle, or "".</summary>
        public string ThrottledBy { get; private set; } = "";

        /// <summary>Raised on every change, with the line that was announced.</summary>
        public event Action<ModeChange>? Changed;

        /// <summary>Checks once now, then starts the periodic re-check. Returns the effective mode.</summary>
        public ResourceMode Start()
        {
            ResourceMode m = Poll();
            if (_options.Enabled && _options.PollInterval > TimeSpan.Zero)
            {
                lock (_gate)
                {
                    if (_disposed) return m;
                    _timer ??= new Timer(_ => { try { Poll(); } catch (Exception) { } },
                                         null, _options.PollInterval, _options.PollInterval);
                }
            }
            return m;
        }

        /// <summary>
        /// One look at the process table, applying or lifting the throttle. Cheap (2 ms on this machine)
        /// and safe to call between slices.
        /// </summary>
        public ResourceMode Poll()
        {
            if (!_options.Enabled) return EffectiveMode;

            GameDetection d = GameDetector.Detect(_options, _lister);
            ModeChange? change = null;

            lock (_gate)
            {
                if (d.IsRunning && !_throttled)
                {
                    ResourceMode from = (ResourceMode)_effective;
                    if (from != ResourceMode.Background)
                    {
                        Volatile.Write(ref _effective, (int)ResourceMode.Background);
                        change = new ModeChange(from, ResourceMode.Background, "game-started",
                            d.ProcessName + " is running - dropping to background mode (~25 % of cores, "
                            + "BelowNormal). Override with " + _options.OverrideHint + ".");
                    }
                    _throttled = true;
                    ThrottledBy = d.ProcessName;
                    _announcedOnce = true;
                }
                else if (!d.IsRunning && _throttled)
                {
                    _throttled = false;
                    string was = ThrottledBy;
                    ThrottledBy = "";
                    if (_options.RestoreWhenGameExits && (ResourceMode)_effective != RequestedMode)
                    {
                        ResourceMode from = (ResourceMode)_effective;
                        Volatile.Write(ref _effective, (int)RequestedMode);
                        change = new ModeChange(from, RequestedMode, "game-exited",
                            (was.Length > 0 ? was : "the game") + " has exited - back to "
                            + ResourceModes.Name(RequestedMode) + " mode.");
                    }
                }
            }

            if (change != null)
            {
                _announce(change.Message);
                Changed?.Invoke(change);
            }
            return EffectiveMode;
        }

        /// <summary>True once the throttle has ever fired - so a summary can mention it.</summary>
        public bool EverThrottled { get { lock (_gate) return _announcedOnce; } }

        public void Dispose()
        {
            Timer? t;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                t = _timer;
                _timer = null;
            }
            try { t?.Dispose(); } catch (Exception) { }
        }
    }
}
