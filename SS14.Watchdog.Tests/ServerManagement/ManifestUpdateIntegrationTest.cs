#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Content.Server.ServerUpdates;
using SS14.Watchdog.Components.BackgroundTasks;
using SS14.Watchdog.Components.DataManagement;
using SS14.Watchdog.Components.Notifications;
using SS14.Watchdog.Components.ProcessManagement;
using SS14.Watchdog.Components.ServerManagement;
using SS14.Watchdog.Components.Updates;
using SS14.Watchdog.Configuration;
using SS14.Watchdog.Configuration.Updates;

namespace SS14.Watchdog.Tests.ServerManagement;

public sealed class ManifestUpdateIntegrationTest
{
    [Test]
    public async Task UpstreamVersionSelectionAndListingRemainAvailable()
    {
        await using var fixture = await Fixture.Create("6");
        fixture.Downloads.SetVersions("1", "2", "3", "4", "5", "6");
        var versions = await fixture.Instance.GetRecentVersionsAsync();
        var provider = fixture.CreateProvider();

        Assert.That(versions!.Select(v => v.Version), Is.EqualTo(new[] { "6", "5", "4", "3", "2" }));
        Assert.That(await provider.ResolveRevertTargetAsync("6", null), Is.EqualTo("5"));
        Assert.That(await provider.ResolveRevertTargetAsync("2", "6"), Is.EqualTo("6"));
        Assert.That(await provider.ResolveRevertTargetAsync("6", "missing"), Is.Null);
        Assert.That(await provider.ResolveRevertTargetAsync("1", null), Is.Null);
    }

    [Test]
    public async Task DeferredRevertUsesStagingAndStaysSelectedUntilExplicitUpdate()
    {
        await using var fixture = await Fixture.Create("2", recover: true, periodicChecks: true);
        await fixture.Start();
        Assert.That(await fixture.Instance.DoRevertCommandAsync(null, false), Is.EqualTo("1"));
        await WaitUntil(() => fixture.Gateway.UpdateCount == 1);

        Assert.That(fixture.ReadServer(), Is.EqualTo("2"), "Preparing must not change live files.");
        Assert.That(fixture.Instance.CurrentRevision, Is.EqualTo("2"));
        fixture.Processes.Current!.Exit();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("1"));
        Assert.That(fixture.SavedRevision, Is.EqualTo("2"), "A new revision is saved only after its first ping.");
        await fixture.ConfirmHealthy("1");

        var requests = fixture.Downloads.ManifestRequests;
        await Task.Delay(1200);
        Assert.That(fixture.Downloads.ManifestRequests, Is.EqualTo(requests), "Polling must not undo a manual revert.");

