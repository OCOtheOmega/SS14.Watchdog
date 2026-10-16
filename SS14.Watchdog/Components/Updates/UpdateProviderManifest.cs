using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using SS14.Watchdog.Configuration.Updates;
using SS14.Watchdog.Utility;

namespace SS14.Watchdog.Components.Updates
{
    public sealed record UpdateVersionInfo(string Version, DateTimeOffset Time);

    public sealed class UpdateProviderManifest : UpdateProvider
    {
        public const string HttpClientName = "manifest_updates";
        private const int DownloadTimeoutSeconds = 120;

        private readonly HttpClient _httpClient;

        private readonly string _manifestUrl;
        private readonly UpdateProviderManifestConfiguration _configuration;
        private readonly ILogger<UpdateProviderManifest> _logger;

        public UpdateProviderManifest(
            UpdateProviderManifestConfiguration configuration,
            ILogger<UpdateProviderManifest> logger,
            HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _configuration = configuration;
            _logger = logger;
            _manifestUrl = configuration.ManifestUrl;
            SensitiveData.Register(configuration.Authentication?.Password);
        }

        public override async Task<bool> CheckForUpdateAsync(
            string? currentVersion,
            CancellationToken cancel = default)
        {
            var manifest = await FetchManifestInfoAsync(cancel);
            if (manifest == null)
                return false;

            return SelectMaxVersion(manifest) != currentVersion;
        }

        public override async Task<PreparedUpdate?> PrepareUpdateAsync(
            string? currentVersion,
            UpdatePreparationOptions options,
            CancellationToken cancel = default)
        {
            return await PrepareVersionAsync(currentVersion, null, options, cancel);
        }

        /// <summary>
        /// Stages an explicitly selected manifest version using the same validation as automatic updates.
        /// </summary>
        public Task<PreparedUpdate?> PrepareUpdateToVersionAsync(
            string targetVersion,
            UpdatePreparationOptions options,
            CancellationToken cancel = default)
        {
            return PrepareVersionAsync(null, targetVersion, options, cancel);
        }

        /// <summary>
        /// Resolves the target version to revert to based on the current version, an explicitly specified version,
        /// and available versions in the manifest.
        /// </summary>
        /// <param name="currentVersion">
        /// The current version of the application. This determines the starting point for identifying a prior version to revert to.
        /// </param>
        /// <param name="explicitVersion">
        /// An explicitly specified version to revert to. If provided, this version will be validated against the manifest.
        /// If it is valid, it will be returned; otherwise, null will be returned.
        /// </param>
        /// <param name="cancel">
        /// An optional <see cref="CancellationToken"/> that can be used to signal the operation should be canceled.
        /// </param>
        /// <returns>
        /// A string representing the resolved target version to revert to if successful; otherwise, null if no valid version
        /// could be determined based on the provided criteria.
        /// </returns>
        public async Task<string?> ResolveRevertTargetAsync(
            string? currentVersion,
            string? explicitVersion,
            CancellationToken cancel = default)
        {
            var manifest = await FetchManifestInfoAsync(cancel);
            if (manifest == null)
                return null;

            if (explicitVersion != null)
                return manifest.Builds.ContainsKey(explicitVersion) ? explicitVersion : null;

            if (currentVersion == null || !manifest.Builds.TryGetValue(currentVersion, out var currentInfo))
                return null;

            return manifest.Builds
                .Where(kv => kv.Value.Time < currentInfo.Time)
                .OrderByDescending(kv => kv.Value.Time)
                .ThenByDescending(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key)
                .FirstOrDefault();
        }

        /// <summary>
        /// Retrieves a list of the most recent update versions from the manifest.
        /// </summary>
        /// <param name="count">
        /// The maximum number of recent versions to retrieve.
        /// </param>
        /// <param name="cancel">
        /// An optional <see cref="CancellationToken"/> used to signal the operation should be canceled.
        /// </param>
        /// <returns>
        /// A read-only list of <see cref="UpdateVersionInfo"/> representing the most recent versions sorted by date,
        /// or an empty list if the manifest cannot be fetched or contains no versions.
        /// </returns>
        public async Task<IReadOnlyList<UpdateVersionInfo>> GetRecentVersionsAsync(
            int count,
            CancellationToken cancel = default)
        {
            var manifest = await FetchManifestInfoAsync(cancel);
            if (manifest == null)
                return Array.Empty<UpdateVersionInfo>();

            return manifest.Builds
                .OrderByDescending(kv => kv.Value.Time)
                .ThenByDescending(kv => kv.Key, StringComparer.Ordinal)
                .Take(count)
                .Select(kv => new UpdateVersionInfo(kv.Key, kv.Value.Time))
                .ToList();
        }

