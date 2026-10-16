#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using SS14.Watchdog.Components.BackgroundTasks;
using SS14.Watchdog.Components.DataManagement;
using SS14.Watchdog.Components.Notifications;
using SS14.Watchdog.Components.ProcessManagement;
using SS14.Watchdog.Components.ServerManagement;
using SS14.Watchdog.Configuration;

namespace SS14.Watchdog.Tests.ServerManagement;

public sealed class ServerInstanceUpdateTest
{
    [Test]
    public async Task RecoveredServerChecksForUpdateImmediately()
    {
        using var cancellation = new CancellationTokenSource();
        var taskQueue = new RecordingTaskQueue(cancellation.Token);
        await using var fixture = CreateFixture(
            taskQueue,
            new SequenceResponseHandler(HttpStatusCode.OK),
            autoUpdateChecks: false);

        var instanceTask = fixture.Instance.StartAsync("http://127.0.0.1:5000", cancellation.Token);
        try
        {
            await taskQueue.FirstTaskQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(taskQueue.QueuedCount, Is.EqualTo(1));
        }
        finally
        {
            await StopInstance(cancellation, instanceTask);
        }
    }

    [Test]
    public async Task PeriodicUpdateTimerRetriesFailedNotificationWithoutPreparingAgain()
    {
        using var cancellation = new CancellationTokenSource();
        var taskQueue = new RecordingTaskQueue(cancellation.Token);
        var responses = new SequenceResponseHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        await using var fixture = CreateFixture(
            taskQueue,
            responses,
            autoUpdateChecks: true,
            updateIntervalSeconds: 1);

        var instanceTask = fixture.Instance.StartAsync("http://127.0.0.1:5000", cancellation.Token);
        try
        {
            await responses.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(responses.RequestCount, Is.EqualTo(2));
                Assert.That(taskQueue.QueuedCount, Is.EqualTo(1),
                    "The already prepared update should be reused instead of downloaded again.");
            });
        }
        finally
        {
            await StopInstance(cancellation, instanceTask);
        }
    }

    private static Fixture CreateFixture(
        RecordingTaskQueue taskQueue,
        HttpMessageHandler responseHandler,
        bool autoUpdateChecks,
        int updateIntervalSeconds = 300)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"SS14.Watchdog.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        var dataManager = new DataManager(
            NullLogger<DataManager>.Instance,
            Options.Create(new DataOptions { File = Path.Combine(temporaryDirectory, "watchdog.db") }));

        using (var connection = dataManager.OpenConnection())
        {
            connection.Execute("""
                CREATE TABLE ServerInstance(
                    Key TEXT NOT NULL PRIMARY KEY,
                    Revision TEXT NULL,
                    PersistedToken TEXT NULL,
                    PersistedPid INTEGER NULL,
                    PersistedSystemdUnit TEXT NULL
                );
                INSERT INTO ServerInstance(Key, Revision, PersistedToken)
                VALUES ('test', '1', 'persisted-token');
                """);
        }

        var instanceConfiguration = new InstanceConfiguration
        {
            Name = "Test",
            UpdateType = "Dummy",
            ApiPort = 1212,
            UpdateRestart = new() { Mode = UpdateRestartMode.AfterRound },
            TimeoutSeconds = 3600,
            AutoUpdateChecks = autoUpdateChecks,
            AutoUpdateCheckIntervalSeconds = updateIntervalSeconds
        };
        var serversConfiguration = new ServersConfiguration
        {
            InstanceRoot = temporaryDirectory,
            Instances = { ["test"] = instanceConfiguration }
        };

        var notificationHttpFactory = new Mock<IHttpClientFactory>();
        notificationHttpFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new SequenceResponseHandler(HttpStatusCode.OK)));
        var notificationManager = new NotificationManager(
            notificationHttpFactory.Object,
            NullLogger<NotificationManager>.Instance,
            Options.Create(new NotificationOptions()));

        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var serverHttpClient = new HttpClient(responseHandler);
        var instance = new ServerInstance(
            "test",
            instanceConfiguration,
            new ConfigurationBuilder().Build(),
            serversConfiguration,
            NullLogger<ServerInstance>.Instance,
            taskQueue,
            serviceProvider,
            dataManager,
            new PersistedProcessManager(),
            notificationManager,
            serverHttpClient);

        return new Fixture(instance, temporaryDirectory, serviceProvider, serverHttpClient);
    }

    private static async Task StopInstance(CancellationTokenSource cancellation, Task instanceTask)
    {
        await cancellation.CancelAsync();
        await instanceTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class RecordingTaskQueue(CancellationToken cancellation) : IBackgroundTaskQueue
    {
        private int _queuedCount;

        public int QueuedCount => Volatile.Read(ref _queuedCount);
        public TaskCompletionSource FirstTaskQueued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void QueueTask(Func<CancellationToken, Task> task)
        {
            Interlocked.Increment(ref _queuedCount);
            FirstTaskQueued.TrySetResult();
            _ = Task.Run(async () =>
            {
                try
                {
                    await task(cancellation);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // Expected during test teardown.
                }
            }, CancellationToken.None);
        }
    }

    private sealed class PersistedProcessManager : IProcessManager
    {
        public bool CanPersist => true;

        public Task<IProcessHandle> StartServer(IServerInstance instance, ProcessStartData data,
            CancellationToken cancel = default)
        {
            throw new AssertionException("A recovered process must not be started again.");
        }

        public Task<IProcessHandle?> TryGetPersistedServer(IServerInstance instance, string program,
            CancellationToken cancel)
        {
            return Task.FromResult<IProcessHandle?>(new PersistedProcessHandle());
        }
    }

    private sealed class PersistedProcessHandle : IProcessHandle
    {
        public void DumpProcess(string file, Microsoft.Diagnostics.NETCore.Client.DumpType type)
        {
        }

        public Task WaitForExitAsync(CancellationToken cancel = default)
        {
            return Task.Delay(Timeout.InfiniteTimeSpan, cancel);
        }

        public Task Kill()
        {
            return Task.CompletedTask;
        }

        public Task<ProcessExitStatus?> GetExitStatusAsync()
        {
            return Task.FromResult<ProcessExitStatus?>(null);
        }
    }

    private sealed class SequenceResponseHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public TaskCompletionSource SecondRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber == 2)
                SecondRequest.TrySetResult();

            var status = statuses[Math.Min(requestNumber - 1, statuses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class Fixture(
        ServerInstance instance,
        string temporaryDirectory,
        ServiceProvider serviceProvider,
        HttpClient serverHttpClient) : IAsyncDisposable
    {
        public ServerInstance Instance { get; } = instance;

        public ValueTask DisposeAsync()
        {
            serverHttpClient.Dispose();
            serviceProvider.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(temporaryDirectory, true);
            return ValueTask.CompletedTask;
        }
    }
}
