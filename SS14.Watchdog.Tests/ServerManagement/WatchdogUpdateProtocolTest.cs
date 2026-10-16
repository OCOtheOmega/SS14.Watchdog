using System;
using System.Text.Json;
using Content.Server.ServerUpdates;
using NUnit.Framework;
using SS14.Watchdog.Components.ServerManagement;
using SS14.Watchdog.Configuration;

namespace SS14.Watchdog.Tests.ServerManagement;

public sealed class WatchdogUpdateProtocolTest
{
    [TestCase(null, "secret", false)]
    [TestCase("secret", null, false)]
    [TestCase("", "", false)]
    [TestCase("wrong", "secret", false)]
    [TestCase("secret", "secret", true)]
    public void GameApiRequiresTheExactConfiguredWatchdogToken(string supplied, string expected, bool valid)
        => Assert.That(WatchdogUpdateSchedule.Authenticate(supplied, expected), Is.EqualTo(valid));

    [Test]
    public void RetriedAndCompetingRequestsCannotPostponeTheGameAnnouncement()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new WatchdogUpdateSchedule();
        var request = new WatchdogUpdateRequest("schedule", Guid.NewGuid(), now.AddMinutes(10));
        // Match HttpClient's camel-case request serialization and the game's parser.
        request = JsonSerializer.Deserialize<WatchdogUpdateRequest>(JsonSerializer.Serialize(request, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        Assert.That(schedule.TrySchedule(request, now, out var created), Is.True);
        Assert.That(created, Is.True);
        Assert.That(schedule.TrySchedule(request with { RestartAtUtc = now.AddHours(1) }, now, out created), Is.True);
        Assert.That(created, Is.False);
        Assert.That(schedule.RestartAtUtc, Is.EqualTo(now.AddMinutes(10)));
        Assert.That(schedule.TrySchedule(request with { RequestId = Guid.NewGuid() }, now, out _), Is.False);
        Assert.That(schedule.Cancel(Guid.NewGuid()), Is.False);
        Assert.That(schedule.Cancel(request.RequestId), Is.True);
        Assert.That(schedule.Snapshot().RequestId, Is.Null);
        Assert.That(schedule.Snapshot().RestartAtUtc, Is.Null);
    }

    [Test]
    public void GameRejectsExpiredUnboundedAndUnidentifiedSchedules()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new WatchdogUpdateSchedule();
        Assert.That(schedule.TrySchedule(new("schedule", Guid.NewGuid(), now), now, out _), Is.False);
        Assert.That(schedule.TrySchedule(new("schedule", Guid.NewGuid(), now.AddDays(2)), now, out _), Is.False);
        Assert.That(schedule.TrySchedule(new("schedule", Guid.Empty, now.AddMinutes(10)), now, out _), Is.False);
    }

    [TestCase("2026-10-16T04:49:59Z", true)]
    [TestCase("2026-10-16T04:50:00Z", true)]
    [TestCase("2026-10-16T04:50:01Z", false)]
    [TestCase("2026-10-16T05:00:00Z", false)]
    public void CountdownMustFitBeforeTheMaintenanceWindowCloses(string instant, bool fits)
        => Assert.That(UpdatePolicy.CanFinishInWindow(DateTimeOffset.Parse(instant), TimeSpan.FromMinutes(10),
            new MaintenanceWindowConfiguration { Start = "04:00", End = "05:00" }), Is.EqualTo(fits));

    [TestCase("2026-10-17T01:50:00Z", true)]
    [TestCase("2026-10-17T01:50:01Z", false)]
    public void CountdownFitsAnOvernightWindowOnItsStartDay(string instant, bool fits)
        => Assert.That(UpdatePolicy.CanFinishInWindow(DateTimeOffset.Parse(instant), TimeSpan.FromMinutes(10),
            new MaintenanceWindowConfiguration { Start = "22:00", End = "02:00", Days = [DayOfWeek.Friday] }), Is.EqualTo(fits));

    [TestCase("2026-10-25T00:15:00Z", 15, true)]
    [TestCase("2026-10-25T00:15:00Z", 16, false)]
    [TestCase("2026-10-25T01:15:00Z", 15, true)]
    [TestCase("2026-10-25T01:15:00Z", 16, false)]
    public void RepeatedClosingHourUsesTheNextActualClosingInstant(string instant, int minutes, bool fits)
        => Assert.That(UpdatePolicy.CanFinishInWindow(DateTimeOffset.Parse(instant), TimeSpan.FromMinutes(minutes),
            new MaintenanceWindowConfiguration { TimeZone = "Europe/Berlin", Start = "01:00", End = "02:30" }), Is.EqualTo(fits));

    [Test]
    public void InvalidCountdownConfigurationIsRejected()
    {
        foreach (var seconds in new[] { 0, -1, 86401 })
            Assert.Throws<System.IO.InvalidDataException>(() => OperationsValidation.Validate(new InstanceConfiguration
                { UpdateRestart = new() { CountdownSeconds = seconds } }));
        Assert.Throws<System.IO.InvalidDataException>(() => OperationsValidation.Validate(new InstanceConfiguration
            { UpdateRestart = new() { Mode = (UpdateRestartMode) 99 } }));
        Assert.Throws<System.IO.InvalidDataException>(() => OperationsValidation.Validate(new InstanceConfiguration
            { UpdateRestart = new() { ShutdownTimeoutSeconds = 0 } }));
    }

    [TestCase(15, true)]
    [TestCase(16, false)]
    public void SkippedClosingTimeClosesAtTheDaylightSavingJump(int minutes, bool fits)
        => Assert.That(UpdatePolicy.CanFinishInWindow(DateTimeOffset.Parse("2026-03-29T00:45:00Z"), TimeSpan.FromMinutes(minutes),
            new MaintenanceWindowConfiguration { TimeZone = "Europe/Berlin", Start = "01:00", End = "02:30" }), Is.EqualTo(fits));
}
