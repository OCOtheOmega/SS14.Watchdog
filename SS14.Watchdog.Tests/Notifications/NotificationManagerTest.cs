using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using SS14.Watchdog.Components.Notifications;

namespace SS14.Watchdog.Tests.Notifications;

public sealed class NotificationManagerTest
{
    [TestCase(null)]
    [TestCase("123456789012345678")]
    public async Task OnlyTheConfiguredRoleCanBeMentionedInUpdatesAndErrors(string role)
    {
        using var handler = new WebhookHandler();
        using var client = new HttpClient(handler);
        using var manager = Create(client, role);
        await manager.StartAsync(CancellationToken.None);
        try
        {
            manager.SendUpdated("main", "old", "new");
            manager.SendError("main", "Map load failed @everyone <@999999999999999999>");
            foreach (var kind in new[] { "updated", "error" })
            {
                using var json = JsonDocument.Parse(await handler.NextMessage());
                var root = json.RootElement;
                var text = root.GetProperty("content").GetString();
                Assert.That(text, Does.Contain(kind));
                Assert.That(root.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength(), Is.Zero);
                var roles = root.GetProperty("allowed_mentions").GetProperty("roles");
                Assert.That(roles.GetArrayLength(), Is.EqualTo(role == null ? 0 : 1));
                if (role != null)
                {
                    Assert.That(roles[0].GetString(), Is.EqualTo(role));
                    Assert.That(text, Does.StartWith($"<@&{role}> "));
                }
                else
                    Assert.That(text, Does.Not.Contain("<@&"));
            }
        }
        finally { await manager.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task RateLimitedWebhookIsRetriedAndDuplicateErrorsAreSuppressed()
    {
        using var handler = new WebhookHandler { RateLimitFirst = true };
        using var client = new HttpClient(handler);
        using var manager = Create(client, null);
        await manager.StartAsync(CancellationToken.None);
        try
        {
            manager.SendError("main", "Disk is full");
            manager.SendError("main", "Disk is full");
            var first = await handler.NextMessage();
            Assert.That(await handler.NextMessage(), Is.EqualTo(first));
            manager.SendUpdated("main", "1", "2");
            Assert.That(await handler.NextMessage(), Does.Contain("updated"));
            Assert.That(handler.Requests, Is.EqualTo(3));
        }
        finally { await manager.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task TransientNetworkFailureDoesNotDropTheNotification()
    {
        using var handler = new WebhookHandler { FailFirst = true };
        using var client = new HttpClient(handler);
        using var manager = Create(client, null);
        await manager.StartAsync(CancellationToken.None);
        try
        {
            manager.SendError("main", "Network test");
            await handler.NextMessage();
            await handler.NextMessage();
            Assert.That(handler.Requests, Is.EqualTo(2));
        }
        finally { await manager.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task LongErrorsAreBoundedAndWebhookSecretsAreRedacted()
    {
        using var handler = new WebhookHandler();
        using var client = new HttpClient(handler);
        using var manager = Create(client, "123456789012345678");
        await manager.StartAsync(CancellationToken.None);
        try
        {
            manager.SendError("main", "Failed https://discord.com/api/webhooks/secret-token " + new string('x', 4000));
            using var json = JsonDocument.Parse(await handler.NextMessage());
            var text = json.RootElement.GetProperty("content").GetString()!;
            Assert.That(text.Length, Is.LessThanOrEqualTo(2000));
            Assert.That(text, Does.Not.Contain("secret-token"));
        }
        finally { await manager.StopAsync(CancellationToken.None); }
    }

    [TestCase("https://discord.com/api/webhooks/id/token", "123", true)]
    [TestCase(null, null, true)]
    [TestCase("http://discord.com/api/webhooks/id/token", null, false)]
    [TestCase("https://discord.com/api/webhooks/id/token", "@everyone", false)]
    [TestCase("https://discord.com/api/webhooks/id/token", "0", false)]
    public void InvalidWebhookAndRoleConfigurationIsRejected(string webhook, string role, bool valid)
        => Assert.That(new NotificationOptions { DiscordWebhook = webhook, DiscordRoleId = role }.IsValid(), Is.EqualTo(valid));

    private static NotificationManager Create(HttpClient client, string role)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(NotificationManager.DiscordHttpClient)).Returns(client);
        return new NotificationManager(factory.Object, NullLogger<NotificationManager>.Instance,
            Options.Create(new NotificationOptions { DiscordWebhook = "https://discord.com/api/webhooks/id/token", DiscordRoleId = role }));
    }

    private sealed class WebhookHandler : HttpMessageHandler
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        public int Requests;
        public bool RateLimitFirst;
        public bool FailFirst;
        public Task<string> NextMessage() => _messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var number = Interlocked.Increment(ref Requests);
            await _messages.Writer.WriteAsync(await request.Content!.ReadAsStringAsync(cancel), cancel);
            if (number == 1 && FailFirst)
                throw new HttpRequestException("Simulated failure with https://discord.com/api/webhooks/id/token");
            if (number == 1 && RateLimitFirst)
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) },
                    Content = new StringContent("{\"retry_after\":0}")
                };
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
