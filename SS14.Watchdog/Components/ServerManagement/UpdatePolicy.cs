using System;
using System.Globalization;
using System.IO;
using System.Linq;
using SS14.Watchdog.Configuration;

namespace SS14.Watchdog.Components.ServerManagement;

public sealed class UpdatePolicy
{
    private DateTimeOffset? _lowSince;
    private LowPopulationConfiguration? _observedOptions;

    public static bool IsWindowOpen(DateTimeOffset now, MaintenanceWindowConfiguration? window)
    {
        if (window == null)
            return true;
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(window.TimeZone));
        var start = TimeOnly.ParseExact(window.Start, "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(window.End, "HH:mm", CultureInfo.InvariantCulture);
        if (start == end)
            throw new InvalidDataException("Maintenance window Start and End must differ.");
        var time = TimeOnly.FromDateTime(local.DateTime);
        var overnight = start > end;
        var day = overnight && time < end ? local.AddDays(-1).DayOfWeek : local.DayOfWeek;
        return (window.Days.Count == 0 || window.Days.Contains(day)) &&
               (overnight ? time >= start || time < end : time >= start && time < end);
    }

    public bool ObservePopulation(DateTimeOffset now, int? players, LowPopulationConfiguration? options)
    {
        if (!ReferenceEquals(options, _observedOptions))
        {
            _lowSince = null;
            _observedOptions = options;
        }
        if (options == null)
        {
            _lowSince = null;
            return true;
        }
        if (players == null || players < 0 || players > options.MaximumPlayers)
        {
            _lowSince = null;
            return false;
        }
        if (_lowSince == null || now < _lowSince)
            _lowSince = now;
        return now - _lowSince.Value >= TimeSpan.FromSeconds(options.StableSeconds);
    }

    public static bool CanFinishInWindow(DateTimeOffset now, TimeSpan duration, MaintenanceWindowConfiguration? window)
    {
        if (window == null)
            return true;
        if (!IsWindowOpen(now, window))
            return false;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(window.TimeZone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = TimeOnly.ParseExact(window.Start, "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(window.End, "HH:mm", CultureInfo.InvariantCulture);
        var closing = DateTime.SpecifyKind(local.Date + end.ToTimeSpan(), DateTimeKind.Unspecified);
        if (start > end && TimeOnly.FromDateTime(local.DateTime) >= start)
            closing = closing.AddDays(1);
        // A skipped local closing time closes at the first valid instant after the jump.
        while (zone.IsInvalidTime(closing))
            closing = closing.AddMinutes(1);
        var offsets = zone.IsAmbiguousTime(closing) ? zone.GetAmbiguousTimeOffsets(closing) : [zone.GetUtcOffset(closing)];
        var closeUtc = offsets.Select(offset => new DateTimeOffset(closing, offset))
            .Where(instant => instant > now).OrderBy(instant => instant).First();
        return now + duration <= closeUtc;
    }
}
