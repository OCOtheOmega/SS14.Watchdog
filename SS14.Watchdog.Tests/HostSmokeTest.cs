using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using SS14.Watchdog.Components.DataManagement;
using SS14.Watchdog.Components.ProcessManagement;
using SS14.Watchdog.Components.ServerManagement;

namespace SS14.Watchdog.Tests;

[NonParallelizable]
public sealed class HostSmokeTest
{
    [Test]
    public void EnvironmentAndCommandLineOverrideYamlForContainerConfiguration()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        const string variable = "Servers__Instances__main__ApiToken";
        var previous = Environment.GetEnvironmentVariable(variable);
        var directory = Path.Combine(Path.GetTempPath(), $"SS14.Watchdog.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "appsettings.yml"), "Servers:\n  Instances:\n    main:\n      ApiToken: yaml-value\n");
            Directory.SetCurrentDirectory(directory);
            Environment.SetEnvironmentVariable(variable, "environment-value");
            using var environmentHost = Program.CreateHostBuilder([]).Build();
            Assert.That(environmentHost.Services.GetRequiredService<IConfiguration>()["Servers:Instances:main:ApiToken"], Is.EqualTo("environment-value"));
            using var commandHost = Program.CreateHostBuilder(["--Servers:Instances:main:ApiToken", "command-value"]).Build();
            Assert.That(commandHost.Services.GetRequiredService<IConfiguration>()["Servers:Instances:main:ApiToken"], Is.EqualTo("command-value"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            Directory.SetCurrentDirectory(originalDirectory);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task CompleteHostStartsWithBackgroundServicesAndAcceptsHttpRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"SS14.Watchdog.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var host = Program.CreateHostBuilder([]).ConfigureAppConfiguration((_, builder) =>
            {
                builder.Sources.Clear();
                builder.AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["urls"] = "http://127.0.0.1:0",
                    ["Data:File"] = Path.Combine(directory, "data.db"),
                    ["Servers:InstanceRoot"] = Path.Combine(directory, "instances")
                });
            }).Build();
            await host.StartAsync();
            try
            {
                var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                using var client = new HttpClient { BaseAddress = new Uri(System.Linq.Enumerable.First(addresses.Addresses)) };
                var response = await client.GetAsync("/watchdog/padlock");
                Assert.That(response.IsSuccessStatusCode, Is.True);
                using var connection = host.Services.GetRequiredService<DataManager>().OpenConnection();
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ServerInstance'"), Is.EqualTo(1));
            }
            finally { await host.StopAsync(); }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Test]
    public async Task NativeProcessLaunchDoesNotLogArgumentOrEnvironmentValues()
    {
        const string secret = "native-process-test-secret";
        var messages = new List<string>();
        var logger = new CaptureLogger<ProcessManagerBasic>(messages);
        var data = new DataManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<DataManager>.Instance,
            Options.Create(new DataOptions()));
        var manager = new ProcessManagerBasic(Options.Create(new ProcessOptions()), logger, data);
        var instance = new Mock<IServerInstance>();
        instance.SetupGet(i => i.Key).Returns("test");
        var process = await manager.StartServer(instance.Object,
            new ProcessStartData("dotnet", Path.GetTempPath(), ["--version"], [("DB_PASSWORD", secret)]), CancellationToken.None);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That((await process.GetExitStatusAsync())!.IsClean, Is.True);
        Assert.That(string.Join("\n", messages), Does.Not.Contain(secret).And.Not.Contain("--version"));
    }

    private sealed class CaptureLogger<T>(List<string> messages) : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => messages.Add(formatter(state, exception));
    }
}
