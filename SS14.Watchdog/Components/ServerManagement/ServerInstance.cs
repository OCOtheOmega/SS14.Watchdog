using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SS14.Watchdog.Components.BackgroundTasks;
using SS14.Watchdog.Components.DataManagement;
using SS14.Watchdog.Components.Notifications;
using SS14.Watchdog.Components.ProcessManagement;
using SS14.Watchdog.Components.Updates;
using SS14.Watchdog.Configuration;
using SS14.Watchdog.Configuration.Updates;
using SS14.Watchdog.Utility;

namespace SS14.Watchdog.Components.ServerManagement
{
    public sealed partial class ServerInstance : IServerInstance
    {
        public string Key { get; }
        public string? Secret { get; private set; }
        public string? ApiToken => _instanceConfig.ApiToken;
        public string? CurrentRevision => _currentRevision;

        public bool IsRunning => _runningServer != null;

        /// <summary>
        ///     How long since the last ping before we consider the server "dead" and forcefully terminate it.
        /// </summary>
        private TimeSpan PingTimeoutDelay => TimeSpan.FromSeconds(_lastPing != null ? _instanceConfig.TimeoutSeconds :
            _pendingUpdateHealth != null && _instanceConfig.AutoUpdateHealthTimeoutSeconds is > 0
                ? _instanceConfig.AutoUpdateHealthTimeoutSeconds.Value : _instanceConfig.StartupTimeoutSeconds);

        private readonly HttpClient _serverHttpClient;

        private InstanceConfiguration _instanceConfig;
        private readonly IConfiguration _configuration;
        private readonly UpdateProvider? _updateProvider;
        private readonly ServersConfiguration _serversConfiguration;
        private readonly ILogger<ServerInstance> _logger;
        private readonly IBackgroundTaskQueue _taskQueue;
        private readonly DataManager _dataManager;
        private readonly IProcessManager _processManager;
        private readonly NotificationManager _notificationManager;

        private string? _currentRevision;
        private bool _updateOnRestart = true;
        private bool _updateNotificationSent;
        private bool _updateCheckInProgress;
        private int _updateGeneration;
        private bool _manualVersionOverride;
        private bool _updateCheckAfterHealth;
        private CancellationToken _instanceCancellation;
        private readonly HashSet<string> _blockedUpdateVersions = new(StringComparer.Ordinal);
        private PreparedUpdate? _preparedUpdate;
        private PendingUpdateHealth? _pendingUpdateHealth;


        private IProcessHandle? _runningServer;

        private UpdatePreparationOptions UpdatePreparationOptions => new(
            InstanceDir,
            Math.Max(1, _instanceConfig.AutoUpdateMaximumDownloadMiB) * 1024L * 1024L,
            Math.Max(1, _instanceConfig.AutoUpdateMaximumExtractedMiB) * 1024L * 1024L,
            _instanceConfig.MinimumFreeSpaceMiB * 1024L * 1024L);

