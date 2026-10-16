# SS14.Watchdog 

SS14.Watchdog is SS14's server-hosting wrapper thing, similar to [TGS](https://github.com/tgstation/tgstation-server) for BYOND (but much simpler for the time being). It handles auto updates, monitoring, automatic restarts, and administration. We recommend you use this for proper deployments.

Documentation on how setup and use for SS14.Watchdog is [here](https://docs.spacestation14.com/en/server-hosting/setting-up-ss14-watchdog.html).

## Automatic updates in this fork

The `dev` branch adds periodic update checks to upstream's server management and manifest version selection API.
Configure these options per instance under `Servers:Instances:<key>`:

```yaml
AutoUpdateChecks: true
AutoUpdateCheckIntervalSeconds: 300
StartupTimeoutSeconds: 180
TimeoutSeconds: 90
UpdateRestart:
  Mode: Countdown
  CountdownSeconds: 600
  ShutdownTimeoutSeconds: 120
AutoUpdateMaximumDownloadMiB: 4096
AutoUpdateMaximumExtractedMiB: 8192
```

Setting `AutoUpdateChecks` to `false`, or the check interval to zero or less, disables periodic checks.
An explicit `POST /instances/<key>/update` still checks for updates. A recovered persisted server is also checked on startup.

Downloaded updates are extracted and validated in a separate staging directory before the running server is notified.
By default the game announces a ten-minute countdown with five-minute and one-minute reminders, then restarts for the
update even during an active round, using HeretekRT's graceful save barrier. This requires the
[game integration](integrations/ss14/README.md); without it updates are deferred with an error. Select
`UpdateRestart.Mode: AfterRound` to retain compatibility with unmodified SS14 servers.
Manifest URLs and archive URLs must use HTTPS, and manifest archives must match their SHA-256 checksum.
The prepared files replace `bin` when the game server exits. Its previous files are kept as `bin.previous` until the
new process sends its first watchdog ping. A launch failure, exit before that ping, or health-check timeout restores
the previous files and starts the previous version. Rejected versions are skipped for the rest of the Watchdog session;
an explicit manifest revert can retry one. The runtime ping timeout starts after the first ping.
The legacy `AutoUpdateHealthTimeoutSeconds` setting overrides the startup timeout for updated builds;
zero or less retains its old behavior of committing immediately.

## Selecting a manifest version

Upstream's authenticated endpoints are available for Manifest instances:

```sh
curl -u myInstance:ApiToken http://localhost:5000/instances/myInstance/versions
curl -X POST -u myInstance:ApiToken 'http://localhost:5000/instances/myInstance/revert'
curl -X POST -u myInstance:ApiToken 'http://localhost:5000/instances/myInstance/revert?version=BUILD_VERSION&immediate=true'
```

`versions` lists the five most recent builds. Omitting `version` selects the build immediately before the current one;
an explicit version can be older or newer. The target is downloaded and validated before any shutdown request.
By default the running server is notified to exit at the end of its round; `immediate=true` requests an immediate restart.
Both modes use the same staging, first-ping confirmation, and automatic rollback as regular updates.

A manually selected version is held until an explicit `/update` request or a Watchdog restart. Periodic checks retry a
failed shutdown notification for the selected build, but do not switch back to the latest manifest version. An explicit
update received during first-ping verification is deferred until that verification finishes.

See [operational settings and deployment](docs/operations.md) for optional maintenance windows, low-population updates,
crash recovery, disk management, Discord notifications, Docker, Nix and reproducible builds.
