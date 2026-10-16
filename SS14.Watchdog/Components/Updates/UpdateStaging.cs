using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Mono.Unix;

namespace SS14.Watchdog.Components.Updates
{
    /// <summary>
    /// An update that was downloaded, extracted and validated without touching the live server files.
    /// </summary>
    public sealed record PreparedUpdate(string Version, string? StagingDirectory);

    /// <summary>
    /// Information needed to restore the previous server files after a failed launch.
    /// </summary>
    public sealed record AppliedUpdate(string? PreviousDirectory)
    {
        public bool HasFallback => PreviousDirectory != null;
    }

    /// <summary>
    /// Safely stages server packages and swaps them only after they have been validated.
    /// </summary>
    public static class UpdateStaging
    {
        private const string PreviousDirectorySuffix = ".previous";

        public static async Task<PreparedUpdate> StageArchiveAsync(
            Stream archiveStream,
            string version,
            string instanceDirectory,
            long maximumExtractedBytes,
            CancellationToken cancel = default,
            long minimumFreeSpaceBytes = 0)
        {
            if (maximumExtractedBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumExtractedBytes));

            var stagingDirectory = Path.Combine(instanceDirectory, $".update-staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            using var active = InstanceStorage.Protect(stagingDirectory);

            try
            {
                var stagingRoot = Path.GetFullPath(stagingDirectory);
                var stagingPrefix = stagingRoot + Path.DirectorySeparatorChar;
                long extractedBytes = 0;

                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
                long totalBytes = 0;
                foreach (var entry in archive.Entries)
                    totalBytes = checked(totalBytes + entry.Length);
                if (totalBytes > maximumExtractedBytes)
                    throw new InvalidDataException("Update archive exceeds the configured extracted-size limit.");
                InstanceStorage.EnsureAvailable(stagingDirectory, totalBytes, minimumFreeSpaceBytes);
                foreach (var entry in archive.Entries)
                {
                    cancel.ThrowIfCancellationRequested();

                    var destination = GetSafeDestination(stagingPrefix, entry.FullName);
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    checked
                    {
                        extractedBytes += entry.Length;
                    }

                    if (extractedBytes > maximumExtractedBytes)
                        throw new InvalidDataException("Update archive exceeds the configured extracted-size limit.");

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var input = entry.Open();
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, cancel);
                }

                ValidateServerExecutable(stagingDirectory);
                return new PreparedUpdate(version, stagingDirectory);
            }
            catch
            {
                Discard(new PreparedUpdate(version, stagingDirectory));
                throw;
            }
        }

        public static async Task CopyHttpContentWithLimitAsync(
            HttpContent content,
            Stream destination,
            long maximumDownloadBytes,
            CancellationToken cancel = default,
            long minimumFreeSpaceBytes = 0)
        {
            if (maximumDownloadBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumDownloadBytes));

            if (content.Headers.ContentLength is { } contentLength && contentLength > maximumDownloadBytes)
                throw new InvalidDataException("Update download exceeds the configured size limit.");

            if (destination is FileStream file)
                InstanceStorage.EnsureAvailable(Path.GetDirectoryName(file.Name)!, content.Headers.ContentLength ?? 0, minimumFreeSpaceBytes);

            await using var source = await content.ReadAsStreamAsync(cancel);
            var buffer = new byte[81920];
            long downloadedBytes = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancel);
                if (read == 0)
                    break;

                checked
                {
                    downloadedBytes += read;
                }

                if (downloadedBytes > maximumDownloadBytes)
                    throw new InvalidDataException("Update download exceeds the configured size limit.");

                if (destination is FileStream download)
                    InstanceStorage.EnsureAvailable(Path.GetDirectoryName(download.Name)!, read, minimumFreeSpaceBytes);

                await destination.WriteAsync(buffer.AsMemory(0, read), cancel);
            }
        }

        public static AppliedUpdate Apply(PreparedUpdate update, string binPath)
        {
            if (update.StagingDirectory == null)
                return new AppliedUpdate(null);

            ValidateServerExecutable(update.StagingDirectory);
            RecoverInterruptedUpdate(binPath);

            var previousDirectory = binPath + PreviousDirectorySuffix;
            var hadPreviousDirectory = Directory.Exists(binPath);

            try
            {
                if (hadPreviousDirectory)
                    Directory.Move(binPath, previousDirectory);

                Directory.Move(update.StagingDirectory, binPath);
                return new AppliedUpdate(hadPreviousDirectory ? previousDirectory : null);
            }
            catch
            {
                if (!Directory.Exists(binPath) && hadPreviousDirectory && Directory.Exists(previousDirectory))
                    Directory.Move(previousDirectory, binPath);

                throw;
            }
        }

        public static bool Rollback(AppliedUpdate update, string binPath)
        {
            if (!update.HasFallback || update.PreviousDirectory == null || !Directory.Exists(update.PreviousDirectory))
                return false;

            if (Directory.Exists(binPath))
                Directory.Delete(binPath, true);

            Directory.Move(update.PreviousDirectory, binPath);
            return true;
        }

        public static void Commit(AppliedUpdate update)
        {
            if (update.PreviousDirectory != null && Directory.Exists(update.PreviousDirectory))
                Directory.Delete(update.PreviousDirectory, true);
        }

        public static bool RecoverInterruptedUpdate(string binPath)
        {
            var previousDirectory = binPath + PreviousDirectorySuffix;
            if (!Directory.Exists(previousDirectory))
                return false;

            if (Directory.Exists(binPath))
                Directory.Delete(binPath, true);

            Directory.Move(previousDirectory, binPath);
            return true;
        }

        public static void Discard(PreparedUpdate update)
        {
            if (update.StagingDirectory != null && Directory.Exists(update.StagingDirectory))
                Directory.Delete(update.StagingDirectory, true);
        }

        private static string GetSafeDestination(string stagingPrefix, string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
                throw new InvalidDataException("Update archive contains an entry with an empty path.");

            var normalizedEntryName = entryName
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(stagingPrefix, normalizedEntryName));
            if (!destination.StartsWith(stagingPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("Update archive contains a path outside the staging directory.");

            return destination;
        }

        private static void ValidateServerExecutable(string directory)
        {
            var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Robust.Server.exe"
                : "Robust.Server";
            var executablePath = Path.Combine(directory, executableName);

            if (!File.Exists(executablePath))
                throw new InvalidDataException($"Update archive does not contain {executableName} at its root.");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var file = new UnixFileInfo(executablePath);
                file.FileAccessPermissions |=
                    FileAccessPermissions.UserExecute | FileAccessPermissions.GroupExecute |
                    FileAccessPermissions.OtherExecute;
            }
        }
    }
}
