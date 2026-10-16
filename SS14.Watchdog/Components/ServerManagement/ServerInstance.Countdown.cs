using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SS14.Watchdog.Components.Updates;
using SS14.Watchdog.Utility;

namespace SS14.Watchdog.Components.ServerManagement;

public sealed partial class ServerInstance
{
    private CountdownState? _countdown;
    private CancellationTokenSource? _countdownCancellation;
    private int _countdownGeneration;
    private DateTimeOffset? _countdownShutdownDeadline;
    private DateTimeOffset _nextReminderAttempt;
    private string CountdownPath => Path.Combine(InstanceDir, ".watchdog-update-countdown.json");

    private sealed record CountdownState(Guid RequestId, PreparedUpdate Update, string ProcessFingerprint,
        DateTimeOffset RestartAtUtc, bool Confirmed = false, int LastNoticeSeconds = int.MaxValue);

    private sealed record CountdownRequest(string Action, Guid RequestId, DateTimeOffset? RestartAtUtc = null);
    private sealed record CountdownResponse(int ProtocolVersion, Guid? RequestId, DateTimeOffset? RestartAtUtc);
    private sealed record CommandUpdateCountdown(int Generation) : Command;

    private string ProcessFingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Secret ?? "")));

    private async Task SaveCountdown(CountdownState state)
    {
        var temporary = CountdownPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(flushToDisk: true);
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, CountdownPath, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < 5 && e is IOException or UnauthorizedAccessException)
            {
                // Windows scanners/readers can briefly hold the old record across its atomic replacement.
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (1 << attempt)), _instanceCancellation);
            }
        }
    }

    private void RestoreCountdown(bool recoveredProcess)
    {
        if (!File.Exists(CountdownPath))
            return;
        try
        {
            if (!recoveredProcess)
            {
                File.Delete(CountdownPath);
                return;
            }
            using var stream = File.OpenRead(CountdownPath);
            if (stream.Length > 65536)
                throw new InvalidDataException("Saved update countdown is too large.");
            var state = JsonSerializer.Deserialize<CountdownState>(stream)
                ?? throw new InvalidDataException("Saved update countdown is empty.");
            if (state.Update == null || state.RestartAtUtc == default || state.LastNoticeSeconds < 0)
                throw new InvalidDataException("Saved update countdown is incomplete.");
            if (state.ProcessFingerprint != ProcessFingerprint() || state.Update.Version == _currentRevision)
            {
                stream.Dispose();
                File.Delete(CountdownPath);
                return;
            }
            if (state.RequestId == Guid.Empty || string.IsNullOrEmpty(state.Update.Version))
                throw new InvalidDataException("Saved update countdown has no request ID or version.");
            if (state.Update.StagingDirectory is { } staging)
            {
                var full = Path.GetFullPath(staging);
                if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(InstanceDir),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    !Path.GetFileName(full).StartsWith(".update-staging-", StringComparison.Ordinal) ||
                    !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 ||
                    !File.Exists(Path.Combine(full, Path.GetFileName(GetProgramPath()))))
                    throw new InvalidDataException("Saved countdown does not reference a valid owned staging directory.");
            }
            _countdown = state;
            _preparedUpdate = state.Update;
            _updateOnRestart = true;
            _updateNotificationSent = state.Confirmed;
            if (state.Confirmed)
                StartCountdownTimer();
            _logger.LogInformation("Restored update countdown for {Key} to {Version}, deadline {Deadline}.",
                Key, state.Update.Version, state.RestartAtUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _logger.LogError(SensitiveData.SafeException(e), "Cannot restore update countdown for {Key}.", Key);
            _notificationManager.SendError(Key, $"Cannot restore update countdown: {e.Message}. No timed shutdown will be attempted.");
        }
    }

    private void StopCountdownTimer()
    {
        _countdownGeneration++;
        _countdownCancellation?.Cancel();
        _countdownCancellation?.Dispose();
        _countdownCancellation = null;
    }

    private void ClearCountdown()
    {
        StopCountdownTimer();
        _countdown = null;
        _countdownShutdownDeadline = null;
        _nextReminderAttempt = default;
        try { File.Delete(CountdownPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(SensitiveData.SafeException(e), "Cannot remove saved countdown for {Key}.", Key);
            _notificationManager.SendError(Key, $"Cannot remove saved update countdown: {e.Message}");
        }
    }

    private void StartCountdownTimer()
    {
        StopCountdownTimer();
        _countdownCancellation = CancellationTokenSource.CreateLinkedTokenSource(_instanceCancellation);
        _ = QueueCountdownTicks(_countdownGeneration, _countdownCancellation.Token);
    }

    private async Task QueueCountdownTicks(int generation, CancellationToken cancel)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, cancel);
                await _commandQueue.Writer.WriteAsync(new CommandUpdateCountdown(generation), cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    private async Task<CountdownResponse> CallCountdownApi(CountdownRequest? request, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var address = $"http://localhost:{_instanceConfig.ApiPort}/watchdog/update";
        using var response = request == null
            ? await _serverHttpClient.GetAsync(address, timeout.Token)
            : await _serverHttpClient.PostAsJsonAsync(address, request, timeout.Token);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented)
            throw new InvalidDataException("Game countdown API is unavailable. Deploy the HeretekRT integration or select UpdateRestart.Mode: AfterRound.");
        response.EnsureSuccessStatusCode();
        CountdownResponse? result;
        try { result = await response.Content.ReadFromJsonAsync<CountdownResponse>(timeout.Token); }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Game returned an invalid countdown acknowledgement. Deploy the HeretekRT integration or select UpdateRestart.Mode: AfterRound.", e);
        }
        if (result == null || result.ProtocolVersion != 1 ||
            (result.RequestId == null) != (result.RestartAtUtc == null) || result.RequestId == Guid.Empty ||
            (request != null && result.RequestId != request.RequestId))
            throw new InvalidDataException("Game did not acknowledge the Watchdog countdown protocol (version 1). Install the game integration or select UpdateRestart.Mode: AfterRound.");
        return result;
    }

    private async Task<bool> TryStartCountdown(CancellationToken cancel)
    {
        try
        {
            // Query first: an HTTP timeout may have lost the acknowledgement after the game
            // already displayed the initial warning. Recover that exact deadline without resetting it.
            var status = await CallCountdownApi(null, cancel);
            if (_countdown is { } pending && status.RequestId == pending.RequestId)
            {
                await ConfirmCountdown(pending with { RestartAtUtc = status.RestartAtUtc!.Value });
                return true;
            }
            if (status.RequestId != null)
                throw new InvalidDataException("The game has a different pending update countdown; refusing to replace its announced deadline.");
            if (_countdown != null)
                ClearCountdown();
            if (!await UpdateAllowedAsync(cancel) || !UpdatePolicy.CanFinishInWindow(_timeProvider.GetUtcNow(),
                    TimeSpan.FromSeconds(_instanceConfig.UpdateRestart.CountdownSeconds), _instanceConfig.MaintenanceWindow))
                return false;
            InstanceStorage.EnsureAvailable(InstanceDir, 0, UpdatePreparationOptions.MinimumFreeSpaceBytes);
            var state = new CountdownState(Guid.NewGuid(), _preparedUpdate!, ProcessFingerprint(),
                _timeProvider.GetUtcNow().AddSeconds(_instanceConfig.UpdateRestart.CountdownSeconds));
            await SaveCountdown(state);
            _countdown = state;
            var reply = await CallCountdownApi(new CountdownRequest("schedule", state.RequestId, state.RestartAtUtc), cancel);
            if (reply.RestartAtUtc != state.RestartAtUtc)
                throw new InvalidDataException("Game acknowledged a different update deadline.");
            await ConfirmCountdown(state);
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or JsonException)
        {
            _logger.LogWarning(SensitiveData.SafeException(e), "Update countdown could not be confirmed for {Key}.", Key);
            _notificationManager.SendError(Key, $"Update countdown is deferred: {e.Message}. The running server will not be interrupted without a confirmed warning.");
            return false;
        }
    }

    private async Task ConfirmCountdown(CountdownState state)
    {
        state = state with { Confirmed = true, LastNoticeSeconds = Math.Max(0,
            (int) Math.Clamp(Math.Ceiling((state.RestartAtUtc - _timeProvider.GetUtcNow()).TotalSeconds), 0, int.MaxValue)) };
        await SaveCountdown(state);
        _countdown = state;
        _updateNotificationSent = true;
        _updateOnRestart = true;
        StartCountdownTimer();
        _logger.LogInformation("Game {Key} confirmed the update warning; restarting for {Version} at {Deadline}.",
            Key, state.Update.Version, state.RestartAtUtc);
    }

    private async Task CancelCountdown(CancellationToken cancel)
    {
        if (_countdown is not { } state)
            return;
        try { await CallCountdownApi(new CountdownRequest("cancel", state.RequestId), cancel); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            _notificationManager.SendError(Key, $"Could not cancel the game's update announcement: {e.Message}");
        }
        ClearCountdown();
        _updateNotificationSent = false;
    }

    private async Task RunCountdownTick(CommandUpdateCountdown command, CancellationToken cancel)
    {
        if (command.Generation != _countdownGeneration || _countdown is not { Confirmed: true } state || _stopped || !IsRunning)
            return;
        var now = _timeProvider.GetUtcNow();
        if (_countdownShutdownDeadline is { } shutdownDeadline)
        {
            if (now >= shutdownDeadline)
            {
                StopCountdownTimer();
                _notificationManager.SendError(Key, "Game did not finish its graceful update shutdown within the configured timeout; terminating the process.");
                await _runningServer!.Kill();
            }
            return;
        }
        var remaining = (int) Math.Ceiling((state.RestartAtUtc - now).TotalSeconds);
        if (remaining <= 0)
        {
            try { InstanceStorage.EnsureAvailable(InstanceDir, 0, UpdatePreparationOptions.MinimumFreeSpaceBytes); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _notificationManager.SendError(Key, $"Scheduled update cancelled before shutdown: {e.Message}");
                await CancelCountdown(cancel);
                return;
            }
            await RequestCountdownShutdown(state, cancel);
            return;
        }
        if (now < _nextReminderAttempt || !(remaining <= 300 && state.LastNoticeSeconds > 300 ||
                                         remaining <= 60 && state.LastNoticeSeconds > 60))
            return;
        try
        {
            await CallCountdownApi(new CountdownRequest("announce", state.RequestId), cancel);
            _countdown = state with { LastNoticeSeconds = remaining };
            await SaveCountdown(_countdown);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            _nextReminderAttempt = now.AddSeconds(30);
            _notificationManager.SendError(Key, $"Could not deliver an update countdown reminder: {e.Message}");
        }
    }

    private async Task RequestCountdownShutdown(CountdownState state, CancellationToken cancel)
    {
        _expectedServerExit = true;
        _countdownShutdownDeadline = _timeProvider.GetUtcNow().AddSeconds(_instanceConfig.UpdateRestart.ShutdownTimeoutSeconds);
        try { await CallCountdownApi(new CountdownRequest("shutdown", state.RequestId), cancel); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            _notificationManager.SendError(Key, $"Graceful update shutdown request failed: {e.Message}. Waiting for the shutdown timeout before terminating.");
        }
    }
}