        public ServerInstance(string key,
            InstanceConfiguration instanceConfig,
            IConfiguration configuration,
            ServersConfiguration serversConfiguration,
            ILogger<ServerInstance> logger,
            IBackgroundTaskQueue taskQueue,
            IServiceProvider serviceProvider,
            DataManager dataManager,
            IProcessManager processManager,
            NotificationManager notificationManager,
            HttpClient? serverHttpClient = null)
        {
            Key = key;
            _instanceConfig = instanceConfig;
            _configuration = configuration;
            _serversConfiguration = serversConfiguration;
            _logger = logger;
            _taskQueue = taskQueue;
            _dataManager = dataManager;
            _processManager = processManager;
            _notificationManager = notificationManager;
            _serverHttpClient = serverHttpClient ?? new HttpClient();
            _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
            OperationsValidation.Validate(instanceConfig);

            if (!string.IsNullOrEmpty(_instanceConfig.ApiTokenFile))
            {
                _instanceConfig.ApiToken = File.ReadAllText(_instanceConfig.ApiTokenFile);
            }
            SensitiveData.Register(_instanceConfig.ApiToken);
            foreach (var value in _instanceConfig.EnvironmentVariables.Values)
                SensitiveData.Register(value);

            switch (instanceConfig.UpdateType)
            {
                case "Jenkins":
                    var jenkinsConfig = configuration
                        .GetSection($"Servers:Instances:{key}:Updates")
                        .Get<UpdateProviderJenkinsConfiguration>();

                    if (jenkinsConfig == null)
                        throw new InvalidOperationException("Invalid configuration!");

                    _updateProvider = new UpdateProviderJenkins(
                        jenkinsConfig,
                        serviceProvider.GetRequiredService<ILogger<UpdateProviderJenkins>>());
                    break;

                case "Local":
                    var localConfig = configuration
                        .GetSection($"Servers:Instances:{key}:Updates")
                        .Get<UpdateProviderLocalConfiguration>();

                    if (localConfig == null)
                        throw new InvalidOperationException("Invalid configuration!");

                    _updateProvider = new UpdateProviderLocal(
                        this,
                        localConfig,
                        serviceProvider.GetRequiredService<ILogger<UpdateProviderLocal>>(),
                        configuration);
                    break;

                case "Git":
                    var gitConfig = configuration
                        .GetSection($"Servers:Instances:{key}:Updates")
                        .Get<UpdateProviderGitConfiguration>();

                    if (gitConfig == null)
                        throw new InvalidOperationException("Invalid configuration!");

                    _updateProvider = new UpdateProviderGit(
                        this,
                        gitConfig,
                        serviceProvider.GetRequiredService<ILogger<UpdateProviderGit>>(),
                        configuration);
                    break;

                case "Manifest":
                    var manifestConfig = configuration
                        .GetSection($"Servers:Instances:{key}:Updates")
                        .Get<UpdateProviderManifestConfiguration>();

                    if (manifestConfig == null)
                        throw new InvalidOperationException("Invalid configuration!");

                    _updateProvider = new UpdateProviderManifest(
                        manifestConfig,
                        serviceProvider.GetRequiredService<ILogger<UpdateProviderManifest>>(),
                        serviceProvider.GetService<IHttpClientFactory>()?.CreateClient(UpdateProviderManifest.HttpClientName));
                    break;

                case "Dummy":
                    _updateProvider = new UpdateProviderDummy();
                    break;

                case null:
                    _updateProvider = null;
                    break;

                default:
                    throw new ArgumentException($"Unknown update type: {instanceConfig.UpdateType}");
            }

            if (!Directory.Exists(InstanceDir))
            {
                Directory.CreateDirectory(InstanceDir);
                _logger.LogInformation("Created InstanceDir {InstanceDir}", InstanceDir);
            }

            LoadData();
        }

        public void OnConfigUpdate(InstanceConfiguration cfg)
        {
            try
            {
                OperationsValidation.Validate(cfg);
            }
            catch (Exception e) when (e is InvalidDataException or TimeZoneNotFoundException or InvalidTimeZoneException or FormatException)
            {
                _logger.LogError(SensitiveData.SafeException(e), "Rejected invalid configuration reload for {Key}.", Key);
                _notificationManager.SendError(Key, $"Configuration reload rejected: {e.Message}");
                return;
            }
            _instanceConfig = cfg;
            SensitiveData.Register(cfg.ApiToken);
            foreach (var value in cfg.EnvironmentVariables.Values)
                SensitiveData.Register(value);
        }

        private void LoadData()
        {
            using var con = _dataManager.OpenConnection();

            var revision = con.QuerySingle<string>(
                "SELECT Revision FROM ServerInstance WHERE Key = @Key",
                new { Key });

            // Actually copy data over.
            _currentRevision = revision;
        }

        private void SaveData()
        {
            using var con = _dataManager.OpenConnection();

            con.Execute(
                "UPDATE ServerInstance SET Revision = @Revision WHERE Key = @Key",
                new
                {
                    Revision = _currentRevision,
                    Key
                });
        }

        private void GenerateNewToken()
        {
            Span<byte> raw = stackalloc byte[64];
            RandomNumberGenerator.Fill(raw);

            var token = Convert.ToBase64String(raw);

            SetToken(token);
        }

        private void SetToken(string token)
        {
            Secret = token;
            SensitiveData.Register(token);

            _serverHttpClient.DefaultRequestHeaders.Remove("WatchdogToken");
            _serverHttpClient.DefaultRequestHeaders.Add("WatchdogToken", token);
        }

        public string InstanceDir => Path.Combine(
            Environment.CurrentDirectory,
            _serversConfiguration.InstanceRoot,
            Key);

        public async Task ShutdownAsync()
        {
            _logger.LogTrace("Shutting down server instance {Key}", Key);

            if (IsRunning)
            {
                if (_processManager.CanPersist)
                {
                    _logger.LogInformation("Not shutting down {Key}, persisting running server", Key);
                }
                else
                {
                    _logger.LogInformation("Shutting down running server {Key}", Key);
                    await ForceShutdownServerAsync();
                }
            }
        }

