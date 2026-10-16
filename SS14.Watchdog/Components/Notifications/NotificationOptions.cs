namespace SS14.Watchdog.Components.Notifications;

/// <summary>
/// Options for notifications the watchdog can send via various channels.
/// </summary>
/// <seealso cref="NotificationManager"/>
public sealed class NotificationOptions
{
    public const string Position = "Notification";

    /// <summary>
    /// A Discord webhook URL to send notifications like server crashes to.
    /// </summary>
    public string? DiscordWebhook { get; set; }
    public string? DiscordRoleId { get; set; }

    public bool IsValid() =>
        (string.IsNullOrWhiteSpace(DiscordWebhook) ||
         System.Uri.TryCreate(DiscordWebhook, System.UriKind.Absolute, out var uri) && uri.Scheme == "https") &&
        (string.IsNullOrWhiteSpace(DiscordRoleId) ||
         ulong.TryParse(DiscordRoleId, System.Globalization.NumberStyles.None,
             System.Globalization.CultureInfo.InvariantCulture, out var role) && role != 0);
}
