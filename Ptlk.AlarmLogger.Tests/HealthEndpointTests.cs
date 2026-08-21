using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ptlk.AlarmLogger.Configuration;
using Ptlk.AlarmLogger.Services.Logging;
using Ptlk.AlarmLogger.Services.Status;
using Xunit;

namespace Ptlk.AlarmLogger.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task LiveAndReady_AreAnonymousAndRespectPathBase()
    {
        var readiness = new StubReadinessEvaluator(new(true, "ready", []));
        await using var app = await CreateAppAsync(readiness);
        using var client = app.GetTestClient();

        var live = await client.GetAsync("/alarm-logger/healthz/live");
        var ready = await client.GetAsync("/alarm-logger/healthz/ready");
        var legacy = await client.GetAsync("/alarm-logger/healthz");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal("live", (await live.Content.ReadFromJsonAsync<HealthResponse>())?.Status);
        Assert.Equal("ready", (await ready.Content.ReadFromJsonAsync<LoggerReadinessResult>())?.Status);
    }

    [Fact]
    public async Task Ready_CanRecoverWithinSameHttpProcess()
    {
        var readiness = new StubReadinessEvaluator(new(
            false,
            "not_ready",
            ["database_unavailable"]));
        await using var app = await CreateAppAsync(readiness);
        using var client = app.GetTestClient();

        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/alarm-logger/healthz/ready")).StatusCode);

        readiness.Result = new(true, "ready", []);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/alarm-logger/healthz/ready")).StatusCode);
    }

    [Fact]
    public async Task Ready_WhenDependencyFails_Returns503AndSafeReasonCodes()
    {
        var readiness = new StubReadinessEvaluator(new(
            false,
            "not_ready",
            ["database_unavailable"]));
        await using var app = await CreateAppAsync(readiness);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/alarm-logger/healthz/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("database_unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-canary", body, StringComparison.Ordinal);
    }

    private static async Task<WebApplication> CreateAppAsync(IAlarmLoggerReadinessEvaluator readiness)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
        builder.Services.AddSingleton(readiness);
        builder.Services.Configure<AlarmLoggerOptions>(_ => { });
        builder.Services.AddSingleton<AlarmLoggerUiEventHub>();
        builder.Services.AddSingleton<AlarmLoggerRuntimeSnapshotService>();
        builder.Services.AddSingleton<AlarmEventQueue>();
        builder.Services.AddSingleton<AlarmLoggerStatusQueryService>();
        var app = builder.Build();
        app.UsePathBase("/alarm-logger");
        app.UseAuthorization();
        app.MapAlarmLoggerHealthEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class StubReadinessEvaluator(LoggerReadinessResult result)
        : IAlarmLoggerReadinessEvaluator
    {
        public LoggerReadinessResult Result { get; set; } = result;

        public Task<LoggerReadinessResult> EvaluateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }

    private sealed record HealthResponse(string Status);
}
