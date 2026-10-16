using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SS14.Watchdog.Components.ServerManagement;
using SS14.Watchdog.Components.Updates;
using SS14.Watchdog.Configuration;
using SS14.Watchdog.Utility;

namespace SS14.Watchdog.Tests.ServerManagement;

public sealed class OperationsPolicyTest
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-10-16T00:00:00Z");

    [Test]
    public void SchedulingIsUnrestrictedByDefault()
    {
        var options = new InstanceConfiguration();
        OperationsValidation.Validate(options);
        Assert.That(UpdatePolicy.IsWindowOpen(Epoch, options.MaintenanceWindow), Is.True);
        Assert.That(new UpdatePolicy().ObservePopulation(Epoch, null, options.LowPopulation), Is.True);
    }

    [TestCase("2026-10-16T00:59:59Z", false)]
    [TestCase("2026-10-16T01:00:00Z", true)]
    [TestCase("2026-10-16T04:59:59Z", true)]
    [TestCase("2026-10-16T05:00:00Z", false)]
    [TestCase("2026-10-17T02:00:00Z", false)]
    public void WindowUsesConfiguredTimeZoneDaysAndExclusiveEnd(string instant, bool expected)
    {
        var window = new MaintenanceWindowConfiguration
            { TimeZone = "Europe/Moscow", Start = "04:00", End = "08:00", Days = [DayOfWeek.Friday] };
        Assert.That(UpdatePolicy.IsWindowOpen(DateTimeOffset.Parse(instant), window), Is.EqualTo(expected));
    }

    [TestCase("2026-10-16T21:59:59Z", false)]
    [TestCase("2026-10-16T22:00:00Z", true)]
    [TestCase("2026-10-17T01:59:59Z", true)]
    [TestCase("2026-10-17T02:00:00Z", false)]
    [TestCase("2026-10-17T23:00:00Z", false)]
    public void OvernightWindowBelongsToItsStartDay(string instant, bool expected)
    {
        var window = new MaintenanceWindowConfiguration { Start = "22:00", End = "02:00", Days = [DayOfWeek.Friday] };
        Assert.That(UpdatePolicy.IsWindowOpen(DateTimeOffset.Parse(instant), window), Is.EqualTo(expected));
    }

    [TestCase("2026-10-25T00:30:00Z")]
    [TestCase("2026-10-25T01:30:00Z")]
    public void RepeatedHourDuringDaylightSavingTransitionIsHandled(string instant)
    {
        var window = new MaintenanceWindowConfiguration { TimeZone = "Europe/Berlin", Start = "02:00", End = "03:00" };
        Assert.That(UpdatePolicy.IsWindowOpen(DateTimeOffset.Parse(instant), window), Is.True);
    }

    [Test]
    public void LowPopulationMustRemainLowAndMissingStatusResetsTheObservation()
    {
        var policy = new UpdatePolicy();
        var options = new LowPopulationConfiguration { MaximumPlayers = 5, StableSeconds = 10 };
        Assert.That(policy.ObservePopulation(Epoch, 5, options), Is.False);
        Assert.That(policy.ObservePopulation(Epoch.AddSeconds(9), 0, options), Is.False);
        Assert.That(policy.ObservePopulation(Epoch.AddSeconds(10), 4, options), Is.True);
        Assert.That(policy.ObservePopulation(Epoch.AddSeconds(11), 6, options), Is.False);
        Assert.That(policy.ObservePopulation(Epoch.AddSeconds(20), 1, options), Is.False);
        Assert.That(policy.ObservePopulation(Epoch.AddSeconds(30), null, options), Is.False);
        Assert.That(policy.ObservePopulation(Epoch.AddSeconds(40), 1, options), Is.False);
        Assert.That(policy.ObservePopulation(Epoch.AddSeconds(50), -1, options), Is.False);
    }

    [Test]
    public void CrashPolicyBacksOffAndProbesAgainAfterCooldown()
    {
        var policy = new CrashRestartPolicy();
        var options = new CrashLoopConfiguration();
        var delays = Enumerable.Range(0, 5).Select(i =>
            policy.RecordFailure(Epoch.AddSeconds(i), TimeSpan.Zero, options).Delay.TotalSeconds).ToArray();
        Assert.That(delays, Is.EqualTo(new[] { 5, 15, 30, 60, 600 }));
        Assert.That(policy.RecordFailure(Epoch.AddHours(1), TimeSpan.Zero, options).CoolingDown, Is.True);
        Assert.That(policy.RecordFailure(Epoch.AddHours(2), TimeSpan.FromSeconds(600), options).Delay.TotalSeconds, Is.EqualTo(5));
        policy.Reset();
        Assert.That(policy.RecordFailure(Epoch.AddHours(3), TimeSpan.Zero, options).CoolingDown, Is.False);
        options.Enabled = false;
        Assert.That(policy.RecordFailure(Epoch, TimeSpan.Zero, options).Delay, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void OldFailuresExpireOutsideTheWindow()
    {
        var policy = new CrashRestartPolicy();
        var options = new CrashLoopConfiguration { MaximumFailures = 2, WindowSeconds = 10 };
        policy.RecordFailure(Epoch, TimeSpan.Zero, options);
        Assert.That(policy.RecordFailure(Epoch.AddSeconds(10), TimeSpan.Zero, options).CoolingDown, Is.False);
    }

    [Test]
    public void InvalidConfigurationIsRejectedBeforeStartingAProcess()
    {
        Assert.Throws<InvalidDataException>(() => OperationsValidation.Validate(new InstanceConfiguration { StartupTimeoutSeconds = 0 }));
        Assert.Throws<InvalidDataException>(() => OperationsValidation.Validate(new InstanceConfiguration { StartupTimeoutSeconds = int.MaxValue }));
        Assert.Throws<InvalidDataException>(() => OperationsValidation.Validate(new InstanceConfiguration { MinimumFreeSpaceMiB = -1 }));
        Assert.Throws<InvalidDataException>(() => OperationsValidation.Validate(new InstanceConfiguration { LowPopulation = new() { MaximumPlayers = -1 } }));
        Assert.Throws<InvalidDataException>(() => OperationsValidation.Validate(new InstanceConfiguration { CrashLoop = new() { BackoffSeconds = [-1] } }));
        Assert.Throws<InvalidDataException>(() => OperationsValidation.Validate(new InstanceConfiguration { MaintenanceWindow = new() { Start = "04:00", End = "04:00" } }));
        Assert.Throws<TimeZoneNotFoundException>(() => OperationsValidation.Validate(new InstanceConfiguration { MaintenanceWindow = new() { TimeZone = "No/SuchZone" } }));
    }

    [Test]
    public void DiskBudgetIncludesReserveAndAllowsAnExactFit()
    {
        var root = Path.GetTempPath();
        Assert.DoesNotThrow(() => InstanceStorage.EnsureAvailable(root, 700, 300, _ => 1000));
        Assert.Throws<IOException>(() => InstanceStorage.EnsureAvailable(root, 701, 300, _ => 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => InstanceStorage.EnsureAvailable(root, -1, 0, _ => 1000));
        Assert.Throws<OverflowException>(() => InstanceStorage.EnsureAvailable(root, long.MaxValue, 1, _ => long.MaxValue));
    }

    [Test]
    public void CleanupProtectsActivePreparedAndServerFilesAndRespectsDumpBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SS14.Watchdog.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var stale = Directory.CreateDirectory(Path.Combine(root, ".update-staging-old")).FullName;
            var prepared = Directory.CreateDirectory(Path.Combine(root, ".update-staging-prepared")).FullName;
            var active = Directory.CreateDirectory(Path.Combine(root, ".update-staging-active")).FullName;
            var fresh = Directory.CreateDirectory(Path.Combine(root, ".update-staging-fresh")).FullName;
            var previous = Directory.CreateDirectory(Path.Combine(root, "bin.previous")).FullName;
            foreach (var path in new[] { stale, prepared, active, previous })
                Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
            var oldDownload = Path.Combine(root, ".update-download-old.tmp");
            File.WriteAllText(oldDownload, "old");
            File.SetLastWriteTimeUtc(oldDownload, DateTime.UtcNow.AddDays(-2));
            var dumps = Directory.CreateDirectory(Path.Combine(root, "dumps")).FullName;
            File.WriteAllBytes(Path.Combine(dumps, "dump_old"), new byte[600_000]);
            File.WriteAllBytes(Path.Combine(dumps, "dump_new"), new byte[600_000]);
            File.SetLastWriteTimeUtc(Path.Combine(dumps, "dump_old"), DateTime.UtcNow.AddMinutes(-2));
            using var lease = InstanceStorage.Protect(active);
            Assert.That(InstanceStorage.Cleanup(root, prepared, new CleanupConfiguration { DumpMaximumMiB = 1 }, DateTimeOffset.UtcNow), Is.Empty);
            Assert.That(Directory.Exists(stale), Is.False);
            Assert.That(File.Exists(oldDownload), Is.False);
            Assert.That(new[] { prepared, active, fresh, previous }.All(Directory.Exists), Is.True);
            Assert.That(File.Exists(Path.Combine(dumps, "dump_old")), Is.False);
            Assert.That(File.Exists(Path.Combine(dumps, "dump_new")), Is.True);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void RedactionCoversSecretsUrlsAndNestedExceptionMessages()
    {
        const string secret = "test-sensitive-value-42";
        SensitiveData.Register(secret);
        var exception = new IOException($"password={secret} https://user:pass@example.test/api/webhooks/secret", new Exception(secret));
        var output = SensitiveData.SafeException(exception).ToString();
        Assert.That(output, Does.Not.Contain(secret).And.Not.Contain("user:pass").And.Not.Contain("webhooks/secret"));
        Assert.That(SensitiveData.Redact("token=unknown-token authorization=BasicSecret"), Does.Not.Contain("unknown-token").And.Not.Contain("BasicSecret"));
        Assert.That(SensitiveData.Redact("Authorization: Basic abcdefghijklmnop"), Does.Not.Contain("abcdefghijklmnop"));
        Assert.That(SensitiveData.Redact("Authorization=Bearer abcdefghijklmnop"), Does.Not.Contain("abcdefghijklmnop"));
    }

    [Test]
    public void CleanupNeverFollowsSymbolicLinksOutsideTheInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SS14.Watchdog.Tests-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"SS14.Watchdog.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            var staging = Directory.CreateDirectory(Path.Combine(root, ".update-staging-old")).FullName;
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(staging, "nested-link"), outside);
                Directory.CreateSymbolicLink(Path.Combine(root, ".update-staging-link"), outside);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Assert.Ignore("This host cannot create directory symbolic links.");
            }
            Directory.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddDays(-2));
            InstanceStorage.Cleanup(root, null, new CleanupConfiguration(), DateTimeOffset.UtcNow);
            Assert.That(Directory.Exists(staging), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(outside, "keep.txt")), Is.EqualTo("keep"));
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }
}