        fixture.Instance.HandleUpdateCheck();
        await WaitUntil(() => fixture.Gateway.UpdateCount == 2);
        fixture.Processes.Current!.Exit();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("2"));
        await fixture.ConfirmHealthy("2");
    }

    [Test]
    public async Task ImmediateRevertStopsOnlyAfterTheSelectedArchiveIsPrepared()
    {
        await using var fixture = await Fixture.Create("2", recover: true);
        await fixture.Start();
        Assert.That(await fixture.Instance.DoRevertCommandAsync("1", true), Is.EqualTo("1"));
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("1"));
        Assert.That(fixture.Gateway.ShutdownCount, Is.EqualTo(1));
        Assert.That(fixture.Downloads.DownloadCount("1"), Is.EqualTo(1));
        await fixture.ConfirmHealthy("1");
    }

    [Test]
    public async Task InvalidRevertArchiveLeavesTheRunningServerUntouched()
    {
        await using var fixture = await Fixture.Create("2", recover: true);
        fixture.Downloads.CorruptVersion = "1";
        await fixture.Start();
        var running = fixture.Processes.Current;

        Assert.That(await fixture.Instance.DoRevertCommandAsync("1", true), Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Processes.Current, Is.SameAs(running));
            Assert.That(fixture.ReadServer(), Is.EqualTo("2"));
            Assert.That(fixture.Gateway.ShutdownCount, Is.Zero);
            Assert.That(fixture.Gateway.UpdateCount, Is.Zero);
        });
    }

    [Test]
    public async Task AutomaticUpdateIsCommittedOnlyAfterItsFirstPing()
    {
        await using var fixture = await Fixture.Create("1");
        await fixture.Start();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("2"));
        Assert.That(fixture.SavedRevision, Is.EqualTo("1"));
        Assert.That(Directory.Exists(fixture.BinDirectory + ".previous"), Is.True);

        await fixture.ConfirmHealthy("2");
        Assert.That(Directory.Exists(fixture.BinDirectory + ".previous"), Is.False);
    }

    [Test]
    public async Task InterruptedUpdateIsRecoveredWhenPersistenceIsEnabledButTheProcessIsGone()
    {
        await using var fixture = await Fixture.Create("1");
        fixture.Downloads.SetVersions("1");
        Directory.CreateDirectory(fixture.BinDirectory + ".previous");
        File.WriteAllText(Path.Combine(fixture.BinDirectory + ".previous", ExecutableName), "1");
        File.WriteAllText(Path.Combine(fixture.BinDirectory, ExecutableName), "2");

        await fixture.Start();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("1"));
        Assert.That(fixture.SavedRevision, Is.EqualTo("1"));
        Assert.That(Directory.Exists(fixture.BinDirectory + ".previous"), Is.False);
    }

    [Test]
    public async Task FailedAutomaticUpdateRestoresLocalFilesAndSkipsTheRejectedVersion()
    {
        await using var fixture = await Fixture.Create("1");
        await fixture.Start();
        (await fixture.NextStart()).Exit(1);
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("1"));
        Assert.That(fixture.ReadServer(), Is.EqualTo("1"));
        Assert.That(fixture.SavedRevision, Is.EqualTo("1"));

        fixture.Instance.HandleUpdateCheck();
        await WaitUntil(() => fixture.Downloads.DownloadCount("2") == 2);
        await fixture.DrainBackgroundTasks();
        fixture.Downloads.SetVersions("1", "2", "3");
        fixture.Instance.HandleUpdateCheck();
        await WaitUntil(() => fixture.Gateway.UpdateCount == 1);
        fixture.Processes.Current!.Exit();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("3"));
        Assert.That(fixture.Processes.Attempts.Count(v => v == "2"), Is.EqualTo(1));
        await fixture.ConfirmHealthy("3");
    }

    [Test]
    public async Task MissingFirstPingAutomaticallyRestoresThePreviousBuild()
    {
        await using var fixture = await Fixture.Create("1", healthTimeout: 1);
        await fixture.Start();
        var unhealthy = await fixture.NextStart();
        Assert.That(unhealthy.Version, Is.EqualTo("2"));
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("1"));
        Assert.That(fixture.ReadServer(), Is.EqualTo("1"));
        Assert.That(fixture.SavedRevision, Is.EqualTo("1"));
    }

    [Test]
    public async Task FailedManualRevertUsesTheSameAutomaticRollback()
    {
        await using var fixture = await Fixture.Create("2", recover: true);
        fixture.Processes.FailVersion = "1";
        await fixture.Start();
        Assert.That(await fixture.Instance.DoRevertCommandAsync("1", true), Is.EqualTo("1"));
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("2"));
        Assert.That(fixture.Processes.Attempts, Is.EqualTo(new[] { "1", "2" }));
        Assert.That(fixture.ReadServer(), Is.EqualTo("2"));
        Assert.That(fixture.SavedRevision, Is.EqualTo("2"));
    }

    [Test]
    public async Task RevertOnAManuallyStoppedServerIsAppliedOnManualRestart()
    {
        await using var fixture = await Fixture.Create("2", recover: true);
        await fixture.Start();
        await fixture.Instance.DoStopCommandAsync(new ServerInstanceStopCommand());
        await WaitUntil(() => fixture.Gateway.UpdateCount == 1);
        fixture.Processes.Current!.Exit();
        await WaitUntil(() => !fixture.Instance.IsRunning);

        Assert.That(await fixture.Instance.DoRevertCommandAsync("1", true), Is.EqualTo("1"));
        await fixture.Instance.DoRestartCommandAsync();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("1"));
        await fixture.ConfirmHealthy("1");
    }

    [Test]
    public async Task ManualRevertSupersedesAnAutomaticDownloadAlreadyInProgress()
    {
        await using var fixture = await Fixture.Create("2", recover: true);
        await fixture.Start();
        fixture.Downloads.SetVersions("1", "2", "3");
        fixture.Downloads.BlockVersion = "3";
        fixture.Instance.HandleUpdateCheck();
        await fixture.Downloads.BlockedDownload.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.That(await fixture.Instance.DoRevertCommandAsync("1", false), Is.EqualTo("1"));
            await WaitUntil(() => fixture.Gateway.UpdateCount == 1);
        }
        finally
        {
            fixture.Downloads.ReleaseDownload.TrySetResult();
        }

        await fixture.DrainBackgroundTasks();
        fixture.Processes.Current!.Exit();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("1"));
        await fixture.ConfirmHealthy("1");
        Assert.That(Directory.GetDirectories(fixture.DirectoryPath, ".update-staging-*"), Is.Empty);
    }

    [Test]
    public async Task DeferredManualRevertRetriesAFailedNotificationWithoutDownloadingAgain()
    {
        await using var fixture = await Fixture.Create("2", recover: true, periodicChecks: true);
        fixture.Gateway.RejectFirstUpdate = true;
        await fixture.Start();
        Assert.That(await fixture.Instance.DoRevertCommandAsync("1", false), Is.EqualTo("1"));
        await WaitUntil(() => fixture.Gateway.UpdateCount == 2);
        Assert.That(fixture.Downloads.DownloadCount("1"), Is.EqualTo(1));
    }

    [Test]
    public async Task ExplicitUpdateDuringHealthCheckRunsAfterTheFirstPing()
    {
        await using var fixture = await Fixture.Create("2", recover: true);
        await fixture.Start();
        Assert.That(await fixture.Instance.DoRevertCommandAsync("1", true), Is.EqualTo("1"));
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("1"));

        fixture.Instance.HandleUpdateCheck();
        await fixture.ConfirmHealthy("1");
        await WaitUntil(() => fixture.Gateway.UpdateCount == 1);
        fixture.Processes.Current!.Exit();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("2"));
        await fixture.ConfirmHealthy("2");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, deadline.Token);
    }

    [Test]
    public async Task WindowDefersNotificationButAnAcceptedRoundCanFinishAfterClosing()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-10-16T03:00:00Z"));
        await using var fixture = await Fixture.Create("2", recover: true, configure: o =>
        {
            o.MaintenanceWindow = new() { Start = "04:00", End = "05:00" };
            o.UpdatePolicyPollSeconds = 1;
        }, clock: clock);
        await fixture.Start();
        fixture.Downloads.SetVersions("1", "2", "3");
        fixture.Instance.HandleUpdateCheck();
        await WaitUntil(() => fixture.Downloads.DownloadCount("3") == 1);
        await fixture.DrainBackgroundTasks();
        Assert.That(fixture.Gateway.UpdateCount, Is.Zero);
        clock.Now = clock.Now.AddHours(1);
        await WaitUntil(() => fixture.Gateway.UpdateCount == 1);
        clock.Now = clock.Now.AddHours(2);
        fixture.Processes.Current!.Exit();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("3"));
        await fixture.ConfirmHealthy("3");
    }

    [Test]
    public async Task LowPopulationIsReadFromTheExistingStatusApiAndMustStayLow()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-10-16T03:00:00Z"));
        await using var fixture = await Fixture.Create("2", recover: true, configure: o =>
        {
            o.LowPopulation = new() { MaximumPlayers = 5, StableSeconds = 10 };
            o.UpdatePolicyPollSeconds = 1;
        }, clock: clock);
        fixture.Gateway.Players = 20;
        await fixture.Start();
        fixture.Downloads.SetVersions("1", "2", "3");
        fixture.Instance.HandleUpdateCheck();
        await WaitUntil(() => fixture.Gateway.StatusCount >= 1);
        Assert.That(fixture.Gateway.UpdateCount, Is.Zero);
        fixture.Gateway.Players = 5;
        var observed = fixture.Gateway.StatusCount;
        await WaitUntil(() => fixture.Gateway.StatusCount > observed);
        // Wait for the status response to be consumed before advancing the observation clock.
        await Task.Delay(30);
        clock.Now = clock.Now.AddSeconds(10);
        await WaitUntil(() => fixture.Gateway.UpdateCount == 1);
        fixture.Gateway.Players = 50;
        fixture.Processes.Current!.Exit();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("3"));
        await fixture.ConfirmHealthy("3");
    }

    [Test]
    public async Task ExplicitRevertBypassesAutomaticSchedulingFilters()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-10-16T03:00:00Z"));
        await using var fixture = await Fixture.Create("2", recover: true, configure: o =>
        {
            o.MaintenanceWindow = new() { Start = "04:00", End = "05:00" };
            o.LowPopulation = new() { MaximumPlayers = 0 };
        }, clock: clock);
        fixture.Gateway.Players = 50;
        await fixture.Start();
        Assert.That(await fixture.Instance.DoRevertCommandAsync("1", true), Is.EqualTo("1"));
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("1"));
        await fixture.ConfirmHealthy("1");
    }

    [Test]
    public async Task LowPopulationObservationRestartsWhenTheMaintenanceWindowReopens()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-10-16T04:59:55Z"));
        await using var fixture = await Fixture.Create("2", recover: true, configure: o =>
        {
            o.MaintenanceWindow = new() { Start = "04:00", End = "05:00" };
            o.LowPopulation = new() { MaximumPlayers = 5, StableSeconds = 10 };
            o.UpdatePolicyPollSeconds = 1;
        }, clock: clock);
        await fixture.Start();
        fixture.Downloads.SetVersions("1", "2", "3");
        fixture.Instance.HandleUpdateCheck();
        await WaitUntil(() => fixture.Gateway.StatusCount > 0);
        await Task.Delay(30);
        clock.Now = DateTimeOffset.Parse("2026-10-16T05:00:00Z");
        // Allow a policy tick to observe the closed window (the timers use real time).
        await Task.Delay(1200);
        var observed = fixture.Gateway.StatusCount;
        clock.Now = DateTimeOffset.Parse("2026-10-17T04:00:00Z");
        await WaitUntil(() => fixture.Gateway.StatusCount > observed);
        await Task.Delay(30);
        Assert.That(fixture.Gateway.UpdateCount, Is.Zero, "Yesterday's low count is no longer a continuous observation.");
        clock.Now = clock.Now.AddSeconds(10);
        await WaitUntil(() => fixture.Gateway.UpdateCount == 1);
    }

    [Test]
    public async Task SelectingTheCurrentVersionDoesNotCancelCrashRecovery()
    {
        await using var fixture = await Fixture.Create("2", configure: o =>
        {
            o.CrashLoop.MaximumFailures = 1;
            o.CrashLoop.CooldownSeconds = 1;
        });
        await fixture.Start();
        (await fixture.NextStart()).Exit(1);
        await WaitUntil(() => !fixture.Instance.IsRunning);
        Assert.That(await fixture.Instance.DoRevertCommandAsync("2", true), Is.EqualTo("2"));
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("2"));
    }

    [Test]
    public async Task FailedInstallationDoesNotSuppressTheNextUpdateNotification()
    {
        InstanceConfiguration options = null!;
        await using var fixture = await Fixture.Create("2", recover: true, configure: o => options = o);
        await fixture.Start();
        fixture.Downloads.SetVersions("1", "2", "3");
        fixture.Instance.HandleUpdateCheck();
        await WaitUntil(() => fixture.Gateway.UpdateCount == 1);
        options.MinimumFreeSpaceMiB = int.MaxValue;
        fixture.Processes.Current!.Exit();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("2"));

        options.MinimumFreeSpaceMiB = 0;
        fixture.Downloads.SetVersions("1", "2", "3", "4");
        fixture.Instance.HandleUpdateCheck();
        await WaitUntil(() => fixture.Gateway.UpdateCount == 2);
        fixture.Processes.Current!.Exit();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("4"));
        await fixture.ConfirmHealthy("4");
    }

    [TestCase("{}")]
    [TestCase("{\"players\":\"5\"}")]
    [TestCase("{\"players\":-1}")]
    public async Task InvalidPopulationStatusDefersTheUpdateAndCanRecover(string body)
    {
        await using var fixture = await Fixture.Create("2", recover: true, configure: o =>
        {
            o.LowPopulation = new() { MaximumPlayers = 5, StableSeconds = 0 };
            o.UpdatePolicyPollSeconds = 1;
        });
        fixture.Gateway.StatusBody = body;
        await fixture.Start();
        fixture.Downloads.SetVersions("1", "2", "3");
        fixture.Instance.HandleUpdateCheck();
        await WaitUntil(() => fixture.Gateway.StatusCount > 0);
        Assert.That(fixture.Gateway.UpdateCount, Is.Zero);
        Assert.That(fixture.Instance.IsRunning, Is.True);
        fixture.Gateway.StatusBody = null;
        await WaitUntil(() => fixture.Gateway.UpdateCount == 1);
    }

    [Test]
    public async Task StartupTimeoutDoesNotUseTheShorterRuntimeTimeout()
    {
        await using var fixture = await Fixture.Create("2", configure: o =>
        {
            o.StartupTimeoutSeconds = 4;
            o.TimeoutSeconds = 1;
            o.CrashLoop.BackoffSeconds = [0];
            o.DumpOnTimeout = false;
        });
        await fixture.Start();
        var initial = await fixture.NextStart();
        await Task.Delay(1200);
        Assert.That(fixture.Processes.Attempts.Count, Is.EqualTo(1));
        await fixture.Instance.PingReceived();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("2"));
        Assert.That((await initial.GetExitStatusAsync())!.Status, Is.EqualTo(137));
    }

    [Test]
    public async Task ManualRestartCancelsPendingCrashBackoff()
    {
        await using var fixture = await Fixture.Create("2", configure: o => o.CrashLoop.BackoffSeconds = [1]);
        await fixture.Start();
        (await fixture.NextStart()).Exit(1);
        await WaitUntil(() => !fixture.Instance.IsRunning);
        await fixture.Instance.DoRestartCommandAsync();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("2"));
        await Task.Delay(1200);
        Assert.That(fixture.Processes.Attempts.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task CrashCooldownProbesAgainAndManualStopCancelsTheNextProbe()
    {
        await using var fixture = await Fixture.Create("2", configure: o =>
        {
            o.CrashLoop.MaximumFailures = 2;
            o.CrashLoop.BackoffSeconds = [0];
            o.CrashLoop.CooldownSeconds = 1;
        });
        await fixture.Start();
        (await fixture.NextStart()).Exit(1);
        var second = await fixture.NextStart();
        var start = DateTimeOffset.UtcNow;
        second.Exit(1);
        var probe = await fixture.NextStart();
        Assert.That(DateTimeOffset.UtcNow - start, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(800)));
        probe.Exit(1);
        await WaitUntil(() => !fixture.Instance.IsRunning);
        await fixture.Instance.DoStopCommandAsync(new ServerInstanceStopCommand());
        await Task.Delay(1200);
        Assert.That(fixture.Processes.Attempts.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task InsufficientDiskSpaceKeepsTheExistingServerAndDiscardsStaging()
    {
        await using var fixture = await Fixture.Create("1", configure: o => o.MinimumFreeSpaceMiB = int.MaxValue);
        await fixture.Start();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("1"));
        Assert.That(fixture.ReadServer(), Is.EqualTo("1"));
        Assert.That(Directory.GetFileSystemEntries(fixture.DirectoryPath, ".update-*", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task LaunchFailuresUseCooldownAndManualRestartCanRecover()
    {
        await using var fixture = await Fixture.Create("2", recover: true, configure: o =>
        {
            o.CrashLoop.MaximumFailures = 2;
            o.CrashLoop.BackoffSeconds = [0];
            o.CrashLoop.CooldownSeconds = 1;
        });
        await fixture.Start();
        fixture.Processes.FailVersion = "2";
        fixture.Processes.Current!.Exit(1);
        await WaitUntil(() => fixture.Processes.Attempts.Count == 1);
        await Task.Delay(150);
        Assert.That(fixture.Processes.Attempts.Count, Is.EqualTo(1), "Launch failures must not spin in a tight retry loop.");
        await WaitUntil(() => fixture.Processes.Attempts.Count >= 2);
        fixture.Processes.FailVersion = null;
        await fixture.Instance.DoRestartCommandAsync();
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("2"));
        await Task.Delay(1200);
        Assert.That(fixture.Processes.Attempts.Count, Is.EqualTo(3), "The old cooldown probe must be cancelled.");
    }

    [Test]
    public async Task CountdownWarnsAtTenFiveAndOneMinutesThenUpdatesAnActiveRound()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var fixture = await Fixture.Create("2", recover: true, clock: clock, configure: o =>
        {
            o.UpdateRestart = new();
            o.LowPopulation = new() { MaximumPlayers = 5, StableSeconds = 0 };
        });
        await fixture.Start();
        await fixture.PrepareNextVersion();
        await fixture.CountdownConfirmed();
        Assert.That(fixture.Gateway.AnnouncementCount, Is.EqualTo(1));
        var deadline = fixture.Gateway.Schedule.RestartAtUtc!.Value;
        fixture.Gateway.Players = 100;
        fixture.Downloads.SetVersions("1", "2", "3", "4");
        fixture.Instance.HandleUpdateCheck();
        clock.Now = deadline.AddSeconds(-300);
        await WaitUntil(() => fixture.Gateway.AnnouncementCount == 2);
        clock.Now = deadline.AddSeconds(-60);
        await WaitUntil(() => fixture.Gateway.AnnouncementCount == 3);
        Assert.That(fixture.Processes.Attempts, Is.Empty, "The active round must retain its warning period.");
        clock.Now = deadline;
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("3"));
        Assert.That(fixture.Gateway.CountdownShutdownCount, Is.EqualTo(1));
        Assert.That(fixture.Gateway.UpdateCount, Is.Zero, "Do not show the old, misleading end-of-round message.");
        Assert.That(fixture.Gateway.ShutdownCount, Is.Zero, "Do not bypass the game's save barrier via /shutdown.");
        Assert.That(fixture.Downloads.DownloadCount("4"), Is.Zero);
        await fixture.ConfirmHealthy("3");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingOrInvalidCountdownSupportNeverSilentlyInterruptsTheRound(bool invalidReply)
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var fixture = await Fixture.Create("2", recover: true, clock: clock, configure: o => o.UpdateRestart = new());
        fixture.Gateway.CountdownSupported = invalidReply;
        fixture.Gateway.InvalidCountdownReply = invalidReply;
        await fixture.Start();
        await fixture.PrepareNextVersion();
        clock.Now = clock.Now.AddHours(1);
        await Task.Delay(1200);
        Assert.That(fixture.Instance.IsRunning, Is.True);
        Assert.That(fixture.ReadServer(), Is.EqualTo("2"));
        Assert.That(fixture.Gateway.CountdownShutdownCount, Is.Zero);
        Assert.That(fixture.Gateway.UpdateCount, Is.Zero);
    }

    [Test]
    public async Task LostWarningAcknowledgementRecoversTheOriginalDeadline()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var fixture = await Fixture.Create("2", recover: true, clock: clock, configure: o =>
        {
            o.UpdateRestart = new();
            o.UpdatePolicyPollSeconds = 1;
        });
        fixture.Gateway.LoseScheduleAcknowledgement = true;
        await fixture.Start();
        await fixture.PrepareNextVersion();
        await WaitUntil(() => fixture.Gateway.ScheduleCount == 1);
        var deadline = fixture.Gateway.Schedule.RestartAtUtc!.Value;
        clock.Now = clock.Now.AddMinutes(2);
        await fixture.CountdownConfirmed();
        Assert.That(fixture.Gateway.Schedule.RestartAtUtc, Is.EqualTo(deadline));
        Assert.That(fixture.Gateway.ScheduleCount, Is.EqualTo(1));
        Assert.That(fixture.Gateway.AnnouncementCount, Is.EqualTo(1));
        clock.Now = deadline;
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("3"));
    }

    [Test]
    public async Task RestartingWatchdogPreservesTheDeadlineVersionAndPreparedFiles()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var fixture = await Fixture.Create("2", recover: true, clock: clock, configure: o => o.UpdateRestart = new());
        await fixture.Start();
        await fixture.PrepareNextVersion();
        await fixture.CountdownConfirmed();
        var deadline = fixture.Gateway.Schedule.RestartAtUtc;
        var requestId = fixture.Gateway.Schedule.RequestId;
        clock.Now = clock.Now.AddMinutes(2);
        await fixture.RestartWatchdog();
        await fixture.CountdownConfirmed();
        Assert.That(fixture.Gateway.Schedule.RestartAtUtc, Is.EqualTo(deadline));
        Assert.That(fixture.Gateway.Schedule.RequestId, Is.EqualTo(requestId));
        Assert.That(fixture.Downloads.DownloadCount("3"), Is.EqualTo(1));
        clock.Now = deadline!.Value;
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("3"));
        await fixture.ConfirmHealthy("3");
    }

    [Test]
    public async Task ManualStopCancelsTheCountdownAndItsQueuedTimer()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var fixture = await Fixture.Create("2", recover: true, clock: clock, configure: o => o.UpdateRestart = new());
        await fixture.Start();
        await fixture.PrepareNextVersion();
        await fixture.CountdownConfirmed();
        await fixture.Instance.DoStopCommandAsync(new());
        await WaitUntil(() => fixture.Gateway.CancelCount == 1);
        clock.Now = clock.Now.AddHours(1);
        await Task.Delay(1200);
        Assert.That(fixture.Gateway.CountdownShutdownCount, Is.Zero);
        fixture.Processes.Current!.Exit();
        await WaitUntil(() => !fixture.Instance.IsRunning);
        Assert.That(fixture.Processes.Attempts, Is.Empty);
    }

    [Test]
    public async Task CountdownWaitsForGracefulSavingBeforeKillingAHungProcess()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var fixture = await Fixture.Create("2", recover: true, clock: clock, configure: o =>
            o.UpdateRestart = new() { ShutdownTimeoutSeconds = 30 });
        fixture.Gateway.DelayCountdownShutdown = true;
        await fixture.Start();
        var original = fixture.Processes.Current!;
        await fixture.PrepareNextVersion();
        await fixture.CountdownConfirmed();
        clock.Now = fixture.Gateway.Schedule.RestartAtUtc!.Value;
        await WaitUntil(() => fixture.Gateway.CountdownShutdownCount == 1);
        clock.Now = clock.Now.AddSeconds(29);
        await Task.Delay(1200);
        Assert.That(await original.GetExitStatusAsync(), Is.Null, "The save barrier must get the full shutdown grace period.");
        clock.Now = clock.Now.AddSeconds(1);
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("3"));
        Assert.That((await original.GetExitStatusAsync())!.Status, Is.EqualTo(137));
        await fixture.ConfirmHealthy("3");
    }

    [Test]
    public async Task FailedCountdownUpdateRollsBackWithoutAStaleTimerKillingTheFallback()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var fixture = await Fixture.Create("2", recover: true, clock: clock, configure: o => o.UpdateRestart = new());
        fixture.Processes.FailVersion = "3";
        await fixture.Start();
        await fixture.PrepareNextVersion();
        await fixture.CountdownConfirmed();
        clock.Now = fixture.Gateway.Schedule.RestartAtUtc!.Value;
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("2"));
        clock.Now = clock.Now.AddHours(1);
        await Task.Delay(1200);
        Assert.That(await fixture.Processes.Current!.GetExitStatusAsync(), Is.Null);
        Assert.That(fixture.ReadServer(), Is.EqualTo("2"));
        Assert.That(fixture.SavedRevision, Is.EqualTo("2"));
    }

    [Test]
    public async Task DiskReserveIsCheckedAgainBeforeInterruptingTheRound()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        InstanceConfiguration options = null!;
        await using var fixture = await Fixture.Create("2", recover: true, clock: clock, configure: o =>
        {
            options = o;
            o.UpdateRestart = new();
        });
        await fixture.Start();
        await fixture.PrepareNextVersion();
        await fixture.CountdownConfirmed();
        options.MinimumFreeSpaceMiB = int.MaxValue;
        clock.Now = fixture.Gateway.Schedule.RestartAtUtc!.Value;
        await WaitUntil(() => fixture.Gateway.CancelCount == 1);
        Assert.That(fixture.Gateway.CountdownShutdownCount, Is.Zero);
        Assert.That(fixture.ReadServer(), Is.EqualTo("2"));
        Assert.That(fixture.Instance.IsRunning, Is.True);
    }

    [Test]
    public async Task CountdownPersistenceToleratesATransientWindowsReaderLock()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var fixture = await Fixture.Create("2", recover: true, clock: clock, configure: o => o.UpdateRestart = new());
        Task releaseReader = Task.CompletedTask;
        fixture.Gateway.BeforeScheduleAcknowledgement = () =>
        {
            var reader = new FileStream(Path.Combine(fixture.DirectoryPath, "test", ".watchdog-update-countdown.json"),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            releaseReader = Task.Run(async () => { await Task.Delay(150); reader.Dispose(); });
        };
        await fixture.Start();
        await fixture.PrepareNextVersion();
        await fixture.CountdownConfirmed();
        await releaseReader;
        clock.Now = fixture.Gateway.Schedule.RestartAtUtc!.Value;
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("3"));
    }

    [Test]
    public async Task CountdownWaitsForAWindowThatFitsTheFullWarningPeriod()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-10-16T04:55:00Z"));
        await using var fixture = await Fixture.Create("2", recover: true, clock: clock, configure: o =>
        {
            o.UpdateRestart = new();
            o.MaintenanceWindow = new() { Start = "04:00", End = "05:00" };
        });
        await fixture.Start();
        await fixture.PrepareNextVersion();
        await WaitUntil(() => fixture.Gateway.CountdownStatusCount > 0);
        await Task.Delay(30);
        Assert.That(fixture.Gateway.ScheduleCount, Is.Zero);
        clock.Now = DateTimeOffset.Parse("2026-10-17T04:00:00Z");
        fixture.Instance.HandleUpdateCheck();
        await fixture.CountdownConfirmed();
        clock.Now = fixture.Gateway.Schedule.RestartAtUtc!.Value;
        Assert.That((await fixture.NextStart()).Version, Is.EqualTo("3"));
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "Robust.Server.exe" : "Robust.Server";

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"SS14.Watchdog.Tests-{Guid.NewGuid():N}");
        public string BinDirectory => Path.Combine(DirectoryPath, "test", "bin");
        public ManifestHandler Downloads { get; } = new();
        public FakeProcesses Processes { get; }
        public ServerHandler Gateway { get; }
        public ServerInstance Instance { get; private set; } = null!;
        private CancellationTokenSource _cancel = new();
        private readonly BackgroundTaskQueue _queue = new(NullLogger<BackgroundTaskQueue>.Instance);
        private readonly HttpClient _downloadClient;
        private readonly HttpClient _serverClient;
        private DataManager _data = null!;
        private ServiceProvider _services = null!;
        private Task? _instanceTask;
        private InstanceConfiguration _options = null!;
        private IConfiguration _configuration = null!;

        private Fixture(string revision, bool recover)
        {
            Processes = new FakeProcesses(recover ? revision : null);
            Gateway = new ServerHandler(Processes);
            Processes.OnStart = () => Gateway.Schedule = new();
            _downloadClient = new HttpClient(Downloads);
            _serverClient = new HttpClient(Gateway);
        }

        public static async Task<Fixture> Create(string revision, bool recover = false, bool periodicChecks = false,
            int healthTimeout = 120, Action<InstanceConfiguration>? configure = null, TimeProvider? clock = null)
        {
            var fixture = new Fixture(revision, recover);
            Directory.CreateDirectory(fixture.BinDirectory);
            File.WriteAllText(Path.Combine(fixture.BinDirectory, ExecutableName), revision);
            fixture._data = new DataManager(NullLogger<DataManager>.Instance,
                Options.Create(new DataOptions { File = Path.Combine(fixture.DirectoryPath, "watchdog.db") }));
            await ((IHostedService) fixture._data).StartAsync(CancellationToken.None);
            using (var connection = fixture._data.OpenConnection())
            {
                connection.Execute("INSERT INTO ServerInstance(Key, Revision, PersistedToken) VALUES ('test', @revision, 'token')",
                    new { revision });
            }

            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(fixture._downloadClient);
            fixture._services = new ServiceCollection()
                .AddSingleton<IHttpClientFactory>(factory.Object)
                .AddSingleton<ILogger<UpdateProviderManifest>>(NullLogger<UpdateProviderManifest>.Instance)
                .AddSingleton(clock ?? TimeProvider.System)
                .BuildServiceProvider();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Servers:Instances:test:Updates:ManifestUrl"] = "https://updates.test/manifest"
            }).Build();
            var instanceOptions = new InstanceConfiguration
                {
                    UpdateType = "Manifest", ApiPort = 1212, TimeoutSeconds = 3600,
                    AutoUpdateChecks = periodicChecks, AutoUpdateCheckIntervalSeconds = 1,
                    UpdateRestart = new() { Mode = UpdateRestartMode.AfterRound },
                    AutoUpdateHealthTimeoutSeconds = healthTimeout
                };
            configure?.Invoke(instanceOptions);
            fixture.Gateway.Clock = clock ?? TimeProvider.System;
            fixture._options = instanceOptions;
            fixture._configuration = configuration;
            fixture.Instance = new ServerInstance("test", instanceOptions,
                configuration, new ServersConfiguration { InstanceRoot = fixture.DirectoryPath },
                new TestLogger<ServerInstance>(), fixture._queue, fixture._services, fixture._data,
                fixture.Processes, new NotificationManager(factory.Object, NullLogger<NotificationManager>.Instance,
                    Options.Create(new NotificationOptions())), fixture._serverClient);
            return fixture;
        }

        public async Task RestartWatchdog()
        {
            await _cancel.CancelAsync();
            await _instanceTask!.WaitAsync(TimeSpan.FromSeconds(5));
            _cancel.Dispose();
            _cancel = new();
            var factory = _services.GetRequiredService<IHttpClientFactory>();
            Instance = new ServerInstance("test", _options, _configuration,
                new ServersConfiguration { InstanceRoot = DirectoryPath }, new TestLogger<ServerInstance>(),
                _queue, _services, _data, Processes, new NotificationManager(factory,
                    NullLogger<NotificationManager>.Instance, Options.Create(new NotificationOptions())), _serverClient);
            _instanceTask = Instance.StartAsync("http://127.0.0.1:5000", _cancel.Token);
            await WaitUntil(() => Instance.IsRunning || _instanceTask.IsCompleted);
            Assert.That(_instanceTask.IsCompleted, Is.False);
            await DrainBackgroundTasks();
        }

        public Task CountdownConfirmed() => WaitUntil(() =>
        {
            try
            {
                using var stream = new FileStream(Path.Combine(DirectoryPath, "test", ".watchdog-update-countdown.json"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var json = JsonDocument.Parse(stream);
                return json.RootElement.GetProperty("Confirmed").GetBoolean();
            }
            catch (IOException) { return false; }
        });

        public async Task PrepareNextVersion()
        {
            Downloads.SetVersions("1", "2", "3");
            Instance.HandleUpdateCheck();
            await WaitUntil(() => Downloads.DownloadCount("3") > 0);
            await DrainBackgroundTasks();
        }

        public async Task Start()
        {
            await _queue.StartAsync(CancellationToken.None);
            _instanceTask = Instance.StartAsync("http://127.0.0.1:5000", _cancel.Token);
            await WaitUntil(() => Instance.IsRunning || _instanceTask.IsCompleted);
            Assert.That(_instanceTask.IsCompleted, Is.False, "Instance loop stopped unexpectedly.");
            await DrainBackgroundTasks();
        }

        public Task<FakeProcess> NextStart() => Processes.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public string ReadServer() => File.ReadAllText(Path.Combine(BinDirectory, ExecutableName));

        public string SavedRevision
        {
            get
            {
                using var connection = _data.OpenConnection();
                return connection.QuerySingle<string>("SELECT Revision FROM ServerInstance WHERE Key = 'test'");
            }
        }

        public async Task ConfirmHealthy(string revision)
        {
            await Instance.PingReceived();
            await WaitUntil(() => SavedRevision == revision && !Directory.Exists(BinDirectory + ".previous"));
        }

        public async Task DrainBackgroundTasks()
        {
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.QueueTask(_ => { drained.SetResult(); return Task.CompletedTask; });
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public UpdateProviderManifest CreateProvider() => new(
            new UpdateProviderManifestConfiguration { ManifestUrl = "https://updates.test/manifest" },
            NullLogger<UpdateProviderManifest>.Instance, _downloadClient);

        public async ValueTask DisposeAsync()
        {
            Downloads.ReleaseDownload.TrySetResult();
            await _cancel.CancelAsync();
            if (_instanceTask != null)
                await _instanceTask.WaitAsync(TimeSpan.FromSeconds(5));
            await _queue.StopAsync(CancellationToken.None);
            _queue.Dispose();
            _cancel.Dispose();
            _serverClient.Dispose();
            _downloadClient.Dispose();
            _services.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(DirectoryPath, true);
        }
    }

    private sealed class FakeProcesses(string? recoveredVersion) : IProcessManager
    {
        public bool CanPersist => true;
        public FakeProcess? Current { get; private set; } = recoveredVersion == null ? null : new FakeProcess(recoveredVersion);
        public string? FailVersion { get; set; }
        public Channel<FakeProcess> Starts { get; } = Channel.CreateUnbounded<FakeProcess>();
        public ConcurrentQueue<string> Attempts { get; } = new();
        public Action? OnStart;

        public Task<IProcessHandle> StartServer(IServerInstance instance, ProcessStartData data, CancellationToken cancel)
        {
            var version = File.ReadAllText(data.Program);
            Attempts.Enqueue(version);
            if (version == FailVersion)
                throw new IOException("Simulated server launch failure.");
            Current = new FakeProcess(version);
            OnStart?.Invoke();
            Starts.Writer.TryWrite(Current);
            return Task.FromResult<IProcessHandle>(Current);
        }

        public Task<IProcessHandle?> TryGetPersistedServer(IServerInstance instance, string program, CancellationToken cancel)
            => Task.FromResult<IProcessHandle?>(Current);
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                TestContext.WriteLine(formatter(state, exception) + (exception == null ? "" : "\n" + exception));
        }
    }

    private sealed class FakeProcess(string version) : IProcessHandle
    {
        public string Version { get; } = version;
        private readonly TaskCompletionSource<ProcessExitStatus> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Exit(int code = 0) => _exit.TrySetResult(new ProcessExitStatus(ProcessExitReason.ExitCode, code));
        public void DumpProcess(string file, Microsoft.Diagnostics.NETCore.Client.DumpType type) { }
        public Task WaitForExitAsync(CancellationToken cancel = default) => _exit.Task.WaitAsync(cancel);
        public Task Kill() { Exit(137); return Task.CompletedTask; }
        public Task<ProcessExitStatus?> GetExitStatusAsync()
            => Task.FromResult(_exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : null);
    }

    private sealed class ServerHandler(FakeProcesses processes) : HttpMessageHandler
    {
        public int UpdateCount;
        public int ShutdownCount;
        public int StatusCount;
        public int Players;
        public string? StatusBody;
        public bool RejectFirstUpdate;
        public TimeProvider Clock = TimeProvider.System;
        public WatchdogUpdateSchedule Schedule = new();
        public bool CountdownSupported = true;
        public bool InvalidCountdownReply;
        public bool LoseScheduleAcknowledgement;
        public bool DelayCountdownShutdown;
        public int ScheduleCount;
        public int AnnouncementCount;
        public int CountdownShutdownCount;
        public int CancelCount;
        public int CountdownStatusCount;
        public Action? BeforeScheduleAcknowledgement;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = HttpStatusCode.OK;
            if (request.RequestUri!.AbsolutePath == "/watchdog/update")
            {
                if (request.Method == HttpMethod.Get) Interlocked.Increment(ref CountdownStatusCount);
                if (!CountdownSupported)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (InvalidCountdownReply)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Success") };
                var reply = Schedule.Snapshot();
                if (request.Method == HttpMethod.Post)
                {
                    var data = (await request.Content!.ReadFromJsonAsync<WatchdogUpdateRequest>(cancellationToken))!;
                    switch (data.Action)
                    {
                        case "schedule":
                            if (!Schedule.TrySchedule(data, Clock.GetUtcNow(), out var created))
                                return new HttpResponseMessage(HttpStatusCode.Conflict);
                            if (created) { Interlocked.Increment(ref ScheduleCount); Interlocked.Increment(ref AnnouncementCount); }
                            BeforeScheduleAcknowledgement?.Invoke();
                            reply = Schedule.Snapshot();
                            if (LoseScheduleAcknowledgement)
                            {
                                LoseScheduleAcknowledgement = false;
                                throw new HttpRequestException("Simulated connection loss after displaying the warning.");
                            }
                            break;
                        case "announce":
                            Assert.That(data.RequestId, Is.EqualTo(Schedule.RequestId));
                            Interlocked.Increment(ref AnnouncementCount);
                            break;
                        case "shutdown":
                            Assert.That(data.RequestId, Is.EqualTo(Schedule.RequestId));
                            Interlocked.Increment(ref CountdownShutdownCount);
                            if (!DelayCountdownShutdown) processes.Current!.Exit();
                            break;
                        case "cancel":
                            reply = new WatchdogUpdateResponse(1, data.RequestId, Schedule.RestartAtUtc ?? Clock.GetUtcNow());
                            Assert.That(Schedule.Cancel(data.RequestId), Is.True);
                            Interlocked.Increment(ref CancelCount);
                            break;
                        default: throw new AssertionException("Unexpected countdown action.");
                    }
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply) };
            }
            if (request.RequestUri!.AbsolutePath == "/status")
            {
                Interlocked.Increment(ref StatusCount);
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(StatusBody ?? JsonSerializer.Serialize(new { players = Players })) };
            }
            if (request.RequestUri.AbsolutePath == "/shutdown")
            {
                Interlocked.Increment(ref ShutdownCount);
                processes.Current!.Exit();
            }
            else if (Interlocked.Increment(ref UpdateCount) == 1 && RejectFirstUpdate)
                status = HttpStatusCode.ServiceUnavailable;
            return new HttpResponseMessage(status);
        }
    }

    private sealed class ManifestHandler : HttpMessageHandler
    {
        private string[] _versions = ["1", "2"];
        private readonly ConcurrentDictionary<string, byte[]> _archives = new();
        private readonly ConcurrentDictionary<string, int> _downloads = new();
        public int ManifestRequests;
        public string? CorruptVersion;
        public string? BlockVersion;
        public TaskCompletionSource BlockedDownload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDownload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SetVersions(params string[] versions) => Volatile.Write(ref _versions, versions);
        public int DownloadCount(string version) => _downloads.GetValueOrDefault(version);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/manifest")
            {
                Interlocked.Increment(ref ManifestRequests);
                var rid = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
                          + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
                var builds = Volatile.Read(ref _versions).ToDictionary(version => version, version => new
                {
                    time = new DateTimeOffset(2026, 1, int.Parse(version), 0, 0, 0, TimeSpan.Zero),
                    client = new { url = "", sha256 = "" },
                    server = new Dictionary<string, object>
                    {
                        [rid] = new { url = $"https://updates.test/{version}.zip", sha256 =
                            Convert.ToHexString(SHA256.HashData(_archives.GetOrAdd(version, CreateArchive))) }
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(JsonSerializer.Serialize(new { builds })) };
            }

            var selected = Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath);
            _downloads.AddOrUpdate(selected, 1, (_, count) => count + 1);
            if (selected == BlockVersion)
            {
                BlockedDownload.TrySetResult();
                await ReleaseDownload.Task.WaitAsync(cancellationToken);
            }

            var bytes = selected == CorruptVersion ? new byte[] { 0 } : _archives.GetOrAdd(selected, CreateArchive);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }

        private static byte[] CreateArchive(string version)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry(ExecutableName).Open());
                writer.Write(version);
            }
            return stream.ToArray();
        }
    }
}