        private async Task<PreparedUpdate?> PrepareVersionAsync(
            string? currentVersion,
            string? targetVersion,
            UpdatePreparationOptions options,
            CancellationToken cancel)
        {
            try
            {
                var manifest = await FetchManifestInfoAsync(cancel);
                if (manifest == null)
                    return null;

                var maxVersion = targetVersion ?? SelectMaxVersion(manifest);
                if (maxVersion == null)
                {
                    _logger.LogWarning("Manifest contains no builds; not updating.");
                    return null;
                }

                if (maxVersion == currentVersion)
                {
                    _logger.LogDebug("Update not necessary!");
                    return null;
                }

                if (!manifest.Builds.TryGetValue(maxVersion, out var versionInfo))
                {
                    _logger.LogWarning("Requested version {Version} is no longer present in the manifest.", maxVersion);
                    return null;
                }

                _logger.LogTrace("Preparing version {NewVersion} from {OldVersion}", maxVersion, currentVersion ?? "<none>");

                var rid = RidUtility.FindBestRid(versionInfo.Server.Keys);
                if (rid == null)
                {
                    throw new InvalidDataException($"Manifest version {maxVersion} has no compatible server build for this platform.");
                }

                var build = versionInfo.Server[rid];
                if (!Uri.TryCreate(build.Url, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidDataException($"Manifest version {maxVersion} has a non-HTTPS or invalid server URL.");
                }

                byte[] downloadHash;
                try
                {
                    downloadHash = Convert.FromHexString(build.Sha256);
                }
                catch (FormatException)
                {
                    throw new InvalidDataException($"Manifest version {maxVersion} has an invalid SHA-256 value.");
                }

                if (downloadHash.Length != 32)
                {
                    throw new InvalidDataException($"Manifest version {maxVersion} has an invalid SHA-256 length.");
                }

                await using var tempFile = TempFile.CreateTempFile(options.InstanceDirectory);
                _logger.LogTrace("Downloading server binary to {TempFile}.", tempFile.Name);

                using var timeoutTcs = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeoutTcs.CancelAfter(TimeSpan.FromSeconds(DownloadTimeoutSeconds));
                using var resp = await _httpClient.SendAsync(
                    MakeAuthenticatedGet(downloadUri.ToString()),
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutTcs.Token);

                resp.EnsureSuccessStatusCode();
                await UpdateStaging.CopyHttpContentWithLimitAsync(
                    resp.Content,
                    tempFile,
                    options.MaximumDownloadBytes,
                    timeoutTcs.Token,
                    options.MinimumFreeSpaceBytes);

                _logger.LogTrace("Update file downloaded to disk; verifying SHA-256.");
                tempFile.Seek(0, SeekOrigin.Begin);
                var hashOutput = await SHA256.HashDataAsync(tempFile, cancel);

                if (!downloadHash.AsSpan().SequenceEqual(hashOutput))
                {
                    throw new InvalidDataException($"SHA-256 verification failed for manifest version {maxVersion}.");
                }

                tempFile.Seek(0, SeekOrigin.Begin);
                var prepared = await UpdateStaging.StageArchiveAsync(
                    tempFile,
                    maxVersion,
                    options.InstanceDirectory,
                    options.MaximumExtractedBytes,
                    cancel,
                    options.MinimumFreeSpaceBytes);
                _logger.LogInformation("Prepared validated update {Version} in {StagingDirectory}.", maxVersion, prepared.StagingDirectory);
                return prepared;
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new IOException($"Timed out while preparing an update after {DownloadTimeoutSeconds} seconds.");
            }
            catch (Exception e)
            {
                _logger.LogError(SensitiveData.SafeException(e), "Failed to prepare update from manifest.");
                throw;
            }
        }

        private async Task<ManifestInfo?> FetchManifestInfoAsync(CancellationToken cancel)
        {
            if (!Uri.TryCreate(_manifestUrl, UriKind.Absolute, out var manifestUri) || manifestUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Manifest URL must be an absolute HTTPS URL.");
            }

            _logger.LogDebug("Fetching build manifest.");
            try
            {
                using var resp = await _httpClient.SendAsync(MakeAuthenticatedGet(manifestUri.ToString()), cancel);
                resp.EnsureSuccessStatusCode();

                var manifest = await resp.Content.ReadFromJsonAsync<ManifestInfo>(cancel);
                if (manifest == null)
                    throw new InvalidDataException("Build manifest response was null.");

                return manifest;
            }
            catch (Exception e)
            {
                _logger.LogError(SensitiveData.SafeException(e), "Failed to fetch build manifest.");
                throw;
            }
        }

        private HttpRequestMessage MakeAuthenticatedGet(string url)
        {
            var message = new HttpRequestMessage(HttpMethod.Get, url);
            if (_configuration.Authentication is { Username: { } user, Password: { } pass })
                message.Headers.Authorization = new AuthenticationHeaderValue("Basic", MakeBasicAuthValue(user, pass));

            return message;
        }

        private static string MakeBasicAuthValue(string user, string pass)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        }

        private static string? SelectMaxVersion(ManifestInfo manifest)
        {
            if (manifest.Builds.Count == 0)
                return null;

            return manifest.Builds
                .OrderByDescending(x => x.Value.Time)
                .ThenByDescending(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Key)
                .First();
        }

        private sealed record ManifestInfo
        {
            [UsedImplicitly]
            public ManifestInfo(Dictionary<string, VersionInfo> builds)
            {
                Builds = builds;
            }

            [JsonPropertyName("builds")] public Dictionary<string, VersionInfo> Builds { get; }
        }

        private sealed record VersionInfo
        {
            [JsonPropertyName("time")] public DateTimeOffset Time { get; }
            [JsonPropertyName("client")] public DownloadInfo Client { get; }
            [JsonPropertyName("server")] public Dictionary<string, DownloadInfo> Server { get; }

            [UsedImplicitly]
            public VersionInfo(DateTimeOffset time, DownloadInfo client, Dictionary<string, DownloadInfo> server)
            {
                Client = client;
                Server = server;
                Time = time;
            }
        }

        private sealed record DownloadInfo
        {
            [JsonPropertyName("url")] public string Url { get; }
            [JsonPropertyName("sha256")] public string Sha256 { get; }

            [UsedImplicitly]
            public DownloadInfo(string url, string sha256)
            {
                Url = url;
                Sha256 = sha256;
            }
        }
    }
}
