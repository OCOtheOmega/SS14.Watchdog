#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace Content.Server.ServerUpdates;

// Shared, engine-independent contract. All schedule mutations run on the game thread.
public sealed record WatchdogUpdateRequest(string Action, Guid RequestId, DateTimeOffset? RestartAtUtc = null);
public sealed record WatchdogUpdateResponse(int ProtocolVersion, Guid? RequestId, DateTimeOffset? RestartAtUtc);

public sealed class WatchdogUpdateSchedule
{
    public Guid? RequestId { get; private set; }
    public DateTimeOffset? RestartAtUtc { get; private set; }

    public static bool Authenticate(string? supplied, string? expected) =>
        !string.IsNullOrEmpty(supplied) && !string.IsNullOrEmpty(expected) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));

    public WatchdogUpdateResponse Snapshot() => new(1, RequestId, RestartAtUtc);

    public bool TrySchedule(WatchdogUpdateRequest request, DateTimeOffset now, out bool created)
    {
        created = false;
        if (request.RequestId == Guid.Empty)
            return false;
        if (RequestId != null)
            return RequestId == request.RequestId; // Retried requests cannot postpone the announced deadline.
        if (request.RestartAtUtc is not { } deadline || deadline <= now || deadline - now > TimeSpan.FromDays(1))
            return false;
        RequestId = request.RequestId;
        RestartAtUtc = deadline;
        created = true;
        return true;
    }

    public bool Cancel(Guid requestId)
    {
        if (RequestId != null && RequestId != requestId)
            return false;
        RequestId = null;
        RestartAtUtc = null;
        return true;
    }

    public int RemainingSeconds(DateTimeOffset now) => RestartAtUtc == null ? 0 :
        Math.Max(0, (int) Math.Ceiling((RestartAtUtc.Value - now).TotalSeconds));
}
