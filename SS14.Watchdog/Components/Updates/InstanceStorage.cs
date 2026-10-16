using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SS14.Watchdog.Configuration;

namespace SS14.Watchdog.Components.Updates;

public static class InstanceStorage
{
    private static readonly ConcurrentDictionary<string, byte> ActivePaths = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static IDisposable Protect(string path)
    {
        path = Path.GetFullPath(path);
        if (!ActivePaths.TryAdd(path, 0))
            throw new IOException("An update path is already in use.");
        return new PathLease(path);
    }

    public static void EnsureAvailable(string path, long additionalBytes, long reserveBytes,
        Func<string, long>? availableSpace = null)
    {
        if (additionalBytes < 0 || reserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalBytes));
        var directory = Path.GetFullPath(path);
        while (!Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory) ?? throw new IOException("Cannot locate the update volume.");
        var available = availableSpace?.Invoke(directory) ?? new DriveInfo(directory).AvailableFreeSpace;
        var required = checked(additionalBytes + reserveBytes);
        if (available < required)
            throw new IOException($"Insufficient disk space: {required} bytes required including reserve, {available} bytes available.");
    }

    public static IReadOnlyList<string> Cleanup(string instanceDirectory, string? preparedDirectory,
        CleanupConfiguration options, DateTimeOffset now)
    {
        var errors = new List<string>();
        var root = Path.GetFullPath(instanceDirectory);
        if (!Directory.Exists(root))
            return errors;
        var prepared = preparedDirectory == null ? null : Path.GetFullPath(preparedDirectory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, ".update-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(entry);
            var isStaging = name.StartsWith(".update-staging-", StringComparison.Ordinal) && Directory.Exists(entry);
            var isDownload = name.StartsWith(".update-download-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal) && File.Exists(entry);
            if ((!isStaging && !isDownload) || ActivePaths.ContainsKey(Path.GetFullPath(entry)) ||
                string.Equals(entry, prepared, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                continue;
            if (now.UtcDateTime - File.GetLastWriteTimeUtc(entry) >= TimeSpan.FromHours(options.TemporaryMaxAgeHours))
                DeleteOwned(entry, root, errors);
        }

        var dumpsDirectory = Path.Combine(root, "dumps");
        if (!Directory.Exists(dumpsDirectory) || IsLink(dumpsDirectory))
            return errors;
        var dumps = Directory.EnumerateFiles(dumpsDirectory, "dump_*", SearchOption.TopDirectoryOnly)
            .Where(p => !IsLink(p) && !ActivePaths.ContainsKey(Path.GetFullPath(p)))
            .Select(p => new FileInfo(p)).OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = dumps.Sum(f => f.Length);
        foreach (var file in dumps)
        {
            if (now.UtcDateTime - file.LastWriteTimeUtc < TimeSpan.FromDays(options.DumpMaxAgeDays) &&
                total <= options.DumpMaximumMiB * 1024L * 1024L)
                continue;
            var length = file.Length;
            if (DeleteOwned(file.FullName, root, errors))
                total -= length;
        }
        return errors;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool DeleteOwned(string path, string root, List<string> errors)
    {
        var target = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, comparison) || IsLink(target))
            return false;
        try
        {
            if (Directory.Exists(target))
                Directory.Delete(target, true);
            else
                File.Delete(target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Could not clean {Path.GetFileName(target)}: {e.GetType().Name}");
            return false;
        }
    }

    private sealed class PathLease(string path) : IDisposable
    {
        public void Dispose() => ActivePaths.TryRemove(path, out _);
    }
}
