using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ptlk.AlarmLogger.Configuration;
using Ptlk.AlarmLogger.Data;
using Ptlk.AlarmLogger.Models;
using Ptlk.AlarmLogger.Services.Status;

namespace Ptlk.AlarmLogger.Services.Logging;

public sealed class AlarmHistoryWriter(
    IDbContextFactory<HistoryDbContext> dbFactory,
    AlarmLoggerRuntimeSnapshotService status,
    AlarmLoggerUiEventHub uiEvents,
    IOptions<AlarmLoggerOptions> options,
    ILogger<AlarmHistoryWriter> logger)
{
    public async Task WriteBatchAsync(
        IReadOnlyList<AlarmHistoryRecord> records,
        CancellationToken cancellationToken = default)
    {
        if (records.Count == 0)
        {
            return;
        }

        var maxAttempts = Math.Max(1, options.Value.HistoryWriteRetryCount + 1);
        Exception? lastException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await WriteBatchOnceAsync(records, cancellationToken);
                status.MarkWriteSuccess(records.Count);
                uiEvents.NotifyHistoryChanged();
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                logger.LogWarning(
                    ex,
                    "Failed to write AlarmLogger history batch on attempt {Attempt}/{MaxAttempts}.",
                    attempt,
                    maxAttempts);

                if (attempt < maxAttempts)
                {
                    await Task.Delay(GetRetryDelay(attempt), cancellationToken);
                }
            }
        }

        var reason = lastException?.Message ?? "Unknown history write failure.";
        status.MarkWriteFailure(
            records.Count,
            $"History write failed after {maxAttempts} attempts: {reason}");
    }

    private async Task WriteBatchOnceAsync(
        IReadOnlyList<AlarmHistoryRecord> records,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            db.AlarmHistoryRecords.AddRange(records);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await TryRollbackAsync(transaction, ex, cancellationToken);
            throw;
        }
    }

    private async Task TryRollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        Exception writeException,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (Exception rollbackException)
        {
            logger.LogWarning(
                rollbackException,
                "Failed to rollback AlarmLogger history transaction after write failure.");
            status.MarkDiagnostic(
                $"History transaction rollback failed after write failure: {rollbackException.Message}. Original write failure: {writeException.Message}");
        }
    }

    private TimeSpan GetRetryDelay(int attempt)
    {
        var delayMs = options.Value.HistoryWriteRetryDelayMs * attempt;
        return TimeSpan.FromMilliseconds(Math.Min(delayMs, 5000));
    }
}
