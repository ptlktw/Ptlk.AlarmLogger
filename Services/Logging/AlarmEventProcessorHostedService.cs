using System.Text.Json;
using Microsoft.Extensions.Options;
using Ptlk.AlarmLogger.Configuration;
using Ptlk.AlarmLogger.Models;
using Ptlk.AlarmLogger.Services.Status;
using Ptlk.SCADA.Interop.Contracts.Redis;

namespace Ptlk.AlarmLogger.Services.Logging;

public sealed class AlarmEventProcessorHostedService(
    AlarmEventQueue queue,
    AlarmHistoryWriter writer,
    AlarmLoggerRuntimeSnapshotService status,
    IOptions<AlarmLoggerOptions> options,
    ILogger<AlarmEventProcessorHostedService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions ContractJsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AlarmHistoryRecord>();
        var nextFlush = DateTimeOffset.UtcNow.AddMilliseconds(options.Value.HistoryFlushIntervalMs);

        await foreach (var envelope in queue.ReadAllAsync(stoppingToken))
        {
            if (TryCreateRecord(envelope, out var record, out var error))
            {
                batch.Add(record);
            }
            else
            {
                var reason = $"Invalid AlarmEvent from {envelope.Channel}: {error}";
                logger.LogWarning("{Reason}", reason);
                status.MarkInvalidPayload(reason);
            }

            if (batch.Count >= options.Value.HistoryBatchSize || DateTimeOffset.UtcNow >= nextFlush)
            {
                await writer.WriteBatchAsync(batch, stoppingToken);
                batch.Clear();
                nextFlush = DateTimeOffset.UtcNow.AddMilliseconds(options.Value.HistoryFlushIntervalMs);
            }
        }
    }

    private static bool TryCreateRecord(
        AlarmEventEnvelope envelope,
        out AlarmHistoryRecord record,
        out string error)
    {
        record = new AlarmHistoryRecord();
        error = "";

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(envelope.Payload);
        }
        catch (JsonException ex)
        {
            error = $"payload is not valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "payload root must be a JSON object.";
                return false;
            }

            var root = document.RootElement;
            if (!TryGetRequiredString(root, "SourceName", out var sourceName, out error)
                || !TryGetRequiredEnum<AlarmConditionName>(root, "ConditionName", out var conditionName, out error)
                || !TryGetRequiredString(root, "ConditionSubName", out var conditionSubName, out error)
                || !TryGetRequiredBool(root, "ConditionActive", out var conditionActive, out error)
                || !TryGetRequiredEnum<AlarmQuality>(root, "Quality", out var quality, out error)
                || !TryGetRequiredUnixMs(root, "QualityTime", out var qualityTime, out error)
                || !TryGetRequiredUnixMs(root, "EventTime", out var eventTime, out error)
                || !TryGetRequiredUnixMs(root, "Timestamp", out var timestamp, out error)
                || !TryGetRequiredBool(root, "IsAcknowledge", out var isAcknowledge, out error)
                || !TryGetRequiredBool(root, "NeedAck", out var needAck, out error)
                || !TryGetRequiredString(root, "Message", out var message, out error)
                || !TryGetOptionalString(root, "CategoryTag", out var categoryTag, out error)
                || !TryGetOptionalScalarJson(root, "OldValue", out var oldValueJson, out error)
                || !TryGetOptionalScalarJson(root, "NewValue", out var newValueJson, out error))
            {
                return false;
            }

            AlarmEventContract? contract;
            try
            {
                contract = JsonSerializer.Deserialize<AlarmEventContract>(
                    envelope.Payload,
                    ContractJsonOptions);
            }
            catch (JsonException ex)
            {
                error = $"payload could not be materialized as the shared alarm contract: {ex.Message}";
                return false;
            }

            if (contract is null)
            {
                error = "payload could not be materialized as the shared alarm contract.";
                return false;
            }

            record = new AlarmHistoryRecord
            {
                Timestamp = timestamp,
                EventTime = eventTime,
                SourceName = contract.SourceName,
                CategoryTag = contract.CategoryTag,
                ConditionName = conditionName,
                ConditionSubName = contract.ConditionSubName,
                ConditionActive = contract.ConditionActive,
                Quality = quality,
                QualityTime = qualityTime,
                IsAcknowledge = contract.IsAcknowledge,
                NeedAck = contract.NeedAck,
                OldValueJson = contract.OldValue?.GetRawText(),
                NewValueJson = contract.NewValue?.GetRawText(),
                Message = contract.Message,
                ReceivedAt = envelope.ReceivedAt.ToUniversalTime()
            };
            return true;
        }
    }

    private static bool TryGetRequiredString(
        JsonElement root,
        string propertyName,
        out string value,
        out string error)
    {
        value = "";
        error = "";
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            error = $"{propertyName} is required and must be a string.";
            return false;
        }

        value = property.GetString()!.Trim();
        if (value.Length == 0)
        {
            error = $"{propertyName} cannot be empty.";
            return false;
        }

        return true;
    }

    private static bool TryGetOptionalString(
        JsonElement root,
        string propertyName,
        out string? value,
        out string error)
    {
        value = null;
        error = "";
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null
            || property.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            error = $"{propertyName} must be a string or null.";
            return false;
        }

        value = property.GetString()?.Trim();
        if (value?.Length == 0)
        {
            value = null;
        }

        return true;
    }

    private static bool TryGetRequiredEnum<TEnum>(
        JsonElement root,
        string propertyName,
        out TEnum value,
        out string error)
        where TEnum : struct
    {
        value = default;
        error = "";
        if (!TryGetRequiredString(root, propertyName, out var raw, out error))
        {
            return false;
        }

        if (!Enum.TryParse<TEnum>(raw, ignoreCase: true, out value))
        {
            error = $"{propertyName} has an unsupported value.";
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredBool(
        JsonElement root,
        string propertyName,
        out bool value,
        out string error)
    {
        value = false;
        error = "";
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"{propertyName} is required and must be a boolean.";
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetRequiredUnixMs(
        JsonElement root,
        string propertyName,
        out DateTimeOffset value,
        out string error)
    {
        value = default;
        error = "";
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var unixMs))
        {
            error = $"{propertyName} is required and must be a Unix millisecond number.";
            return false;
        }

        try
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToUniversalTime();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = $"{propertyName} is outside the supported Unix millisecond range.";
            return false;
        }
    }

    private static bool TryGetOptionalScalarJson(
        JsonElement root,
        string propertyName,
        out string? value,
        out string error)
    {
        value = null;
        error = "";
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null
            || property.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }

        if (property.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            error = $"{propertyName} must be a JSON scalar or null.";
            return false;
        }

        value = property.GetRawText();
        return true;
    }
}
