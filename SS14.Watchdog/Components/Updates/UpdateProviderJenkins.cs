using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SS14.Watchdog.Configuration.Updates;
using SS14.Watchdog.Utility;

namespace SS14.Watchdog.Components.Updates
{
    /// <summary>
    ///     Update providers that pulls the latest build of a Jenkins job as updates.
    /// </summary>
    public sealed class UpdateProviderJenkins : UpdateProvider
    {
        private readonly HttpClient _httpClient = new HttpClient();

        private readonly ILogger<UpdateProviderJenkins> _logger;
        private readonly string _baseUrl;
        private readonly string _jobName;

        public UpdateProviderJenkins(UpdateProviderJenkinsConfiguration configuration,
            ILogger<UpdateProviderJenkins> logger)
        {
            _logger = logger;
            _baseUrl = configuration.BaseUrl;
            _jobName = Uri.EscapeDataString(configuration.JobName);
        }

        public override async Task<bool> CheckForUpdateAsync(string? currentVersion, CancellationToken cancel = default)
        {
            try
            {
                var lastSuccessfulBuild = await GetLastSuccessfulBuildAsync(cancel);

                if (lastSuccessfulBuild == null)
                {
                    // No succeeded builds?
                    return false;
                }

                return lastSuccessfulBuild.Number.ToString(CultureInfo.InvariantCulture) != currentVersion;
            }
            catch (HttpRequestException e)
            {
                _logger.LogWarning(SensitiveData.SafeException(e), "Failed to check Jenkins for updates.");
                throw;
            }

        }

        public override async Task<PreparedUpdate?> PrepareUpdateAsync(
            string? currentVersion,
            UpdatePreparationOptions options,
            CancellationToken cancel = default)
        {
            try
            {
                _logger.LogTrace("Preparing update...");

                var buildRef = await GetLastSuccessfulBuildAsync(cancel);

                if (buildRef == null)
                {
                    _logger.LogTrace("No last build?");
                    return null;
                }

                if (buildRef.Number.ToString(CultureInfo.InvariantCulture) == currentVersion)
                {
                    _logger.LogTrace("Update not necessary!");
                    return null;
                }

                _logger.LogTrace("New version is {newVersion} from {oldVersion}", buildRef.Number,
                    currentVersion ?? "<none>");


                var downloadRootUri = new Uri($"{_baseUrl}/job/{_jobName}/{buildRef.Number}/artifact/release/");

                // Create temporary file to download binary into (not doing this in memory).
                await using var tempFile = TempFile.CreateTempFile(options.InstanceDirectory);
                // Download URI for server binary.
                var serverDownload = new Uri(downloadRootUri, ServerZipName);

                _logger.LogTrace("Downloading server binary to {TempFile}.", tempFile.Name);

                // Download to file...
                using var resp = await _httpClient.GetAsync(serverDownload, HttpCompletionOption.ResponseHeadersRead, cancel);
                resp.EnsureSuccessStatusCode();
                await UpdateStaging.CopyHttpContentWithLimitAsync(
                    resp.Content,
                    tempFile,
                    options.MaximumDownloadBytes,
                    cancel,
                    options.MinimumFreeSpaceBytes);

                tempFile.Seek(0, SeekOrigin.Begin);
                var version = buildRef.Number.ToString(CultureInfo.InvariantCulture);
                var prepared = await UpdateStaging.StageArchiveAsync(
                    tempFile,
                    version,
                    options.InstanceDirectory,
                    options.MaximumExtractedBytes,
                    cancel,
                    options.MinimumFreeSpaceBytes);
                _logger.LogInformation("Prepared validated Jenkins update {Version} in {StagingDirectory}.", version,
                    prepared.StagingDirectory);
                return prepared;
            }
            catch (Exception e)
            {
                _logger.LogError(SensitiveData.SafeException(e), "Failed to prepare Jenkins update.");
                throw;
            }
        }

        private async Task<JenkinsBuildRef?> GetLastSuccessfulBuildAsync(CancellationToken cancel = default)
        {
            var jobUri = new Uri($"{_baseUrl}/job/{_jobName}/api/json");

            using var jobDataResponse = await _httpClient.GetAsync(jobUri, cancel);

            jobDataResponse.EnsureSuccessStatusCode();

            var jobInfo = JsonSerializer.Deserialize<JenkinsJobInfo>(await jobDataResponse.Content.ReadAsStringAsync(cancel));

            return jobInfo!.LastSuccessfulBuild;
        }
    }
}
