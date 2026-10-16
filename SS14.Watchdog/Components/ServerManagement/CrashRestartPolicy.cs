using System;
using System.Collections.Generic;
using SS14.Watchdog.Configuration;

namespace SS14.Watchdog.Components.ServerManagement;

public sealed record RestartDecision(TimeSpan Delay, bool CoolingDown);

public sealed class CrashRestartPolicy
{
    private readonly Queue<DateTimeOffset> _failures = new();
    private bool _circuitOpen;

    public void Reset()
    {
        _failures.Clear();
        _circuitOpen = false;
    }

    public RestartDecision RecordFailure(DateTimeOffset now, TimeSpan uptime, CrashLoopConfiguration options)
    {
        if (!options.Enabled)
            return new RestartDecision(TimeSpan.Zero, false);
        if (uptime >= TimeSpan.FromSeconds(options.StableSeconds))
            Reset();
        while (_failures.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromSeconds(options.WindowSeconds))
            _failures.Dequeue();
        _failures.Enqueue(now);
        if (_circuitOpen || _failures.Count >= options.MaximumFailures)
        {
            _circuitOpen = true;
            return new RestartDecision(TimeSpan.FromSeconds(options.CooldownSeconds), true);
        }
        var delay = options.BackoffSeconds.Count == 0 ? 0 :
            options.BackoffSeconds[Math.Min(_failures.Count - 1, options.BackoffSeconds.Count - 1)];
        return new RestartDecision(TimeSpan.FromSeconds(delay), false);
    }
}
