# HeretekRT game integration

These files extend HeretekRT's `Content.Server.ServerUpdates.ServerUpdateManager`. They require its existing
`DoShutdown`, `_shutdownPreparation` and `_shutdownIssued` save-barrier implementation. RobustToolbox needs no changes.

1. Copy `ServerUpdateManager.Watchdog.cs` and `WatchdogUpdateProtocol.cs` to `Content.Server/ServerUpdates/`.
2. Add `InitializeWatchdogCountdown();` to `ServerUpdateManager`'s existing `IPostInjectInit.PostInject()` method.
3. Copy `countdown.en-US.ftl` and `countdown.ru-RU.ftl` to the matching `Resources/Locale/<locale>/server-updates/` directories.
4. Build and deploy HeretekRT once. The already running old game process cannot gain a new API without restarting.

`GET /watchdog/update` reports protocol version 1 and any pending request ID/deadline. `POST /watchdog/update` accepts
`schedule`, `announce`, `cancel` and `shutdown`. Every request requires the same `WatchdogToken` header already used by
Watchdog; an absent token never grants access. JSON request names use camel case; responses contain ProtocolVersion,
RequestId and RestartAtUtc. Bodies are limited to 4 KiB. No general remote console is exposed.

Scheduling displays a localized warning before acknowledging the request. Repeating the same request ID returns its
original deadline without repeating the warning. A competing ID is rejected. The original round-end and empty-server
paths remain enabled. Watchdog owns the countdown timer; `shutdown` invokes `DoShutdown()` and its save barrier, rather
than bypassing saves through Robust's `/shutdown`. Cancellation clears this countdown's round-end/empty-server request.
Game restarts naturally clear the in-memory request; Watchdog only restores saved countdowns for the same persisted process.

The protocol state/authentication source is compiled directly into Watchdog's tests to keep the HTTP contract consistent.
On Windows, after building HeretekRT, run the actual game HTTP smoke test:

```powershell
pwsh ./tools/test-game-countdown.ps1 -GameRepository ../HeretekRT
```

It runs a separate game process on loopback with isolated configuration/data, checks authentication, scheduling,
idempotent retries, reminders, conflicts and cancellation, then verifies shutdown through the inventory save barrier.
Logs stay in the ignored `SS14.Watchdog.Tests/bin/game-countdown-smoke` directory.
