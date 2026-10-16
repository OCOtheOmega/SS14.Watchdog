# Operational settings and deployment

All instance settings below belong under `Servers:Instances:<key>`. By default Watchdog prepares and validates a build,
asks the game to announce a ten-minute countdown, then requests a graceful update shutdown even during an active round.
Maintenance windows and player-count restrictions remain optional and disabled by default.

## Update countdown (default)

```yaml
UpdateRestart:
  Mode: Countdown
  CountdownSeconds: 600
  ShutdownTimeoutSeconds: 120
```

The game announces the initial deadline and reminders at five minutes and one minute remaining. If the configured
countdown is shorter, reminders already covered by the initial warning are skipped. The prepared version and announced
deadline remain fixed when new builds appear or players connect. A natural round end or the game's stable-empty-server
restart may install the update earlier. No warning is needed when installing before a process has started.

At the deadline Watchdog calls the game integration's shutdown action, which uses HeretekRT's existing quest/inventory
save barrier. It allows ShutdownTimeoutSeconds for the process to exit, then kills a hung process. The actor remains
responsive during this grace period. Set the timeout above your game's own save timeout, including time for quest drains.
This saves what HeretekRT already supports; Watchdog does not provide whole-world persistence.

Countdown intent, deadline and prepared-file location are saved atomically in `.watchdog-update-countdown.json` under
the instance directory. Restarting Watchdog while retaining the same game process resumes the same countdown and protects
its staging files from cleanup. Lost HTTP acknowledgements are recovered from the game without repeating the initial warning
or postponing the deadline. A stop cancels the countdown; a manual restart can accelerate it through the same save barrier.
Disk reserve is checked again at the deadline; insufficient space cancels the announced restart before stopping the game.

Countdown requires the authenticated game integration in [integrations/ss14](../integrations/ss14/README.md).
An ordinary HTTP 200 from the old `/update` route does not prove that players received a countdown warning.
Missing/invalid integration defers the update and emits an error; Watchdog does not silently interrupt an unwarned round.
Deploy the game integration once manually, or temporarily use the compatible mode:

```yaml
UpdateRestart:
  Mode: AfterRound
```

AfterRound uses the original `/update` behaviour, including the game's existing empty-server restart timer.
Explicit `/revert?immediate=false` continues to request a round-end restart; `immediate=true` remains an explicit immediate restart.

## Startup and runtime timeouts

`StartupTimeoutSeconds` defaults to 180 seconds until the first watchdog ping. `TimeoutSeconds` defaults to 90 seconds
between pings after startup. A newly installed process that fails to ping is rolled back. A timeout in a confirmed
process kills it and enters normal crash recovery. There is one active ping timer per process.

Existing `AutoUpdateHealthTimeoutSeconds` values remain supported as a startup override for updated processes.
Omit this legacy option to use `StartupTimeoutSeconds`. Its old zero/negative immediate-commit behavior is preserved;
the process still has the regular startup timeout. Pings queued by an earlier process generation are ignored.

## Optional update conditions

Both sections below are absent by default. Adding one enables that condition; adding both requires both to pass.

```yaml
MaintenanceWindow:
  TimeZone: Europe/Moscow
  Start: '04:00'
  End: '08:00'
  Days: [Monday, Tuesday, Wednesday, Thursday, Friday]
LowPopulation:
  MaximumPlayers: 5
  StableSeconds: 600
UpdatePolicyPollSeconds: 30
```

Windows use the configured time zone, include Start and exclude End. Overnight windows are supported and belong to
the day they start. An empty Days list allows every day. Start and End must differ.

Packages can be prepared outside the window. Conditions authorize starting the update countdown (or sending `/update`
in AfterRound mode). In Countdown mode the entire warning period must fit before the window closes, including overnight
and daylight-saving transitions. Once announced, rising player counts do not cancel or postpone the deadline. Actual save
completion may take the additional shutdown grace period. In AfterRound mode the accepted round may finish outside the window.
Explicit `/revert` commands bypass these automatic conditions; ordinary `/update` checks respect them.
If a process exits before an update was authorized, the previous version is restarted and the prepared build remains pending.

Low-population checks use the existing game server's `/status` players field: no SS14 changes are needed.
The count must stay at or below the threshold for StableSeconds. High, missing, invalid or unavailable counts reset
the observation and defer the countdown. No overall waiting limit secretly overrides this condition: a server that never
meets the configured population threshold will continue waiting. Omit LowPopulation when only a timed window is desired.

## Crash recovery

```yaml
CrashLoop:
  Enabled: true
  MaximumFailures: 5
  WindowSeconds: 600
  StableSeconds: 600
  CooldownSeconds: 600
  BackoffSeconds: [5, 15, 30, 60]
```

