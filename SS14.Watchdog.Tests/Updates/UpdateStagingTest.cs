using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;
using SS14.Watchdog.Components.Updates;

namespace SS14.Watchdog.Tests.Updates;

public sealed class UpdateStagingTest
{
    [Test]
    public async Task ValidArchiveCanBeAppliedAndRolledBack()
    {
        var instanceDirectory = CreateTemporaryDirectory();
        try
        {
            var binDirectory = Path.Combine(instanceDirectory, "bin");
            Directory.CreateDirectory(binDirectory);
            await File.WriteAllTextAsync(Path.Combine(binDirectory, ServerExecutableName), "old-server");

            await using var archive = await CreateServerArchive("new-server");
            var update = await UpdateStaging.StageArchiveAsync(archive, "new", instanceDirectory, 1024 * 1024);

            var applied = UpdateStaging.Apply(update, binDirectory);
            Assert.That(applied.HasFallback, Is.True);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(binDirectory, ServerExecutableName)), Is.EqualTo("new-server"));

            Assert.That(UpdateStaging.Rollback(applied, binDirectory), Is.True);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(binDirectory, ServerExecutableName)), Is.EqualTo("old-server"));
        }
        finally
        {
            Directory.Delete(instanceDirectory, true);
        }
    }

    [Test]
    public void ArchiveWithPathTraversalIsRejectedWithoutChangingLiveFiles()
    {
        var instanceDirectory = CreateTemporaryDirectory();
        try
        {
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("../outside.txt").Open()))
                    writer.Write("bad");
                using (var writer = new StreamWriter(zip.CreateEntry(ServerExecutableName).Open()))
                    writer.Write("server");
            }

            archive.Position = 0;
            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await UpdateStaging.StageArchiveAsync(archive, "bad", instanceDirectory, 1024 * 1024));
            Assert.That(Directory.GetDirectories(instanceDirectory, ".update-staging-*"), Is.Empty);
            Assert.That(File.Exists(Path.Combine(instanceDirectory, "outside.txt")), Is.False);
        }
        finally
        {
            Directory.Delete(instanceDirectory, true);
        }
    }

    [Test]
    public void ArchiveWithoutServerExecutableIsRejected()
    {
        var instanceDirectory = CreateTemporaryDirectory();
        try
        {
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("README.txt").Open());
                writer.Write("not a server");
            }

            archive.Position = 0;
            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await UpdateStaging.StageArchiveAsync(archive, "bad", instanceDirectory, 1024 * 1024));
        }
        finally
        {
            Directory.Delete(instanceDirectory, true);
        }
    }

    [Test]
    public void DownloadOverConfiguredLimitIsRejected()
    {
        using var content = new ByteArrayContent(new byte[1024]);
        using var destination = new MemoryStream();

        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await UpdateStaging.CopyHttpContentWithLimitAsync(content, destination, 512));
    }

    [Test]
    public async Task InterruptedUpdateRestoresPreviousFiles()
    {
        var instanceDirectory = CreateTemporaryDirectory();
        try
        {
            var binDirectory = Path.Combine(instanceDirectory, "bin");
            var previousDirectory = binDirectory + ".previous";
            Directory.CreateDirectory(binDirectory);
            Directory.CreateDirectory(previousDirectory);
            await File.WriteAllTextAsync(Path.Combine(binDirectory, ServerExecutableName), "new-server");
            await File.WriteAllTextAsync(Path.Combine(previousDirectory, ServerExecutableName), "old-server");

            Assert.That(UpdateStaging.RecoverInterruptedUpdate(binDirectory), Is.True);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(binDirectory, ServerExecutableName)), Is.EqualTo("old-server"));
            Assert.That(Directory.Exists(previousDirectory), Is.False);
        }
        finally
        {
            Directory.Delete(instanceDirectory, true);
        }
    }

    private static string ServerExecutableName => OperatingSystem.IsWindows() ? "Robust.Server.exe" : "Robust.Server";

    private static async Task<MemoryStream> CreateServerArchive(string serverContents)
    {
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var writer = new StreamWriter(zip.CreateEntry(ServerExecutableName).Open());
            await writer.WriteAsync(serverContents);
        }

        archive.Position = 0;
        return archive;
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"SS14.Watchdog.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
