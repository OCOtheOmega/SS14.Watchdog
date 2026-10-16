using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Robust.Server.ServerStatus;
using Robust.Shared;
using Robust.Shared.Asynchronous;

namespace Content.Server.ServerUpdates;

public sealed partial class ServerUpdateManager
{
    [Dependency] private IStatusHost _watchdogStatusHost = default!;
    [Dependency] private ITaskManager _watchdogTasks = default!;
    private readonly WatchdogUpdateSchedule _watchdogSchedule = new();
    private bool _roundEndBeforeCountdown;

    private void InitializeWatchdogCountdown() => _watchdogStatusHost.AddHandler(WatchdogCountdownHandler);

    private async Task<bool> WatchdogCountdownHandler(IStatusHandlerContext context)
    {
        if (context.Url.AbsolutePath != "/watchdog/update" ||
            context.RequestMethod != HttpMethod.Get && context.RequestMethod != HttpMethod.Post)
            return false;
        context.RequestHeaders.TryGetValue("WatchdogToken", out var supplied);
        if (!WatchdogUpdateSchedule.Authenticate(supplied.ToString(), _cfg.GetCVar(CVars.WatchdogToken)))
        {
            await context.RespondErrorAsync(HttpStatusCode.Unauthorized);
            return true;
        }
        WatchdogUpdateRequest? request = null;
        if (context.RequestMethod == HttpMethod.Post)
        {
            try
            {
                // Bound the body before deserializing; no unrestricted remote console is exposed.
                using var body = new System.IO.MemoryStream();
                var buffer = new byte[1024];
                int read;
                while ((read = await context.RequestBody.ReadAsync(buffer)) != 0)
                {
                    if (body.Length + read > 4096)
                    {
                        await context.RespondErrorAsync(HttpStatusCode.RequestEntityTooLarge);
                        return true;
                    }
                    body.Write(buffer, 0, read);
                }
                request = JsonSerializer.Deserialize<WatchdogUpdateRequest>(body.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                await context.RespondErrorAsync(HttpStatusCode.BadRequest);
                return true;
            }
            if (request == null || request.RequestId == Guid.Empty)
            {
                await context.RespondErrorAsync(HttpStatusCode.BadRequest);
                return true;
            }
        }

        var completion = new TaskCompletionSource<(WatchdogUpdateResponse? Reply, HttpStatusCode Status)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watchdogTasks.RunOnMainThread(() =>
        {
            try { completion.TrySetResult(ProcessWatchdogCountdown(request)); }
            catch (Exception e) { completion.TrySetException(e); }
        });
        var result = await completion.Task;
        if (result.Reply == null)
            await context.RespondErrorAsync(result.Status);
        else
            await context.RespondJsonAsync(result.Reply, result.Status);
        return true;
    }

    private (WatchdogUpdateResponse?, HttpStatusCode) ProcessWatchdogCountdown(WatchdogUpdateRequest? request)
    {
        if (request == null)
            return (_watchdogSchedule.Snapshot(), HttpStatusCode.OK);
        if (_shutdownPreparation != null || _shutdownIssued)
            return (null, HttpStatusCode.Conflict);

        switch (request.Action)
        {
            case "schedule":
                if (!_watchdogSchedule.TrySchedule(request, DateTimeOffset.UtcNow, out var created))
                    return (null, HttpStatusCode.Conflict);
                if (created)
                {
                    _roundEndBeforeCountdown = _updateOnRoundEnd;
                    AnnounceWatchdogCountdown();
                    _updateOnRoundEnd = true;
                    ServerEmptyUpdateRestartCheck("watchdog countdown");
                }
                return (_watchdogSchedule.Snapshot(), HttpStatusCode.OK);
            case "announce" when _watchdogSchedule.RequestId == request.RequestId:
                AnnounceWatchdogCountdown();
                return (_watchdogSchedule.Snapshot(), HttpStatusCode.OK);
            case "shutdown" when _watchdogSchedule.RequestId == request.RequestId:
                // This uses HeretekRT's inventory/quest shutdown barrier before IBaseServer.Shutdown.
                DoShutdown();
                return (_watchdogSchedule.Snapshot(), HttpStatusCode.OK);
            case "cancel":
                var reply = new WatchdogUpdateResponse(1, request.RequestId, _watchdogSchedule.RestartAtUtc ?? DateTimeOffset.UtcNow);
                var hadSchedule = _watchdogSchedule.RequestId != null;
                if (!_watchdogSchedule.Cancel(request.RequestId))
                    return (null, HttpStatusCode.Conflict);
                if (hadSchedule)
                {
                    _updateOnRoundEnd = _roundEndBeforeCountdown;
                    _restartTime = null;
                    _chatManager.DispatchServerAnnouncement(Loc.GetString("server-updates-countdown-cancelled"));
                }
                return (reply, HttpStatusCode.OK);
            default:
                return (null, HttpStatusCode.BadRequest);
        }
    }

    private void AnnounceWatchdogCountdown() => _chatManager.DispatchServerAnnouncement(
        Loc.GetString("server-updates-countdown", ("seconds", _watchdogSchedule.RemainingSeconds(DateTimeOffset.UtcNow))));
}
