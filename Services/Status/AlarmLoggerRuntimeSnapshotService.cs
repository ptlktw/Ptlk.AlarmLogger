using Microsoft.Extensions.Options;
using Ptlk.AlarmLogger.Configuration;
using Ptlk.AlarmLogger.Models;

namespace Ptlk.AlarmLogger.Services.Status;

public sealed class AlarmLoggerRuntimeSnapshotService(
    IOptions<AlarmLoggerOptions> options,
    AlarmLoggerUiEventHub uiEvents)
{
    private readonly object _sync = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Queue<string> _recentErrors = [];
    private readonly int _retainErrors = options.Value.StatusSnapshotRetainErrors;
    private AlarmLoggerServiceStatus _serviceStatus = AlarmLoggerServiceStatus.Starting;
    private bool _redisConnected;
    private bool _assetInitialized;
    private bool _alarmSubscriptionHealthy;
    private bool _historyWriteHealthy = true;
    private DateTimeOffset? _lastAlarmEventReceivedAt;
    private DateTimeOffset? _lastWriteSuccessAt;
    private DateTimeOffset? _lastWriteFailureAt;
    private string? _lastWriteFailureReason;
    private long _receivedCount;
    private long _writtenCount;
    private long _failedWriteCount;
    private long _invalidPayloadCount;

    public void SetStartupState(
        AlarmLoggerServiceStatus serviceStatus,
        bool redisConnected,
        bool assetInitialized,
        string? diagnostic = null)
    {
        lock (_sync)
        {
            _serviceStatus = serviceStatus;
            _redisConnected = redisConnected;
            _assetInitialized = assetInitialized;
            if (!string.IsNullOrWhiteSpace(diagnostic)
                && serviceStatus is AlarmLoggerServiceStatus.Degraded or AlarmLoggerServiceStatus.Failed)
            {
                AddErrorCore(diagnostic);
            }
        }

        uiEvents.NotifyStatusChanged();
    }

    public void SetSubscriptionState(bool healthy)
    {
        lock (_sync)
        {
            _alarmSubscriptionHealthy = healthy;
        }

        uiEvents.NotifyStatusChanged();
    }

    public void MarkReceived(DateTimeOffset receivedAt)
    {
        lock (_sync)
        {
            _receivedCount++;
            _lastAlarmEventReceivedAt = receivedAt;
        }

        uiEvents.NotifyStatusChanged();
    }

    public void MarkInvalidPayload(string reason)
    {
        lock (_sync)
        {
            _invalidPayloadCount++;
            AddErrorCore(reason);
        }

        uiEvents.NotifyStatusChanged();
    }

    public void MarkWriteSuccess(int writtenCount)
    {
        lock (_sync)
        {
            _historyWriteHealthy = true;
            _writtenCount += writtenCount;
            _lastWriteSuccessAt = DateTimeOffset.UtcNow;
            _lastWriteFailureReason = null;
        }

        uiEvents.NotifyStatusChanged();
    }

    public void MarkWriteFailure(int failedCount, string reason)
    {
        lock (_sync)
        {
            _historyWriteHealthy = false;
            _failedWriteCount += failedCount;
            _lastWriteFailureAt = DateTimeOffset.UtcNow;
            _lastWriteFailureReason = Sanitize(reason);
            AddErrorCore(reason);
        }

        uiEvents.NotifyStatusChanged();
    }

    public void MarkDiagnostic(string reason)
    {
        lock (_sync)
        {
            AddErrorCore(reason);
        }

        uiEvents.NotifyStatusChanged();
    }

    public AlarmLoggerStatusSnapshot CreateSnapshot(int queueLength, long queueDroppedCount)
    {
        lock (_sync)
        {
            var status = ResolveStatus();
            return new AlarmLoggerStatusSnapshot(
                options.Value.InstanceId,
                status.ToString().ToLowerInvariant(),
                _startedAt,
                DateTimeOffset.UtcNow,
                _redisConnected,
                _assetInitialized,
                _alarmSubscriptionHealthy,
                _lastAlarmEventReceivedAt,
                _historyWriteHealthy,
                _lastWriteSuccessAt,
                _lastWriteFailureAt,
                _lastWriteFailureReason,
                _receivedCount,
                _writtenCount,
                _failedWriteCount,
                _invalidPayloadCount,
                queueDroppedCount,
                queueLength,
                _recentErrors.Reverse().ToList());
        }
    }

    public AlarmLoggerReadinessState GetReadinessState()
    {
        lock (_sync)
        {
            return new(
                _serviceStatus == AlarmLoggerServiceStatus.Running,
                _redisConnected,
                _assetInitialized,
                _alarmSubscriptionHealthy,
                _historyWriteHealthy);
        }
    }

    private AlarmLoggerServiceStatus ResolveStatus()
    {
        if (_serviceStatus is AlarmLoggerServiceStatus.Stopping or AlarmLoggerServiceStatus.Failed)
        {
            return _serviceStatus;
        }

        if (!_redisConnected || !_assetInitialized || !_alarmSubscriptionHealthy || !_historyWriteHealthy)
        {
            return AlarmLoggerServiceStatus.Degraded;
        }

        return _serviceStatus == AlarmLoggerServiceStatus.Starting
            ? AlarmLoggerServiceStatus.Starting
            : AlarmLoggerServiceStatus.Running;
    }

    private void AddErrorCore(string reason)
    {
        _recentErrors.Enqueue($"{DateTimeOffset.UtcNow:u} {Sanitize(reason)}");
        while (_recentErrors.Count > _retainErrors)
        {
            _recentErrors.Dequeue();
        }
    }

    private static string Sanitize(string reason)
    {
        var normalized = reason.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }
}
