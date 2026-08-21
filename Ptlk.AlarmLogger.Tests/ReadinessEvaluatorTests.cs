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
    public async Task EvaluateAsync_WhenDependenciesAreAvailable_IsReady()
    {
        await using var fixture = new ReadinessFixture();
        fixture.SetRuntimeReady();

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
        Assert.Equal("not_ready", result.Status);
        Assert.Equal(
            ["runtime_not_running", "redis_unavailable", "asset_not_initialized", "subscription_unavailable"],
            result.Reasons);
    }

    [Fact]
    public async Task EvaluateAsync_WhenWriterAndDatabaseRecover_ReturnsToReadyWithoutLeakingFailure()
    {
        await using var fixture = new ReadinessFixture();
        fixture.SetRuntimeReady();
        fixture.Runtime.MarkWriteFailure(1, "Host=db;Password=secret-canary");
        fixture.Database.Available = false;

        var failed = await fixture.Evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(["history_write_unavailable", "database_unavailable"], failed.Reasons);
        Assert.DoesNotContain("secret-canary", System.Text.Json.JsonSerializer.Serialize(failed));

        fixture.Runtime.MarkWriteSuccess(1);
        fixture.Database.Available = true;
        var recovered = await fixture.Evaluator.EvaluateAsync(CancellationToken.None);

        Assert.True(recovered.IsReady);
        Assert.Empty(recovered.Reasons);
    }

    [Fact]
    public async Task EvaluateAsync_WhenCallerCancels_PropagatesCancellation()
    {
        await using var fixture = new ReadinessFixture();
        fixture.SetRuntimeReady();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Evaluator.EvaluateAsync(cancellation.Token));
    }

    private sealed class ReadinessFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");

        public ReadinessFixture()
        {
            connection.Open();
            var options = Options.Create(new AlarmLoggerOptions());
            Runtime = new AlarmLoggerRuntimeSnapshotService(options, new AlarmLoggerUiEventHub());
            var dbOptions = new DbContextOptionsBuilder<HistoryDbContext>().UseSqlite(connection).Options;
            Database = new SwitchableDbContextFactory(dbOptions);
            Evaluator = new AlarmLoggerReadinessEvaluator(Runtime, Database);
        }

        public AlarmLoggerRuntimeSnapshotService Runtime { get; }
        public SwitchableDbContextFactory Database { get; }
        public AlarmLoggerReadinessEvaluator Evaluator { get; }

        public void SetRuntimeReady()
        {
            Runtime.SetStartupState(AlarmLoggerServiceStatus.Running, true, true);
            Runtime.SetSubscriptionState(true);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    public sealed class SwitchableDbContextFactory(DbContextOptions<HistoryDbContext> options)
        : IDbContextFactory<HistoryDbContext>
    {
        public bool Available { get; set; } = true;

        public HistoryDbContext CreateDbContext()
        {
            if (!Available) throw new InvalidOperationException("Password=secret-canary");
            return new HistoryDbContext(options);
        }

        public Task<HistoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
