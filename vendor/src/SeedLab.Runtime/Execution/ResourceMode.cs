using System;
using System.Diagnostics;
using System.Globalization;

namespace SeedLab.Runtime.Execution
{
    /// <summary>
    /// How much of the machine a run may take. The default is <see cref="Balanced"/> everywhere -
    /// the CLI, the web UI and any scripted use - because SeedLab has to be safe on a machine that is
    /// not this one.
    /// </summary>
    public enum ResourceMode
    {
        /// <summary>~25 % of the logical cores at BelowNormal. The user is playing or working.</summary>
        Background = 0,

        /// <summary>~50 % of the logical cores at Normal. The default: the machine stays usable.</summary>
        Balanced = 1,

        /// <summary>Every logical core at Normal - never above it - subject to the memory guard.</summary>
        Full = 2
    }

    public static class ResourceModes
    {
        public const ResourceMode Default = ResourceMode.Balanced;

        /// <summary>The share of logical cores the mode asks for. 0.25 / 0.50 / 1.00, per the decisions file.</summary>
        public static double CoreShare(ResourceMode m) => m switch
        {
            ResourceMode.Background => 0.25,
            ResourceMode.Balanced => 0.50,
            ResourceMode.Full => 1.00,
            _ => 0.50
        };

        /// <summary>Never above Normal: a search must not be able to starve the window manager.</summary>
        public static ProcessPriorityClass Priority(ResourceMode m) =>
            m == ResourceMode.Background ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;

        public static System.Threading.ThreadPriority WorkerThreadPriority(ResourceMode m) =>
            m == ResourceMode.Background
                ? System.Threading.ThreadPriority.BelowNormal
                : System.Threading.ThreadPriority.Normal;

        public static string Name(ResourceMode m) => m switch
        {
            ResourceMode.Background => "background",
            ResourceMode.Balanced => "balanced",
            ResourceMode.Full => "full",
            _ => "balanced"
        };

        /// <summary>Parses the <c>--mode</c> value. Returns false, with a message, on anything else.</summary>
        public static bool TryParse(string? text, out ResourceMode mode, out string error)
        {
            mode = Default;
            error = "";
            string s = (text ?? "").Trim().ToLowerInvariant();
            switch (s)
            {
                case "": mode = Default; return true;
                case "background": case "bg": case "low": mode = ResourceMode.Background; return true;
                case "balanced": case "default": case "normal": mode = ResourceMode.Balanced; return true;
                case "full": case "max": case "all": mode = ResourceMode.Full; return true;
                default:
                    error = "'" + text + "' is not a resource mode - use background, balanced (the default) or full";
                    return false;
            }
        }

        public static string Describe(ResourceMode m) => m switch
        {
            ResourceMode.Background => "background (~25 % of cores, BelowNormal priority)",
            ResourceMode.Balanced => "balanced (~50 % of cores, Normal priority)",
            ResourceMode.Full => "full (all cores, Normal priority, still memory-capped)",
            _ => "balanced"
        };
    }
}
