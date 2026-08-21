using Microsoft.EntityFrameworkCore;
using Ptlk.AlarmLogger.Data;

namespace Ptlk.AlarmLogger.Services.Status;

public sealed record LoggerReadinessResult(
    bool IsReady,
    string Status,
    IReadOnlyList<string> Reasons);

public sealed record AlarmLoggerReadinessState(
    bool RuntimeRunning,
    bool RedisConnected,
    bool AssetInitialized,
    bool SubscriptionHealthy,
    bool HistoryWriteHealthy);

public interface IAlarmLoggerReadinessEvaluator
{
    Task<LoggerReadinessResult> EvaluateAsync(CancellationToken cancellationToken);
}

public sealed class AlarmLoggerReadinessEvaluator(
    AlarmLoggerRuntimeSnapshotService runtime,
    IDbContextFactory<HistoryDbContext> dbFactory) : IAlarmLoggerReadinessEvaluator
{
    public async Task<LoggerReadinessResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        var snapshot = runtime.GetReadinessState();
        var reasons = new List<string>();

        if (!snapshot.RuntimeRunning) reasons.Add("runtime_not_running");
        if (!snapshot.RedisConnected) reasons.Add("redis_unavailable");
        if (!snapshot.AssetInitialized) reasons.Add("asset_not_initialized");
        if (!snapshot.SubscriptionHealthy) reasons.Add("subscription_unavailable");
        if (!snapshot.HistoryWriteHealthy) reasons.Add("history_write_unavailable");
        if (!await CanConnectAsync(cancellationToken)) reasons.Add("database_unavailable");

        return new(
            reasons.Count == 0,
            reasons.Count == 0 ? "ready" : "not_ready",
            reasons);
    }

    private async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var database = await dbFactory.CreateDbContextAsync(timeout.Token);
            return await database.Database.CanConnectAsync(timeout.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
