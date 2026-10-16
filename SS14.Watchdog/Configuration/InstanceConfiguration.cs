using System.Collections.Generic;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
using Microsoft.Diagnostics.NETCore.Client;

namespace SS14.Watchdog.Configuration
{
    [UsedImplicitly]
    public sealed class InstanceConfiguration
    {
        public string? Name { get; set; }
        public string? UpdateType { get; set; }
        /// <summary>
        ///     API Token will be read from this file if set.
        ///     Takes priority over <see cref="ApiToken"/> if not null.
        /// </summary>
        public string? ApiTokenFile { get; set; }
        public string? ApiToken { get; set; }
        public ushort ApiPort { get; set; }

        public string RunCommand { get; set; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "bin/Robust.Server.exe"
            : "bin/Robust.Server";

        /// <summary>
        ///     User arguments to pass to the server process.
        /// </summary>
        public List<string> Arguments { get; set; } = [];

        /// <summary>
        /// Make a heap dump if the server is killed due to timeout. Only supported on Linux.
        /// </summary>
        public bool DumpOnTimeout { get; set; } = true;

        /// <summary>
        /// When <see cref="DumpOnTimeout"/> is on, the type of dump to make.
        /// </summary>
        public DumpType TimeoutDumpType { get; set; } = DumpType.Normal;

        /// <summary>
        /// How long since the last ping before we consider the server "dead" and forcefully terminate it. In seconds.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 90;

        /// <summary>Time allowed for the first ping after starting a process.</summary>
        public int StartupTimeoutSeconds { get; set; } = 180;

        /// <summary>
        /// Whether the watchdog should periodically check the update provider for a new server build.
        /// </summary>
        public bool AutoUpdateChecks { get; set; } = true;

        /// <summary>
        /// The interval in seconds between automatic update checks.
        /// </summary>
        public int AutoUpdateCheckIntervalSeconds { get; set; } = 300;

        /// <summary>
        /// How long a newly installed server has to send its first watchdog ping before it is rolled back. In seconds.
        /// </summary>
        // Compatibility override for updated processes. Null uses StartupTimeoutSeconds.
        public int? AutoUpdateHealthTimeoutSeconds { get; set; }

        /// <summary>
        /// The maximum size, in MiB, accepted for a downloaded server archive.
        /// </summary>
        public int AutoUpdateMaximumDownloadMiB { get; set; } = 4096;

        /// <summary>
        /// The maximum total uncompressed size, in MiB, accepted from a server archive.
        /// </summary>
        public int AutoUpdateMaximumExtractedMiB { get; set; } = 8192;

        public int MinimumFreeSpaceMiB { get; set; } = 1024;
        public UpdateRestartConfiguration UpdateRestart { get; set; } = new();
        public MaintenanceWindowConfiguration? MaintenanceWindow { get; set; }
        public LowPopulationConfiguration? LowPopulation { get; set; }
        public int UpdatePolicyPollSeconds { get; set; } = 30;
        public CrashLoopConfiguration CrashLoop { get; set; } = new();
        public CleanupConfiguration Cleanup { get; set; } = new();

        /// <summary>
        /// Any additional environment variables for the server process.
        /// </summary>
        [UsedImplicitly]
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    }
}
