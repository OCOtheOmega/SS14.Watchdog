using System;
using System.IO;
using System.Linq;
using SS14.Watchdog.Components.ServerManagement;

namespace SS14.Watchdog.Configuration;

public static class OperationsValidation
{
    public static void Validate(InstanceConfiguration options)
    {
        const int maximumTimerSeconds = 4_294_967;
        if (!Enum.IsDefined(options.UpdateRestart.Mode) || options.UpdateRestart.CountdownSeconds is <= 0 or > 86400 ||
            options.UpdateRestart.ShutdownTimeoutSeconds is <= 0 or > maximumTimerSeconds)
            throw new InvalidDataException("UpdateRestart requires a valid Mode, CountdownSeconds between 1 and 86400, and a positive ShutdownTimeoutSeconds up to 4294967.");
        if (options.StartupTimeoutSeconds <= 0 || options.TimeoutSeconds <= 0 || options.UpdatePolicyPollSeconds <= 0)
            throw new InvalidDataException("StartupTimeoutSeconds, TimeoutSeconds and UpdatePolicyPollSeconds must be positive.");
        if (options.StartupTimeoutSeconds > maximumTimerSeconds || options.TimeoutSeconds > maximumTimerSeconds ||
            options.UpdatePolicyPollSeconds > maximumTimerSeconds || options.AutoUpdateCheckIntervalSeconds > maximumTimerSeconds ||
            options.AutoUpdateHealthTimeoutSeconds > maximumTimerSeconds || options.CrashLoop.CooldownSeconds > maximumTimerSeconds ||
            options.CrashLoop.BackoffSeconds.Any(s => s > maximumTimerSeconds))
            throw new InvalidDataException("Timer intervals must not exceed 4294967 seconds.");
        if (options.MinimumFreeSpaceMiB < 0 || options.AutoUpdateMaximumDownloadMiB <= 0 || options.AutoUpdateMaximumExtractedMiB <= 0)
            throw new InvalidDataException("Disk reserve must be non-negative and archive limits must be positive.");
        if (options.LowPopulation is { } low && (low.MaximumPlayers < 0 || low.StableSeconds < 0))
            throw new InvalidDataException("LowPopulation values must be non-negative.");
        if (options.MaintenanceWindow != null)
        {
            if (options.MaintenanceWindow.Days.Any(day => !Enum.IsDefined(day)))
                throw new InvalidDataException("MaintenanceWindow contains an invalid day of the week.");
            _ = UpdatePolicy.IsWindowOpen(DateTimeOffset.UtcNow, options.MaintenanceWindow);
        }
        var crash = options.CrashLoop;
        if (crash.MaximumFailures <= 0 || crash.WindowSeconds <= 0 || crash.StableSeconds <= 0 ||
            crash.CooldownSeconds <= 0 || crash.BackoffSeconds.Any(s => s < 0))
            throw new InvalidDataException("CrashLoop limits must be positive and backoff delays must be non-negative.");
        var cleanup = options.Cleanup;
        if (cleanup.IntervalSeconds <= 0 || cleanup.TemporaryMaxAgeHours <= 0 || cleanup.DumpMaxAgeDays <= 0 || cleanup.DumpMaximumMiB < 0)
            throw new InvalidDataException("Cleanup ages and interval must be positive; dump budget must be non-negative.");
    }
}
