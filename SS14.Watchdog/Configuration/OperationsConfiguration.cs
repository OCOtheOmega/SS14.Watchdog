using System;
using System.Collections.Generic;

namespace SS14.Watchdog.Configuration;

public enum UpdateRestartMode { Countdown, AfterRound }

public sealed class UpdateRestartConfiguration
{
    public UpdateRestartMode Mode { get; set; } = UpdateRestartMode.Countdown;
    public int CountdownSeconds { get; set; } = 600;
    public int ShutdownTimeoutSeconds { get; set; } = 120;
}

public sealed class MaintenanceWindowConfiguration
{
    public string TimeZone { get; set; } = "UTC";
    public string Start { get; set; } = "04:00";
    public string End { get; set; } = "08:00";
    // Empty means every day. Overnight windows belong to the day on which they start.
    public List<DayOfWeek> Days { get; set; } = [];
}

public sealed class LowPopulationConfiguration
{
    public int MaximumPlayers { get; set; } = 5;
    public int StableSeconds { get; set; } = 600;
}

public sealed class CrashLoopConfiguration
{
    public bool Enabled { get; set; } = true;
    public int MaximumFailures { get; set; } = 5;
    public int WindowSeconds { get; set; } = 600;
    public int StableSeconds { get; set; } = 600;
    public int CooldownSeconds { get; set; } = 600;
    public List<int> BackoffSeconds { get; set; } = [5, 15, 30, 60];
}

public sealed class CleanupConfiguration
{
    public int IntervalSeconds { get; set; } = 3600;
    public int TemporaryMaxAgeHours { get; set; } = 24;
    public int DumpMaxAgeDays { get; set; } = 7;
    public int DumpMaximumMiB { get; set; } = 2048;
}
