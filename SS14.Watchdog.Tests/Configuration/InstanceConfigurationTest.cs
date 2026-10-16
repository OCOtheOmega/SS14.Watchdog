using NUnit.Framework;
using SS14.Watchdog.Configuration;

namespace SS14.Watchdog.Tests.Configuration;

public sealed class InstanceConfigurationTest
{
    [Test]
    public void AutoUpdateChecksAreEnabledByDefault()
    {
        var configuration = new InstanceConfiguration();

        Assert.That(configuration.AutoUpdateChecks, Is.True);
        Assert.That(configuration.AutoUpdateCheckIntervalSeconds, Is.EqualTo(300));
        Assert.That(configuration.AutoUpdateHealthTimeoutSeconds, Is.Null);
        Assert.That(configuration.StartupTimeoutSeconds, Is.EqualTo(180));
        Assert.That(configuration.UpdateRestart.Mode, Is.EqualTo(UpdateRestartMode.Countdown));
        Assert.That(configuration.UpdateRestart.CountdownSeconds, Is.EqualTo(600));
        Assert.That(configuration.UpdateRestart.ShutdownTimeoutSeconds, Is.EqualTo(120));
        Assert.That(configuration.AutoUpdateMaximumDownloadMiB, Is.EqualTo(4096));
        Assert.That(configuration.AutoUpdateMaximumExtractedMiB, Is.EqualTo(8192));
    }
}
