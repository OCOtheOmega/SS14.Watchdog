using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using SS14.Watchdog.Components.ProcessManagement;
using SS14.Watchdog.Components.Updates;
using SS14.Watchdog.Utility;

namespace SS14.Watchdog.Components.ServerManagement;

public sealed partial class ServerInstance
{
    private const int CommandChannelCapacity = 32;

    // Server instance management uses an actor-ish model:
    // the actual server monitoring, command sending, all that logic is done with a single command queue.

    private readonly Channel<Command> _commandQueue = Channel.CreateBounded<Command>(
        new BoundedChannelOptions(CommandChannelCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private string _baseServerAddress = "";

    /// <summary>
    /// The last time a ping was received from the game server.
    /// </summary>
    /// <remarks>
    /// This is in local time, not UTC.
    /// </remarks>
    private DateTime? _lastPing;

    private CancellationTokenSource? _serverTimeoutTcs;
    private int _serverTimeoutNumber;

    private int _startNumber;
    // Server got an explicit stop command, will not be automatically restarted.
    private bool _stopped;

    public async Task StartAsync(string baseServerAddress, CancellationToken cancel)
    {
        _baseServerAddress = baseServerAddress;
        _instanceCancellation = cancel;

        _logger.LogDebug("Starting server {Key}", Key);

        using var periodicUpdateCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var periodicUpdateTask = _updateProvider != null
            ? RunPeriodicUpdateChecksAsync(periodicUpdateCts.Token)
            : Task.CompletedTask;
        var policyTask = RunPolicyChecksAsync(periodicUpdateCts.Token);

        try
        {
            var recoveredPersistedProcess = await TryLoadPersistedProcess(cancel);
            if (!recoveredPersistedProcess)
            {
                try
                {
                    if (UpdateStaging.RecoverInterruptedUpdate(Path.Combine(InstanceDir, "bin")))
                    {
                        _logger.LogWarning("Recovered the previous server files for {Key} after an interrupted update.", Key);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Could not recover previous server files for {Key}; starting the available files.", Key);
                }
            }

            RestoreCountdown(recoveredPersistedProcess);

            if (recoveredPersistedProcess)
            {
                _logger.LogInformation("Checking for updates after recovering persisted server {Key}.", Key);
                await _commandQueue.Writer.WriteAsync(new CommandCheckForUpdate(), cancel);
            }

            CleanupInstance();

            await _commandQueue.Writer.WriteAsync(new CommandStart(), cancel);

            await CommandLoop(cancel);

            // Currently no way for this to be reachable, as shutdown is always caused by cancellation.
        }
        catch (OperationCanceledException)
        {
            // Nada, expected shutdown sequence.
        }
        catch (Exception e)
        {
            // Oh fuck.
            _logger.LogCritical(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Exception occurred in server instance loop!");
            _notificationManager.SendError(Key, $"Server management loop stopped: {e.Message}");
        }
        finally
        {
            // Whether clean or unclear, run shutdown logic to the best of our abilities.

            await periodicUpdateCts.CancelAsync();
            CancelScheduledRestart();
            StopCountdownTimer();
            CancelTimeoutTimer();
            await ShutdownAsync();
            await periodicUpdateTask;
            await policyTask;
        }
    }

    private async Task RunPeriodicUpdateChecksAsync(CancellationToken cancel)
    {
        try
        {
            if (_instanceConfig.AutoUpdateChecks && _instanceConfig.AutoUpdateCheckIntervalSeconds > 0)
            {
                _logger.LogInformation("Automatic update checks for {Key} are enabled every {IntervalSeconds} seconds.",
                    Key, _instanceConfig.AutoUpdateCheckIntervalSeconds);
            }
            else
            {
                _logger.LogInformation("Automatic update checks for {Key} are disabled.", Key);
            }

            while (!cancel.IsCancellationRequested)
            {
                if (!_instanceConfig.AutoUpdateChecks || _instanceConfig.AutoUpdateCheckIntervalSeconds <= 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancel);
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(_instanceConfig.AutoUpdateCheckIntervalSeconds), cancel);

                _logger.LogTrace("Running periodic update check for {Key}", Key);
                await _commandQueue.Writer.WriteAsync(new CommandCheckForUpdate(), cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Expected during watchdog shutdown.
        }
        catch (Exception e)
        {
            _logger.LogError(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Exception while running periodic update checks for {Key}", Key);
        }
    }

    private async Task<bool> TryLoadPersistedProcess(CancellationToken cancel)
    {
        if (!_processManager.CanPersist)
            return false;

        string? token;
        {
            using var con = _dataManager.OpenConnection();
            token = con.QuerySingle<string?>(
                "SELECT PersistedToken FROM ServerInstance WHERE Key = @Key",
                new { Key });
        }

        if (token == null)
        {
            _logger.LogDebug("No persisted server token, not trying to load persisted process.");
            return false;
        }

        _logger.LogInformation("Trying to load persisted server process...");

        var handle = await _processManager.TryGetPersistedServer(this, GetProgramPath(), cancel);
        if (handle == null)
        {
            _logger.LogInformation("Couldn't load persisted process");
            return false;
        }

        _logger.LogInformation("Loading persisted process: {Process}", handle);
        _runningServer = handle;
        _processStartedAt = _timeProvider.GetUtcNow();

        SetToken(token);

        MonitorServer(++_startNumber, cancel);
        StartTimeoutTimer();
        return true;
    }

    private async Task CommandLoop(CancellationToken cancel)
    {
        _logger.LogDebug("Entering command loop");

        await foreach (var command in _commandQueue.Reader.ReadAllAsync(cancel))
        {
            await RunCommand(command, cancel);
        }
    }

    private async Task RunCommand(Command command, CancellationToken cancel)
    {
        _logger.LogTrace("Running command: {Command}", command);

        switch (command)
        {
            case CommandStart:
                await RunCommandStart(cancel);
                break;
            case CommandServerExit exit:
                await RunCommandServerExit(exit, cancel);
                break;
            case CommandRestart:
                await RunCommandRestart(cancel);
                break;
            case CommandStop stop:
                await RunCommandStop(stop.StopCommand, cancel);
                break;
            case CommandServerPing ping:
                await RunCommandServerPing(ping, cancel);
                break;
            case CommandTimedOut timedOut:
                await RunCommandTimedOut(timedOut, cancel);
                break;
            case CommandUpdatePrepared updatePrepared:
                await RunCommandUpdatePrepared(updatePrepared, cancel);
                break;
            case CommandPolicyTick:
                await RunPolicyTick(cancel);
                break;
            case CommandUpdateCountdown countdown:
                await RunCountdownTick(countdown, cancel);
                break;
            case CommandRestartDue due:
                await RunRestartDue(due, cancel);
                break;
            case CommandCheckForUpdate check:
                await RunCommandCheckForUpdate(check, cancel);
                break;
            case CommandRevert revert:
                await RunCommandRevert(revert, cancel);
                break;
            default:
                throw new InvalidOperationException($"Invalid command: {command}");
        }
    }

    private async Task RunCommandRestart(CancellationToken cancel)
    {
        CancelScheduledRestart();
        _crashPolicy.Reset();
        if (_stopped)
        {
            _logger.LogDebug("Clearing stopped flag due to manual server restart");
            _stopped = false;
        }

        if (_runningServer == null)
        {
            await StartServer(cancel);
            return;
        }

        _expectedServerExit = true;
        if (_countdown is { Confirmed: true } countdown)
        {
            await RequestCountdownShutdown(countdown, cancel);
            return;
        }
        await CancelCountdown(cancel);
        await ForceShutdownServerAsync(cancel);
    }

    private async Task RunCommandStop(ServerInstanceStopCommand stopCommand, CancellationToken cancel)
    {
        // TODO: use stopCommand to indicate more extensive error message to error.

        _stopped = true;
        CancelScheduledRestart();
        await CancelCountdown(cancel);
        if (IsRunning)
        {
            _logger.LogTrace("Server is running, sending fake update notification to make it stop");
            await SendUpdateNotificationAsync(cancel);
        }
    }

    private async Task RunCommandUpdatePrepared(CommandUpdatePrepared command, CancellationToken cancel)
    {
        _updateCheckInProgress = false;
        if (command.Generation != _updateGeneration || _manualVersionOverride || _pendingUpdateHealth != null)
        {
            if (command.Update != null)
                UpdateStaging.Discard(command.Update);

            // An explicit update may have superseded a check that was already downloading.
            if (command.Generation != _updateGeneration)
                await RunCommandCheckForUpdate(new CommandCheckForUpdate(), cancel);
            return;
        }

        if (command.Update == null)
        {
            if (_preparedUpdate == null)
                _updateNotificationSent = false;
            return;
        }

        var update = command.Update;
        if (_countdown != null && _preparedUpdate != null && update.Version != _preparedUpdate.Version)
        {
            UpdateStaging.Discard(update);
            return;
        }
        if (update.Version == _currentRevision || _pendingUpdateHealth?.TargetRevision == update.Version)
        {
            UpdateStaging.Discard(update);
            _logger.LogTrace("Discarding redundant prepared update {Version} for {Key}.", update.Version, Key);
            return;
        }

        if (_blockedUpdateVersions.Contains(update.Version))
        {
            UpdateStaging.Discard(update);
            _logger.LogWarning("Ignoring blocked update {Version} for {Key}; it failed earlier in this Watchdog session.",
                update.Version, Key);
            return;
        }

        if (_preparedUpdate != null)
        {
            if (_preparedUpdate.Version == update.Version)
            {
                UpdateStaging.Discard(update);
                update = _preparedUpdate;
            }
            else
            {
                UpdateStaging.Discard(_preparedUpdate);
                _preparedUpdate = update;
            }
        }
        else
        {
            _preparedUpdate = update;
        }

        _updateOnRestart = true;
        _preparedUpdateIsManual = false;

        if (IsRunning)
        {
            await TrySendPreparedUpdateNotificationAsync(cancel);
        }
        else if (_stopped)
        {
            _logger.LogInformation("Not restarting server for update as it was manually stopped.");
        }
        else if (_restartPending)
        {
            CancelScheduledRestart();
            _crashPolicy.Reset();
            _logger.LogInformation("Starting failed server after update.");
            await StartServer(cancel);
        }
    }

    private async Task RunCommandCheckForUpdate(CommandCheckForUpdate command, CancellationToken cancel)
    {
        if (command.Manual)
        {
            if (_manualVersionOverride)
            {
                if (_preparedUpdate != null)
                    UpdateStaging.Discard(_preparedUpdate);
                _preparedUpdate = null;
                _preparedUpdateIsManual = false;
                _updateNotificationSent = false;
                _updateOnRestart = false;
            }

            _manualVersionOverride = false;
            _updateGeneration++;
            _updateCheckAfterHealth = _pendingUpdateHealth != null;
        }

        // A selected version stays selected until an explicit /update or a Watchdog restart.
        // Do not replace files again while the previous update is still proving healthy.
        if (_pendingUpdateHealth != null)
            return;

        if (_manualVersionOverride && _preparedUpdate == null)
            return;

        if (_preparedUpdate != null)
        {
            if (IsRunning)
                await TrySendPreparedUpdateNotificationAsync(cancel);

            return;
        }

        BeginUpdateCheck();
    }

    private async Task TrySendPreparedUpdateNotificationAsync(CancellationToken cancel)
    {
        if (_preparedUpdate == null || _updateNotificationSent)
            return;

        if (!_preparedUpdateIsManual && _instanceConfig.UpdateRestart.Mode == Configuration.UpdateRestartMode.Countdown)
        {
            await TryStartCountdown(cancel);
            return;
        }

        if (!await UpdateAllowedAsync(cancel))
            return;
        try
        {
            InstanceStorage.EnsureAvailable(InstanceDir, 0, UpdatePreparationOptions.MinimumFreeSpaceBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _notificationManager.SendError(Key, $"Update {_preparedUpdate.Version} is deferred: {e.Message}");
            return;
        }

        _logger.LogInformation("Server {Key} has prepared update {Version}; sending update notification.",
            Key, _preparedUpdate.Version);
        _updateNotificationSent = await SendUpdateNotificationAsync(cancel);
        if (_updateNotificationSent)
            _updateOnRestart = true;
    }

    /// <summary>
    /// Processes the revert command to change the server's version to a specified target version.
    /// Depending on the parameters, the server may be immediately stopped, restarted, or sent an update notification.
    /// </summary>
    /// <param name="command">The revert command containing the target version and whether the change should occur immediately.</param>
    /// <param name="cancel">
    /// An optional <see cref="CancellationToken"/> that can be used to signal the operation should be canceled.
    /// </param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task RunCommandRevert(CommandRevert command, CancellationToken cancel)
    {
        await CancelCountdown(cancel);
        _manualVersionOverride = true;
        _updateCheckAfterHealth = false;
        _updateGeneration++;
        _updateNotificationSent = false;
        if (_preparedUpdate != null)
            UpdateStaging.Discard(_preparedUpdate);
        _preparedUpdate = null;

        if (command.Update.Version == _currentRevision)
        {
            UpdateStaging.Discard(command.Update);
            _updateOnRestart = false;
            return;
        }

        CancelScheduledRestart();
        _crashPolicy.Reset();
        _preparedUpdate = command.Update;
        _preparedUpdateIsManual = true;
        _updateOnRestart = true;

        if (IsRunning)
        {
            if (command.Immediate)
            {
                _expectedServerExit = true;
                await ForceShutdownServerAsync(cancel);
            }
            else
                await TrySendPreparedUpdateNotificationAsync(cancel);
        }
        else if (!_stopped)
        {
            await StartServer(cancel);
        }
    }

    private async Task RunCommandTimedOut(CommandTimedOut timedOut, CancellationToken cancel)
    {
        if (timedOut.TimeoutCounter != _serverTimeoutNumber)
        {
            _logger.LogTrace("Timeout was wrong number, ignoring");

            // Guard against race condition: the timeout could happen just before we can cancel it
            // (due to ping, server shutdown, etc).
            // We use the sequence number to avoid letting it go through in that case.
            return;
        }

        await TimeoutKill();
        return;
    }

    private Task RunCommandServerPing(CommandServerPing ping, CancellationToken cancel)
    {
        if (ping.StartNumber != _startNumber || !IsRunning)
            return Task.CompletedTask;
        _logger.LogTrace("Received ping from server.");
        _lastPing = DateTime.Now;

        CommitHealthyUpdate();

        StartTimeoutTimer();

        return Task.CompletedTask;
    }

    private async Task RunCommandServerExit(CommandServerExit exit, CancellationToken cancel)
    {
        if (_startNumber != exit.StartNumber)
        {
            // Ok so I don't know the current architecture can exactly have this happen, but here goes:
            // Suppose the server hangs. In the timeout handler, we kill it. We then immediately restart it.
            // The exit monitor would see the process exit and raise a command.
            // We obviously don't want this to start the server again,
            // so we use the start number to tell "was this exit for the last server?".
            //
            // Basically, if this if passes, this exit event is for the last server process,
            // and we already started a new one. So ignore this!

            return;
        }

        CancelTimeoutTimer();
        ClearCountdown();
        _runningServer = null;
        if (_pendingUpdateHealth is { } update && update.StartNumber == exit.StartNumber)
        {
            await RollbackFailedUpdateAsync(_exitFailureReason ?? "server exited before its first watchdog ping", cancel);
            return;
        }

        if (_stopped)
        {
            _logger.LogInformation("{Key}: Not restarting server as it was manually stopped.", Key);
            return;
        }

        if (_expectedServerExit || (_updateNotificationSent && exit.ExitStatus.IsClean))
        {
            await StartServer(cancel);
            return;
        }

        var decision = _crashPolicy.RecordFailure(_timeProvider.GetUtcNow(),
            _timeProvider.GetUtcNow() - _processStartedAt, _instanceConfig.CrashLoop);
        var reason = _exitFailureReason ?? $"Unexpected process exit: {exit.ExitStatus}.";
        _notificationManager.SendError(Key, $"{reason} Restart in {decision.Delay.TotalSeconds:0} seconds" +
            (decision.CoolingDown ? " (crash-loop cooldown)." : "."));
        ScheduleRestart(decision, cancel);
    }

    private async Task RunCommandStart(CancellationToken cancel)
    {
        await StartServer(cancel);
    }

    private async Task StartServer(CancellationToken cancel)
    {
        _logger.LogDebug("{Key}: starting server", Key);

        if (_runningServer != null)
        {
            _logger.LogTrace("Start called while already have running server process, ignoring");
            return;
        }

        if (_updateOnRestart)
        {
            _updateOnRestart = false;

            await StartRunUpdate(cancel);
        }

        GenerateNewToken();

        {
            using var con = _dataManager.OpenConnection();
            con.Execute(
                "UPDATE ServerInstance SET PersistedToken = @Secret WHERE Key = @Key",
                new
                {
                    Secret,
                    Key
                });
        }

        _lastPing = null;
        _expectedServerExit = false;
        _exitFailureReason = null;
        _processStartedAt = _timeProvider.GetUtcNow();
        _startNumber++;

        if (_pendingUpdateHealth != null)
            _pendingUpdateHealth = _pendingUpdateHealth with { StartNumber = _startNumber };

        _logger.LogTrace("Getting launch info...");

        var args = new List<string>
        {
            // Watchdog comms config.
            "--cvar", $"watchdog.key={Key}",
            "--cvar", $"watchdog.baseUrl={_baseServerAddress}",
            // watchdog.token provided through ENV vars, as this does not show up in process listings
            // like `ps -aux` or `htop`.

            "--config-file", Path.Combine(InstanceDir, "config.toml"),
            "--data-dir", Path.Combine(InstanceDir, "data"),
        };

        // Prepare the user provided arguments
        foreach (var arg in _instanceConfig.Arguments)
        {
            args.Add(arg);
        }

        var env = new List<(string, string)>
        {
            // __ is replaced by a . when parsing CVars from an environment variable.
            ("ROBUST_CVAR_watchdog__token", $"{Secret}")
        };

        foreach (var (envVar, value) in _instanceConfig.EnvironmentVariables)
        {
            env.Add((envVar, value));
        }

        // Add current build information.
        if (_currentRevision != null && _updateProvider != null)
        {
            foreach (var (cVar, value) in _updateProvider.GetLaunchCVarOverrides(_currentRevision))
            {
                args.Add("--cvar");
                args.Add($"{cVar}={value}");
            }
        }

        var startData = new ProcessStartData(
            GetProgramPath(),
            InstanceDir,
            args,
            env);

        _logger.LogTrace("Launching...");
        try
        {
            _runningServer = await _processManager.StartServer(this, startData, cancel);
            _logger.LogDebug("Launched! {Process}", _runningServer);
        }
        catch (Exception e)
        {
            _logger.LogError(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Exception while launching!");

            if (_pendingUpdateHealth != null)
            {
                await RollbackFailedUpdateAsync($"the updated server process could not be started: {e.Message}", cancel);
                return;
            }
            _exitFailureReason = $"Could not start server: {SensitiveData.Redact(e.Message)}";
            await RunCommandServerExit(new CommandServerExit(_startNumber,
                new ProcessExitStatus(ProcessExitReason.ExitCode, 1)), cancel);
            return;
        }

        MonitorServer(_startNumber, cancel);
        if (_pendingUpdateHealth != null && _instanceConfig.AutoUpdateHealthTimeoutSeconds is <= 0)
            CommitHealthyUpdate();
        StartTimeoutTimer();
    }

    private string GetProgramPath()
    {
        return Path.Combine(InstanceDir, _instanceConfig.RunCommand);
    }

    private async void MonitorServer(int startNumber, CancellationToken cancel = default)
    {
        if (_runningServer == null)
            return;

        _logger.LogDebug("Starting to monitor running server");

        try
        {
            await _runningServer.WaitForExitAsync(cancel);
            var exitStatus = await _runningServer.GetExitStatusAsync();

            _logger.LogInformation("{Key} shut down with status {ExitStatus}", Key, exitStatus);

            Debug.Assert(exitStatus != null);

            await _commandQueue.Writer.WriteAsync(new CommandServerExit(startNumber, exitStatus), cancel);
        }
        catch (OperationCanceledException)
        {
            // Nada.
        }
        catch (Exception e)
        {
            _logger.LogError(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Error while waiting on process exit for {Key}", Key);
        }
    }

    private async Task StartRunUpdate(CancellationToken cancel)
    {
        if (_updateProvider == null)
            return;

        // An in-flight background preparation will notify this process when its files are ready.
        if (_preparedUpdate == null && _updateCheckInProgress)
            return;

        var update = _preparedUpdate;
        _preparedUpdate = null;

        if (update == null && _manualVersionOverride)
            return;

        try
        {
            update ??= await _updateProvider.PrepareUpdateAsync(_currentRevision, UpdatePreparationOptions, cancel);
        }
        catch (Exception e)
        {
            _logger.LogError(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Error while preparing update for {Key}.", Key);
            if (!cancel.IsCancellationRequested)
                _notificationManager.SendError(Key, $"Update preparation failed: {e.Message}");
            return;
        }

        if (update == null)
            return;

        if (!_manualVersionOverride && _blockedUpdateVersions.Contains(update.Version))
        {
            UpdateStaging.Discard(update);
            _logger.LogWarning("Skipping blocked update {Version} for {Key}.", update.Version, Key);
            return;
        }

        if (update.Version == _currentRevision)
        {
            UpdateStaging.Discard(update);
            return;
        }

        if (!_preparedUpdateIsManual && !_updateNotificationSent && File.Exists(GetProgramPath()) &&
            !await UpdateAllowedAsync(cancel))
        {
            _preparedUpdate = update;
            _updateOnRestart = true;
            return;
        }

        try
        {
            AppliedUpdate? appliedUpdate = null;
            InstanceStorage.EnsureAvailable(InstanceDir, 0, UpdatePreparationOptions.MinimumFreeSpaceBytes);
            if (update.StagingDirectory != null)
                appliedUpdate = UpdateStaging.Apply(update, Path.Combine(InstanceDir, "bin"));

            _pendingUpdateHealth = new PendingUpdateHealth(update.Version, _currentRevision, appliedUpdate);
            _currentRevision = update.Version;
            _logger.LogInformation("Applied prepared update {Version} for {Key}; waiting for the first watchdog ping.",
                update.Version, Key);
        }
        catch (Exception e)
        {
            UpdateStaging.Discard(update);
            _blockedUpdateVersions.Add(update.Version);
            _updateNotificationSent = false;
            _preparedUpdateIsManual = false;
            _logger.LogError(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Failed to apply prepared update {Version} for {Key}; the existing server files will be started.",
                update.Version, Key);
            _notificationManager.SendError(Key,
                $"Could not apply update `{update.Version}`: {e.Message}. Starting the existing server version.");
            return;
        }
    }


    /// <summary>
    /// Base class for all commands that are executed by the server instance actor.
    /// </summary>
    private abstract record Command;

    /// <summary>
    /// A server update has been downloaded and validated, and is ready to be applied on restart.
    /// </summary>
    private sealed record CommandUpdatePrepared(PreparedUpdate? Update, int Generation) : Command;

    /// <summary>
    /// Start a provider update check, or retry notification for an update that is already prepared.
    /// </summary>
    private sealed record CommandCheckForUpdate(bool Manual = false) : Command;

    /// <summary>
    /// Command to start the server, if it's not already running.
    /// </summary>
    private sealed record CommandStart : Command;

    /// <summary>
    /// Command to restart the server.
    /// </summary>
    private sealed record CommandRestart : Command;

    /// <summary>
    /// Command to stop the server gracefully, without restarting it afterwards.
    /// </summary>
    private sealed record CommandStop(ServerInstanceStopCommand StopCommand) : Command;

    /// <summary>
    /// Command to revert the server to a specific (already-resolved) version.
    /// </summary>
    private sealed record CommandRevert(PreparedUpdate Update, bool Immediate) : Command;

    /// <summary>
    /// The server has failed to ping back in time, grab the axe!
    /// </summary>
    private sealed record CommandTimedOut(int TimeoutCounter) : Command;

    /// <summary>
    /// The server has exited while being monitored.
    /// </summary>
    private sealed record CommandServerExit(int StartNumber, ProcessExitStatus ExitStatus) : Command;

    /// <summary>
    /// The server has sent us a ping, it's still kicking!
    /// </summary>
    private sealed record CommandServerPing(int StartNumber) : Command;
}
