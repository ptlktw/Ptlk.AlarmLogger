namespace Ptlk.AlarmLogger.Models;

public sealed record AlarmLoggerStatusSnapshot(
    string InstanceId,
    string ServiceStatus,
    DateTimeOffset StartedAt,
    DateTimeOffset SnapshotTime,
    bool RedisConnected,
    bool AssetInitialized,
    bool AlarmSubscriptionHealthy,
    DateTimeOffset? LastAlarmEventReceivedAt,
    bool HistoryWriteHealthy,
    DateTimeOffset? LastWriteSuccessAt,
    DateTimeOffset? LastWriteFailureAt,
    string? LastWriteFailureReason,
    long ReceivedCount,
    long WrittenCount,
    long FailedWriteCount,
    long InvalidPayloadCount,
    long QueueDroppedCount,
    int QueueLength,
    IReadOnlyList<string> RecentErrors);

public sealed record AlarmLoggerHealthResponse(
    string ServiceStatus,
    DateTimeOffset StartedAt,
    DateTimeOffset SnapshotTime,
    bool RedisConnected,
    bool AssetInitialized,
    bool AlarmSubscriptionHealthy,
    bool HistoryWriteHealthy,
    long ReceivedCount,
    long WrittenCount,
    long FailedWriteCount,
    long InvalidPayloadCount,
    long QueueDroppedCount,
    int QueueLength);
