using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SS14.Watchdog.Components.Updates;
using SS14.Watchdog.Utility;

namespace SS14.Watchdog.Components.ServerManagement;

public sealed partial class ServerInstance
{
    private readonly UpdatePolicy _updatePolicy = new();
    private readonly CrashRestartPolicy _crashPolicy = new();
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _restartCancellation;
    private int _restartGeneration;
    private bool _restartPending;
    private bool _expectedServerExit;
    private bool _preparedUpdateIsManual;
    private DateTimeOffset _processStartedAt;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private string? _exitFailureReason;

    private async Task RunPolicyChecksAsync(CancellationToken cancel)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(_instanceConfig.UpdatePolicyPollSeconds), _timeProvider, cancel);
                await _commandQueue.Writer.WriteAsync(new CommandPolicyTick(), cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    private async Task RunPolicyTick(CancellationToken cancel)
    {
        if (IsRunning && _preparedUpdate != null)
            await TrySendPreparedUpdateNotificationAsync(cancel);
        CleanupInstance();
    }

    private void CleanupInstance()
    {
        var now = _timeProvider.GetUtcNow();
        if (now - _lastCleanup < TimeSpan.FromSeconds(_instanceConfig.Cleanup.IntervalSeconds))
            return;
        _lastCleanup = now;
        try
        {
            foreach (var error in InstanceStorage.Cleanup(InstanceDir, _preparedUpdate?.StagingDirectory, _instanceConfig.Cleanup, now))
                _notificationManager.SendError(Key, error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(SensitiveData.SafeException(e), "Cleanup failed for {Key}.", Key);
            _notificationManager.SendError(Key, $"Temporary file cleanup failed: {e.Message}");
        }
    }

    private async Task<bool> UpdateAllowedAsync(CancellationToken cancel)
    {
        if (_preparedUpdateIsManual || _updateNotificationSent)
            return true;
        if (!UpdatePolicy.IsWindowOpen(_timeProvider.GetUtcNow(), _instanceConfig.MaintenanceWindow))
        {
            // There is no player-count observation outside the window. Do not carry an old
            // low count into the next window as if it had remained continuously low.
            _updatePolicy.ObservePopulation(_timeProvider.GetUtcNow(), null, _instanceConfig.LowPopulation);
            return false;
        }
        var population = _instanceConfig.LowPopulation;
        if (population == null)
            return true;
        int? players = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _serverHttpClient.GetAsync($"http://localhost:{_instanceConfig.ApiPort}/status", timeout.Token);
            response.EnsureSuccessStatusCode();
            var status = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("players", out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0)
                players = count;
            else
                throw new InvalidDataException("Server /status response has no valid non-negative players count.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            _notificationManager.SendError(Key, $"Cannot check player count; update is deferred: {e.Message}");
        }
        return _updatePolicy.ObservePopulation(_timeProvider.GetUtcNow(), players, population);
    }

    private void CancelScheduledRestart()
    {
        _restartGeneration++;
        _restartCancellation?.Cancel();
        _restartCancellation?.Dispose();
        _restartCancellation = null;
        _restartPending = false;
    }

    private void ScheduleRestart(RestartDecision decision, CancellationToken cancel)
    {
        CancelScheduledRestart();
        _restartPending = true;
        _restartCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _ = WaitForRestartAsync(decision.Delay, _restartGeneration, _restartCancellation.Token);
    }

    private async Task WaitForRestartAsync(TimeSpan delay, int generation, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancel);
            await _commandQueue.Writer.WriteAsync(new CommandRestartDue(generation), cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    private async Task RunRestartDue(CommandRestartDue command, CancellationToken cancel)
    {
        if (command.Generation != _restartGeneration || _stopped || IsRunning)
            return;
        _restartPending = false;
        await StartServer(cancel);
    }

    private void CancelTimeoutTimer()
    {
        _serverTimeoutNumber++;
        _serverTimeoutTcs?.Cancel();
        _serverTimeoutTcs?.Dispose();
        _serverTimeoutTcs = null;
    }

    private sealed record CommandPolicyTick : Command;
    private sealed record CommandRestartDue(int Generation) : Command;
}
