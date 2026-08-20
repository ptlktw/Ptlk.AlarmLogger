using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ptlk.AlarmLogger.Configuration;
using Ptlk.AlarmLogger.Data;
using Ptlk.AlarmLogger.Models;
using Ptlk.AlarmLogger.Services.Logging;
using Ptlk.AlarmLogger.Services.Status;
using Xunit;

namespace Ptlk.AlarmLogger.Tests;

public sealed class ReadinessEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_WhenRuntimeDependenciesAndDatabaseAreAvailable_IsReady()
    {
        await using var fixture = new ReadinessFixture();
        fixture.Runtime.SetStartupState(AlarmLoggerServiceStatus.Running, true, true);
        fixture.Runtime.SetSubscriptionState(true);

        var result = await fixture.Evaluator.EvaluateAsync(CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal("ready", result.Status);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public async Task EvaluateAsync_WhenRuntimeDependenciesAreUnavailable_ReturnsStableReasonCodes()
    {
        await using var fixture = new ReadinessFixture();

        var result = await fixture.Evaluator.EvaluateAsync(CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Contains("runtime_not_running", result.Reasons);
        Assert.Contains("redis_unavailable", result.Reasons);
        Assert.Contains("asset_not_initialized", result.Reasons);
        Assert.Contains("subscription_unavailable", result.Reasons);
    }

    private sealed class ReadinessFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");

        public ReadinessFixture()
        {
            connection.Open();
            var options = Options.Create(new AlarmLoggerOptions());
            Runtime = new AlarmLoggerRuntimeSnapshotService(options, new AlarmLoggerUiEventHub());
            var query = new AlarmLoggerStatusQueryService(Runtime, new AlarmEventQueue(options));
            var dbOptions = new DbContextOptionsBuilder<HistoryDbContext>().UseSqlite(connection).Options;
            Evaluator = new AlarmLoggerReadinessEvaluator(query, new TestDbContextFactory(dbOptions));
        }

        public AlarmLoggerRuntimeSnapshotService Runtime { get; }
        public AlarmLoggerReadinessEvaluator Evaluator { get; }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<HistoryDbContext> options)
        : IDbContextFactory<HistoryDbContext>
    {
        public HistoryDbContext CreateDbContext() => new(options);
    }
}
