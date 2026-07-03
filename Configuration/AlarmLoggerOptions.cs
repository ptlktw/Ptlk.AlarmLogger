using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Ptlk.AlarmLogger.Configuration;

public sealed class RedisOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6379;
    public string? AclUsername { get; set; }
    public string? AclPassword { get; set; }
    public int DatabaseIndex { get; set; }
    public int ConnectTimeoutMs { get; set; } = 5000;
    public int SyncTimeoutMs { get; set; } = 3000;
    public bool AbortConnect { get; set; }
    public int ConnectRetry { get; set; } = 3;
    public int KeepAliveSeconds { get; set; } = 60;
    public bool Ssl { get; set; }
}

public sealed class StartupGateOptions
{
    public int WaitInitializedTimeoutMs { get; set; } = 60000;
    public int InitialRetryDelayMs { get; set; } = 250;
    public int MaxRetryDelayMs { get; set; } = 5000;
}

public sealed class AlarmLoggerOptions
{
    public string InstanceId { get; set; } = "alarm-logger-local-1";
    public int EventQueueCapacity { get; set; } = 5000;
    public int HistoryBatchSize { get; set; } = 500;
    public int HistoryFlushIntervalMs { get; set; } = 1000;
    public int HistoryWriteRetryCount { get; set; } = 2;
    public int HistoryWriteRetryDelayMs { get; set; } = 500;
    public int RecentHistoryTake { get; set; } = 50;
    public int HistoryQueryMaxTake { get; set; } = 1000;
    public int HmiRefreshIntervalMs { get; set; } = 3000;
    public string QueryDefaultTimeZone { get; set; } = "+08:00";
    public string HistorySchema { get; set; } = "alarm_logger";
    public int StatusSnapshotRetainErrors { get; set; } = 50;
}

public static class OptionsRegistration
{
    private static readonly Regex IdentifierRegex = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IServiceCollection AddAlarmLoggerOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection("Redis"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Host)
                           && o.Port is > 0 and <= 65535
                           && o.DatabaseIndex >= 0
                           && o.ConnectTimeoutMs > 0
                           && o.SyncTimeoutMs > 0
                           && o.ConnectRetry >= 0
                           && o.KeepAliveSeconds > 0,
                "Redis options are invalid.")
            .ValidateOnStart();

        services.AddOptions<StartupGateOptions>()
            .Bind(configuration.GetSection("StartupGate"))
            .Validate(o => o.WaitInitializedTimeoutMs > 0
                           && o.InitialRetryDelayMs > 0
                           && o.MaxRetryDelayMs >= o.InitialRetryDelayMs,
                "StartupGate retry settings are invalid.")
            .ValidateOnStart();

        services.AddOptions<AlarmLoggerOptions>()
            .Bind(configuration.GetSection("AlarmLogger"))
            .Validate(o => IsSafeToken(o.InstanceId)
                           && o.EventQueueCapacity > 0
                           && o.HistoryBatchSize > 0
                           && o.HistoryFlushIntervalMs > 0
                           && o.HistoryWriteRetryCount >= 0
                           && o.HistoryWriteRetryDelayMs > 0
                           && o.RecentHistoryTake > 0
                           && o.HistoryQueryMaxTake > 0
                           && o.RecentHistoryTake <= o.HistoryQueryMaxTake
                           && o.HmiRefreshIntervalMs > 0
                           && QueryTimeZoneParser.TryParse(o.QueryDefaultTimeZone, out _, out _)
                           && IsSafeIdentifier(o.HistorySchema)
                           && o.StatusSnapshotRetainErrors > 0,
                "AlarmLogger options are invalid.")
            .ValidateOnStart();

        return services;
    }

    public static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && IdentifierRegex.IsMatch(value);

    private static bool IsSafeToken(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains(':', StringComparison.Ordinal)
        && !value.Contains('*', StringComparison.Ordinal)
        && !value.Contains(' ', StringComparison.Ordinal);
}
