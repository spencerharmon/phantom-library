using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public sealed class PluginConfigurationTests
{
    [Fact]
    public void SourceValidationDefaults_MatchSv14Plan()
    {
        var cfg = new PluginConfiguration();

        Assert.Equal(2, cfg.SourceValidationParallelism);
        Assert.Equal(4, cfg.SourceValidationWindowSize);
        Assert.Equal(45, cfg.SourceValidationTimeoutSeconds);
        Assert.Equal(8, cfg.SourceValidationDetailsBudgetSeconds);
        Assert.Equal(168, cfg.SourceValidationTtlHours);
        Assert.Equal(30, cfg.SourceValidationTransientRetryMinutes);
        Assert.Equal(10, cfg.SourceValidationLeaseMinutes);
        Assert.Equal(30, cfg.BulkMaterialiseRunningStaleMinutes);
        Assert.Equal(2, cfg.BulkMaterialiseWorkerCount);
        Assert.Equal(5, cfg.BulkMaterialiseMaxAttempts);
        Assert.Equal("sv14-parser-audio-v1", cfg.SourceValidationPolicyVersion);
        Assert.Equal(2, cfg.GostreamHeavyConcurrency);
        Assert.Equal(string.Empty, cfg.GostreamToken);
    }

    [Fact]
    public void SourceValidationBounds_AreClamped()
    {
        var cfg = new PluginConfiguration
        {
            SourceValidationParallelism = 0,
            SourceValidationWindowSize = 0,
            SourceValidationTimeoutSeconds = 0,
            SourceValidationDetailsBudgetSeconds = 0,
            SourceValidationTtlHours = 0,
            SourceValidationTransientRetryMinutes = 0,
            SourceValidationLeaseMinutes = 0,
            BulkMaterialiseRunningStaleMinutes = 0,
            BulkMaterialiseWorkerCount = 0,
            BulkMaterialiseMaxAttempts = 0,
            SourceValidationPolicyVersion = "",
            GostreamHeavyConcurrency = 0,
        };

        Assert.Equal(1, cfg.SourceValidationParallelism);
        Assert.Equal(1, cfg.SourceValidationWindowSize);
        Assert.Equal(5, cfg.SourceValidationTimeoutSeconds);
        Assert.Equal(1, cfg.SourceValidationDetailsBudgetSeconds);
        Assert.Equal(1, cfg.SourceValidationTtlHours);
        Assert.Equal(1, cfg.SourceValidationTransientRetryMinutes);
        Assert.Equal(1, cfg.SourceValidationLeaseMinutes);
        Assert.Equal(1, cfg.BulkMaterialiseRunningStaleMinutes);
        Assert.Equal(1, cfg.BulkMaterialiseWorkerCount);
        Assert.Equal(1, cfg.BulkMaterialiseMaxAttempts);
        Assert.Equal("sv14-parser-audio-v1", cfg.SourceValidationPolicyVersion);
        Assert.Equal(1, cfg.GostreamHeavyConcurrency);

        cfg.SourceValidationParallelism = 99;
        cfg.SourceValidationWindowSize = 99;
        cfg.SourceValidationTimeoutSeconds = 999;
        cfg.SourceValidationDetailsBudgetSeconds = 99;
        cfg.SourceValidationTtlHours = 9999;
        cfg.SourceValidationTransientRetryMinutes = 9999;
        cfg.SourceValidationLeaseMinutes = 999;
        cfg.BulkMaterialiseRunningStaleMinutes = 9999;
        cfg.BulkMaterialiseWorkerCount = 99;
        cfg.BulkMaterialiseMaxAttempts = 99;
        cfg.GostreamHeavyConcurrency = 99;

        Assert.Equal(6, cfg.SourceValidationParallelism);
        Assert.Equal(12, cfg.SourceValidationWindowSize);
        Assert.Equal(300, cfg.SourceValidationTimeoutSeconds);
        Assert.Equal(30, cfg.SourceValidationDetailsBudgetSeconds);
        Assert.Equal(720, cfg.SourceValidationTtlHours);
        Assert.Equal(1440, cfg.SourceValidationTransientRetryMinutes);
        Assert.Equal(60, cfg.SourceValidationLeaseMinutes);
        Assert.Equal(1440, cfg.BulkMaterialiseRunningStaleMinutes);
        Assert.Equal(8, cfg.BulkMaterialiseWorkerCount);
        Assert.Equal(20, cfg.BulkMaterialiseMaxAttempts);
        Assert.Equal(4, cfg.GostreamHeavyConcurrency);
    }
}