        public async Task ForceShutdown()
        {
            if (_runningServer != null)
                await _runningServer.Kill();
        }

        public async Task PingReceived()
        {
            // TODO: Ok so there's a mild race condition here:
            // It's possible for the server to send a ping JUST as it gets killed.
            // This ping gets queued, and due to the ordering in the entire system,
            // this ping will then be immediately seen as the next process' ping.
            await _commandQueue.Writer.WriteAsync(new CommandServerPing(_startNumber));
        }

        private async void StartTimeoutTimer()
        {
            CancelTimeoutTimer();
            var number = _serverTimeoutNumber;
            _serverTimeoutTcs = CancellationTokenSource.CreateLinkedTokenSource(_instanceCancellation);

            var token = _serverTimeoutTcs.Token;

            try
            {
                _logger.LogTrace("Timeout timer {Number} started", number);
                await Task.Delay(PingTimeoutDelay, _timeProvider, token);

                // ReSharper disable once MethodSupportsCancellation
                await _commandQueue.Writer.WriteAsync(new CommandTimedOut(number));
            }
            catch (OperationCanceledException)
            {
                // It still lives.
                _logger.LogTrace("Timeout {Number} broken, it lives.", number);
            }
        }

        private async Task TimeoutKill()
        {
            _logger.LogWarning("{Key}: timed out, killing", Key);

            if (_runningServer == null)
                return;

            _exitFailureReason = _lastPing == null ? "Startup timeout: no first watchdog ping received." : "Runtime timeout: watchdog pings stopped.";

            if (_instanceConfig.DumpOnTimeout)
            {
                if (!OperatingSystem.IsWindows())
                {
                    _logger.LogInformation("{Key}: making on-kill process dump of type {DumpType}",
                        Key, _instanceConfig.TimeoutDumpType);

                    try
                    {
                        var dumpDir = Path.Combine(InstanceDir, "dumps");
                        Directory.CreateDirectory(dumpDir);
                        var dumpFile = Path.Combine(dumpDir, $"dump_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");

                        _runningServer.DumpProcess(dumpFile, _instanceConfig.TimeoutDumpType);

                        _logger.LogInformation("{Key}: Process dump written to {DumpFilePath}", Key, dumpFile);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "{Key}: exception while making process dump", Key);
                    }
                }
                else
                {
                    _logger.LogInformation("{Key}: not creating process dump: not supported on Windows", Key);
                }

                _logger.LogInformation("{Key}: killing process...", Key);
            }

            await _runningServer.Kill();

            // Monitor will notice server died and pick it up.
        }

        public void HandleUpdateCheck()
        {
            _ = QueueExplicitUpdateCheckAsync();
        }

