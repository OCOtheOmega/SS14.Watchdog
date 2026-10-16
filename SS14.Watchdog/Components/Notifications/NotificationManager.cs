using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SS14.Watchdog.Utility;

namespace SS14.Watchdog.Components.Notifications;

/// <summary>
/// Implements external notifications to various services.
/// </summary>
/// <seealso cref="NotificationOptions"/>
public sealed partial class NotificationManager(
    IHttpClientFactory http,
    ILogger<NotificationManager> logger,
    IOptions<NotificationOptions> options) : BackgroundService
{
    public const string DiscordHttpClient = "discord_webhook";

    private readonly HttpClient _httpClient = http.CreateClient(DiscordHttpClient);
    private readonly Channel<string> _messages = Channel.CreateBounded<string>(128);
    private readonly Dictionary<string, DateTimeOffset> _recentErrors = new();
    private readonly object _errorLock = new();

    public void SendUpdated(string server, string previousVersion, string version) =>
        Enqueue($"Server `{server}` updated: `{previousVersion}` → `{version}`.");

    public void SendError(string server, string reason)
    {
        var message = $"Server `{server}` error: {SensitiveData.Redact(reason)}";
        lock (_errorLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_recentErrors.TryGetValue(message, out var previous) && now - previous < TimeSpan.FromMinutes(5))
                return;
            if (_recentErrors.Count >= 1024)
                _recentErrors.Clear();
            _recentErrors[message] = now;
        }
        Enqueue(message);
    }

    private void Enqueue(string message)
    {
        var optionsValue = options.Value;
        if (string.IsNullOrWhiteSpace(optionsValue.DiscordWebhook))
            return;
        SensitiveData.Register(optionsValue.DiscordWebhook);
        if (!_messages.Writer.TryWrite(SensitiveData.Redact(message)))
            logger.LogWarning("Discord notification queue is full; dropping a notification.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in _messages.Reader.ReadAllAsync(stoppingToken))
            await SendWebhookAsync(options.Value, message, stoppingToken);
    }

    private async Task SendWebhookAsync(NotificationOptions configuration, string message, CancellationToken cancel)
    {
        var role = configuration.DiscordRoleId;
        var roles = !string.IsNullOrWhiteSpace(role) && ulong.TryParse(role, NumberStyles.None,
            CultureInfo.InvariantCulture, out var id) && id != 0 ? new[] { role } : Array.Empty<string>();
        var prefix = roles.Length == 0 ? "" : $"<@&{role}> ";
        var content = prefix + message;
        if (content.Length > 2000)
            content = content[..1999] + "…";
        var payload = new DiscordWebhookExecute
        {
            Content = content,
            AllowedMentions = new DiscordAllowedMentions { Parse = [], Roles = roles }
        };
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.PostAsJsonAsync(configuration.DiscordWebhook, payload,
                        DiscordSourceGenerationContext.Default.DiscordWebhookExecute, timeout.Token);
                }
                catch (Exception e) when ((e is HttpRequestException or OperationCanceledException) &&
                                          !cancel.IsCancellationRequested && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancel);
                    continue;
                }
                using var responseLease = response;
                if (response.IsSuccessStatusCode)
                    return;
                if (response.StatusCode != HttpStatusCode.TooManyRequests && (int) response.StatusCode < 500)
                {
                    logger.LogWarning("Discord rejected a notification with HTTP {Status}.", (int) response.StatusCode);
                    return;
                }
                if (attempt < 2)
                {
                    var delay = response.Headers.RetryAfter?.Delta ??
                                (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ??
                                TimeSpan.FromSeconds(attempt + 1);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        // Discord also supplies fractional retry_after seconds in the JSON body.
                        try
                        {
                            var body = await response.Content.ReadFromJsonAsync(DiscordSourceGenerationContext.Default.RetryResponse, timeout.Token);
                            if (body is { RetryAfter: >= 0 })
                                delay = TimeSpan.FromSeconds(Math.Min(30, body.RetryAfter.Value));
                        }
                        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException) { }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0, 30)), cancel);
                }
            }
            logger.LogWarning("Discord notification delivery failed after three attempts.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("Discord notification delivery failed ({Type}).", e.GetType().Name);
        }
    }

    private sealed class DiscordWebhookExecute
    {
        public string? Content { get; set; }
        public DiscordAllowedMentions? AllowedMentions { get; set; } = null;
    }

    private sealed class DiscordAllowedMentions
    {
        public string[]? Parse { get; set; }
        public string[]? Roles { get; set; }
    }

    private sealed class RetryResponse
    {
        [JsonPropertyName("retry_after")] public double? RetryAfter { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(DiscordWebhookExecute))]
    [JsonSerializable(typeof(RetryResponse))]
    private partial class DiscordSourceGenerationContext : JsonSerializerContext;
}