Unexpected exits and launch failures use increasing restart delays. Five failures in ten minutes open a cooldown;
after ten minutes one probe is allowed. A failed probe returns to cooldown. Ten minutes of stable process uptime,
an explicit restart, or a newly prepared replacement for a failed process reset the policy.
Intentional stops/restarts and clean exits following an accepted update are excluded. A failed update first restores
the previous build immediately. Delays run outside the instance actor, so stop/restart/update requests remain responsive.
Stopping the instance cancels pending probes; manual restart bypasses a pending delay.

## Disk reserve and cleanup

```yaml
MinimumFreeSpaceMiB: 1024
Cleanup:
  IntervalSeconds: 3600
  TemporaryMaxAgeHours: 24
  DumpMaxAgeDays: 7
  DumpMaximumMiB: 2048
```

Downloads are stored in the instance directory, on the same volume as staging. Available space is checked against
known download size, remaining write operations and the total extracted size, while keeping the configured reserve.
The reserve is checked again before notifying the running server and before swapping directories. A failed check keeps
the current files available. Renaming the old bin directory does not count as another full copy.

Cleanup runs at startup and periodically. It removes aged `.update-staging-*` directories and `.update-download-*.tmp`
files owned by Watchdog. Active downloads/extractions and the currently prepared build are protected. `bin`, `bin.previous`,
configuration and game data are excluded. Completed `dumps/dump_*` files expire by age and oldest-first size budget.
Cleanup skips symbolic links and reports filesystem failures without stopping the server.

## Discord

These are global settings, outside Servers:

```yaml
Notification:
  DiscordWebhook: 'https://discord.com/api/webhooks/WEBHOOK_ID/WEBHOOK_TOKEN'
  DiscordRoleId: '123456789012345678' # optional; omit for no ping
```

Only two kinds of message are sent: a confirmed server update and an error containing its reason, including rollback
or crash-recovery details where relevant. Both mention the configured role. Without a role there are no mentions.
Discord allowed_mentions explicitly permits only that role; user and everyone mentions in error text cannot ping.
Identical errors are suppressed for five minutes. Delivery uses a bounded background queue, limited retries and Discord
Retry-After handling. Delivery failures do not stop or block server monitoring. No webhook URL means no messages.

API tokens, authorization credentials, webhook URLs, process argument values and environment variable values are not
written to diagnostic logs. Built-in HTTP client URL logging is disabled for the authenticated clients. Error messages
are sanitized before logging or notifying. Avoid adding secrets to custom log templates.

## Docker / Compose

The image targets linux/amd64 and runs as the unprivileged app user. Its pinned .NET SDK also supports the Git update
provider. All persistent Watchdog/server files live under `/data`; container shutdown stops child game servers.

1. Copy `.env.example` to `.env` and set a long random WATCHDOG_API_TOKEN. Optionally set DISCORD_WEBHOOK_URL and DISCORD_ROLE_ID.
2. Copy/edit `deploy/appsettings.example.yml` and `deploy/server.example.toml`, adjusting paths in `.env` if needed.
3. Run `docker compose build` and `docker compose up -d`, or set WATCHDOG_IMAGE and use `docker compose pull` followed by `docker compose up -d --no-build`.
4. Inspect `docker compose logs -f` and use `docker compose down` to stop. The named volume preserves game data.

Watchdog's API is bound to localhost on the host; game TCP/UDP port 1212 is published. The example does not advertise
the game server to the hub. Adjust game ports and hub settings for your deployment. Environment/command-line settings
override YAML, including the API token supplied by Compose. Keep `.env` and appsettings.Secret.yml out of Git.

The **Manual container build** GitHub workflow runs only on demand. It builds and smoke-tests one image, optionally
publishes it to GHCR, and assigns an immutable `sha-COMMIT` tag plus the selected tag. A published commit is reused
on subsequent publication requests. Publishing is controlled by the `publish` input; false tests the image without publishing.
There is no automatic image publication on push or pull request.

If master is still the fork's default branch, use the existing **Release** workflow with branch `dev` and tag
`container` to publish, or `container-test` to build/smoke-test without publication. These reserved values delegate to
the same container workflow and allow manual runs before a new workflow file exists on the default branch.

## CI, Nix and fixed builds

Pushes and pull requests for master/dev restore locked dependencies, build Release and run tests on Windows and Linux.
The Nix job builds the package from its pinned nixpkgs revision and starts the packaged Watchdog against an empty-instance
configuration. Docker builds remain manual.

`global.json` pins .NET SDK 10.0.401. Commit both packages.lock.json files and use `dotnet restore --locked-mode`.
After an intentional dependency update, run `dotnet restore --force-evaluate`, review lock-file changes, and regenerate
Nix archives with `pwsh ./tools/update-nix-deps.ps1`. The script hashes restored nupkg files, including runtime packs.
These pins improve repeatability; package repository updates during Docker's OS-package installation can still change its layers.

Nix now uses .NET 10 from a pinned nixos-26.05 revision, with updated NuGet archive hashes. Build with `nix build`,
run with `nix run`, or use the NixOS module. Update the SDK and nixpkgs pins together when upgrading .NET.