        private async Task QueueExplicitUpdateCheckAsync()
        {
            try
            {
                await _commandQueue.Writer.WriteAsync(new CommandCheckForUpdate(Manual: true), _instanceCancellation);
            }
            catch (OperationCanceledException) when (_instanceCancellation.IsCancellationRequested)
            {
                // The instance is shutting down.
            }
            catch (Exception e)
            {
                _logger.LogError(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Failed to queue an update check for {Key}.", Key);
            }
        }

        private void BeginUpdateCheck()
        {
            if (_updateProvider == null)
                return;

            if (_updateCheckInProgress)
            {
                _logger.LogTrace("Skipping update check for {Key}: another check is already running", Key);
                return;
            }

            _updateCheckInProgress = true;
            var generation = _updateGeneration;
            var currentRevision = _currentRevision;
            var options = UpdatePreparationOptions;
            _logger.LogDebug("Received update notification.");
            try
            {
                _taskQueue.QueueTask(async cancel =>
                {
                    PreparedUpdate? update = null;
                    try
                    {
                        if (await _updateProvider.CheckForUpdateAsync(currentRevision, cancel))
                            update = await _updateProvider.PrepareUpdateAsync(currentRevision, options, cancel);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Failed to prepare an update for {Key}.", Key);
                        if (!cancel.IsCancellationRequested)
                            _notificationManager.SendError(Key, $"Update preparation failed: {e.Message}");
                    }
                    try
                    {
                        await _commandQueue.Writer.WriteAsync(new CommandUpdatePrepared(update, generation), cancel);
                    }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                    {
                        if (update != null)
                            UpdateStaging.Discard(update);
                    }
                });
            }
            catch
            {
                _updateCheckInProgress = false;
                throw;
            }
        }

        private void CommitHealthyUpdate()
        {
            var update = _pendingUpdateHealth;
            if (update == null)
                return;

            _pendingUpdateHealth = null;
            try
            {
                if (update.AppliedUpdate != null)
                    UpdateStaging.Commit(update.AppliedUpdate);
            }
            catch (Exception e)
            {
                _logger.LogWarning(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Server {Key} is healthy, but failed to remove its previous update backup.", Key);
            }

            SaveData();
            _updateNotificationSent = false;
            _notificationManager.SendUpdated(Key, FormatRevision(update.PreviousRevision), update.TargetRevision);
            _logger.LogInformation("Server {Key} confirmed update {Version} with its first watchdog ping.", Key,
                update.TargetRevision);
            ResumeUpdateCheckAfterHealth();
        }

        private void ResumeUpdateCheckAfterHealth()
        {
            if (!_updateCheckAfterHealth || _pendingUpdateHealth != null || _manualVersionOverride)
                return;

            _updateCheckAfterHealth = false;
            BeginUpdateCheck();
        }

        private async Task RollbackFailedUpdateAsync(string reason, CancellationToken cancel)
        {
            var update = _pendingUpdateHealth;
            if (update == null)
                return;

            _pendingUpdateHealth = null;
            _currentRevision = update.PreviousRevision;
            _blockedUpdateVersions.Add(update.TargetRevision);
            _updateOnRestart = _preparedUpdate != null;
            _updateNotificationSent = false;

            var rollbackSucceeded = update.AppliedUpdate == null;
            if (update.AppliedUpdate != null)
            {
                try
                {
                    rollbackSucceeded = UpdateStaging.Rollback(update.AppliedUpdate, Path.Combine(InstanceDir, "bin"));
                }
                catch (Exception e)
                {
                    rollbackSucceeded = false;
                    _logger.LogError(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Failed to restore the previous server files for {Key}.", Key);
                }
            }

            if (rollbackSucceeded)
            {
                _logger.LogWarning("Update {Version} for {Key} failed ({Reason}); starting the previous server version.",
                    update.TargetRevision, Key, reason);
                _notificationManager.SendError(Key,
                    $"Update `{update.TargetRevision}` failed: {reason}. Restoring `{FormatRevision(update.PreviousRevision)}`.");
            }
            else
            {
                _logger.LogError("Update {Version} for {Key} failed ({Reason}) and no previous files could be restored. Starting the available server files.",
                    update.TargetRevision, Key, reason);
                _notificationManager.SendError(Key,
                    $"Update `{update.TargetRevision}` failed: {reason}. No previous files could be restored; starting available files.");
            }

            if (!_stopped && _runningServer == null)
                await StartServer(cancel);
            ResumeUpdateCheckAfterHealth();
        }

        private static string FormatRevision(string? revision)
        {
            return string.IsNullOrWhiteSpace(revision) ? "<none>" : revision;
        }

        private async Task<bool> SendUpdateNotificationAsync(CancellationToken cancel = default)
        {
            try
            {
                using var resp = await _serverHttpClient.PostAsync($"http://localhost:{_instanceConfig.ApiPort}/update", null!,
                    cancel);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Bad HTTP status code on update notification: {status}", resp.StatusCode);
                    _notificationManager.SendError(Key, $"Server rejected the update notification: HTTP {(int) resp.StatusCode}. Will retry.");
                    return false;
                }

                _logger.LogInformation(
                    "Server {Key} accepted the update notification; the prepared update will be installed when the server exits.",
                    Key);
                return true;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                _logger.LogWarning(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Failed to send update notification to server {Key}.", Key);
                if (!cancel.IsCancellationRequested)
                    _notificationManager.SendError(Key, $"Could not notify the server about an update: {e.Message}. Will retry.");
                return false;
            }
        }

        public async Task DoRestartCommandAsync(CancellationToken cancel = default)
        {
            await _commandQueue.Writer.WriteAsync(new CommandRestart(), cancel);
        }

        public async Task DoStopCommandAsync(ServerInstanceStopCommand stopCommand, CancellationToken cancel = default)
        {
            await _commandQueue.Writer.WriteAsync(new CommandStop(stopCommand), cancel);
        }

        /// <summary>
        /// Reverts the server to a specified target version asynchronously.
        /// </summary>
        /// <param name="targetVersion">
        /// The target version to which the server should revert.
        /// If null, the server will determine the appropriate version to revert to.
        /// </param>
        /// <param name="immediate">
        /// A flag indicating whether the revert should be performed immediately.
        /// </param>
        /// <param name="cancel">
        /// An optional <see cref="CancellationToken"/> that can be used to signal the operation should be canceled.
        /// </param>
        /// <returns>
        /// The resolved target version string, or null if the operation cannot be performed (e.g., if the update provider is invalid).
        /// </returns>
        public async Task<string?> DoRevertCommandAsync(string? targetVersion, bool immediate,
            CancellationToken cancel = default)
        {
            try
            {
                return await PrepareRevertCommandAsync(targetVersion, immediate, cancel);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                _logger.LogError(SensitiveData.SafeException(e), "Could not prepare requested version for {Key}.", Key);
                _notificationManager.SendError(Key, $"Could not prepare requested version: {e.Message}");
                return null;
            }
        }

        private async Task<string?> PrepareRevertCommandAsync(string? targetVersion, bool immediate,
            CancellationToken cancel = default)
        {
            if (_updateProvider is not UpdateProviderManifest manifestProvider)
                return null;

            var resolved = await manifestProvider.ResolveRevertTargetAsync(_currentRevision, targetVersion, cancel);
            if (resolved == null)
                return null;

            // Download and validate before asking the running server to stop.
            var update = await manifestProvider.PrepareUpdateToVersionAsync(resolved, UpdatePreparationOptions, cancel);
            if (update == null)
                return null;

            try
            {
                await _commandQueue.Writer.WriteAsync(new CommandRevert(update, immediate), cancel);
            }
            catch
            {
                UpdateStaging.Discard(update);
                throw;
            }
            return resolved;
        }

        /// <summary>
        /// Retrieves a list of the most recent update versions asynchronously.
        /// </summary>
        /// <param name="count">
        /// The maximum number of recent versions to retrieve. Defaults to 5.
        /// </param>
        /// <param name="cancel">
        /// An optional <see cref="CancellationToken"/> that can be used to signal the operation should be canceled.
        /// </param>
        /// <returns>
        /// A read-only list of <see cref="UpdateVersionInfo"/> objects representing recent update versions.
        /// Returns null if the update provider is not available or valid.
        /// </returns>
        public async Task<IReadOnlyList<UpdateVersionInfo>?> GetRecentVersionsAsync(int count = 5,
            CancellationToken cancel = default)
        {
            if (_updateProvider is not UpdateProviderManifest manifestProvider)
                return null;

            return await manifestProvider.GetRecentVersionsAsync(count, cancel);
        }

        public async Task ForceShutdownServerAsync(CancellationToken cancel = default)
        {
            var proc = _runningServer;
            if (proc == null || await proc.GetExitStatusAsync() != null)
            {
                return;
            }

            try
            {
                var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                // Give it 5 seconds to respond.
                shutdownCts.CancelAfter(5000);
                await SendShutdownNotificationAsync(shutdownCts.Token);
            }
            catch (HttpRequestException e)
            {
                _logger.LogInformation(SS14.Watchdog.Utility.SensitiveData.SafeException(e), "Exception sending shutdown notification to server. Killing.");
                await proc.Kill();
                return;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Timeout sending shutdown notification to server. Killing.");
                await proc.Kill();
                return;
            }

            _logger.LogDebug("{Key} sent shutdown notification to server. Waiting for exit", Key);

            // Give it 5 seconds to shut down.
            var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            waitCts.CancelAfter(5000);
            ProcessExitStatus? status;
            try
            {
                await proc.WaitForExitAsync(waitCts.Token);
                status = await proc.GetExitStatusAsync();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("{Key} did not gracefully shut down in time, killing", Key);
                await proc.Kill();
                return;
            }

            _logger.LogInformation("{Key} shut down gracefully ({Status})", Key, status);
        }

        public async Task SendShutdownNotificationAsync(CancellationToken cancel = default)
        {
            await _serverHttpClient.PostAsync($"http://localhost:{_instanceConfig.ApiPort}/shutdown",
                new StringContent(JsonSerializer.Serialize(new ShutdownParameters
                {
                    Reason = "Watchdog shutting down."
                })), cancel);
        }

        [PublicAPI]
        private sealed class ShutdownParameters
        {
            // ReSharper disable once RedundantDefaultMemberInitializer
            public string Reason { get; set; } = default!;
        }

        private sealed record PendingUpdateHealth(
            string TargetRevision,
            string? PreviousRevision,
            AppliedUpdate? AppliedUpdate,
            int StartNumber = 0);
    }
}
