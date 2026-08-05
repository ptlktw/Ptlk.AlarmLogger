using Ptlk.AlarmLogger.Models;
using Ptlk.AlarmLogger.Services.Logging;

namespace Ptlk.AlarmLogger.Services.Status;

public sealed class AlarmLoggerStatusQueryService(
    AlarmLoggerRuntimeSnapshotService runtime,
    AlarmEventQueue queue)
{
    public AlarmLoggerStatusSnapshot GetSnapshot() =>
        runtime.CreateSnapshot(queue.Count, queue.DroppedCount);

    public AlarmLoggerHealthResponse GetHealth()
    {
        var snapshot = GetSnapshot();
        return new AlarmLoggerHealthResponse(
            snapshot.ServiceStatus,
            snapshot.StartedAt,
            snapshot.SnapshotTime,
            snapshot.RedisConnected,
            snapshot.AssetInitialized,
            snapshot.AlarmSubscriptionHealthy,
            snapshot.HistoryWriteHealthy,
            snapshot.ReceivedCount,
            snapshot.WrittenCount,
            snapshot.FailedWriteCount,
            snapshot.InvalidPayloadCount,
            snapshot.QueueDroppedCount,
            snapshot.QueueLength);
    }
}
