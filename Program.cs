using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Ptlk.AlarmLogger.Components;
using Ptlk.AlarmLogger.Configuration;
using Ptlk.AlarmLogger.Data;
using Ptlk.AlarmLogger.Services.Logging;
using Ptlk.AlarmLogger.Services.Query;
using Ptlk.AlarmLogger.Services.Redis;
using Ptlk.AlarmLogger.Services.Startup;
using Ptlk.AlarmLogger.Services.Status;
using Ptlk.SSO.Client;
using Ptlk.SSO.Core.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddAlarmLoggerOptions(builder.Configuration);
builder.Services.AddPtlkSsoServiceAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddCascadingAuthenticationState();

var historyConnection = builder.Configuration.GetConnectionString("HistoryConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:HistoryConnection is required.");
if (string.IsNullOrWhiteSpace(historyConnection))
{
    throw new InvalidOperationException("ConnectionStrings:HistoryConnection is required.");
}
var historySchema = OptionsRegistration.IsSafeIdentifier(builder.Configuration["AlarmLogger:HistorySchema"])
    ? builder.Configuration["AlarmLogger:HistorySchema"]!
    : "alarm_logger";

var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? (builder.Environment.IsDevelopment() ? "data-protection-keys" : "/data/data-protection-keys");
Directory.CreateDirectory(dataProtectionKeysPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

builder.Services.AddDbContextFactory<HistoryDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(
            historyConnection,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", historySchema))
        .UseSnakeCaseNamingConvention();
});

builder.Services.AddSingleton<AlarmLoggerRuntimeSnapshotService>();
builder.Services.AddSingleton<AlarmLoggerUiEventHub>();
builder.Services.AddSingleton<AlarmEventQueue>();
builder.Services.AddSingleton<RedisConnectionFactory>();
builder.Services.AddSingleton<AlarmHistoryWriter>();

builder.Services.AddScoped<AlarmLoggerStatusQueryService>();
builder.Services.AddScoped<AlarmHistoryQueryService>();

builder.Services.AddHostedService<StartupGateService>();
builder.Services.AddHostedService<RedisAlarmEventSubscriptionService>();
builder.Services.AddHostedService<AlarmEventProcessorHostedService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();
var authenticationDeployment = app.Services.GetRequiredService<PtlkServiceAuthenticationOptions>();
var ssoDeployment = app.Services.GetRequiredService<PtlkSsoServiceOptions>();
var securityWarnings = app.Services.GetRequiredService<PtlkSecurityWarningOptions>();
if (securityWarnings.Show && authenticationDeployment.IsDevelopmentBypass)
{
    app.Logger.LogWarning("Development Bypass is active for AlarmLogger.");
}
if (securityWarnings.Show && !ssoDeployment.RequireTls)
{
    app.Logger.LogWarning("TLS NOT REQUIRED for AlarmLogger; use only on a controlled test network.");
}

using (var scope = app.Services.CreateScope())
{
    var historyDbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<HistoryDbContext>>();
    await using var historyDb = await historyDbFactory.CreateDbContextAsync();
    var alarmLoggerOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlarmLoggerOptions>>();
    await HistoryDatabaseInitializer.PrepareMigrationsAsync(
        historyDb,
        alarmLoggerOptions);
    await historyDb.Database.MigrateAsync();
    await HistoryDatabaseInitializer.InitializeTimescaleAsync(
        historyDb,
        alarmLoggerOptions);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
if (builder.Configuration.GetValue("Sso:RequireTls", true))
{
    app.UseHsts();
}

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();
app.MapPtlkSsoServiceAuthentication();

app.MapGet("/healthz", (AlarmLoggerStatusQueryService status) => Results.Ok(status.GetHealth()))
    .AllowAnonymous();

app.MapGet("/api/alarm-logger/status", (AlarmLoggerStatusQueryService status) => Results.Ok(status.GetSnapshot()))
    .RequireAuthorization(PtlkSsoServiceAuthentication.ApiPolicy);

app.MapGet("/api/alarm-logger/history/range", (
    string? begin,
    string? end,
    string? order,
    string? time_zone,
    string? category_tag,
    AlarmHistoryQueryService query,
    CancellationToken cancellationToken) => query.QueryRangeHttpAsync(begin, end, order, time_zone, category_tag, cancellationToken))
    .RequireAuthorization(PtlkSsoServiceAuthentication.ApiPolicy);

app.MapGet("/api/alarm-logger/history/page", (
    int? skip,
    int? take,
    string? order,
    string? time_zone,
    string? category_tag,
    AlarmHistoryQueryService query,
    CancellationToken cancellationToken) => query.QueryPageHttpAsync(skip, take, order, time_zone, category_tag, cancellationToken))
    .RequireAuthorization(PtlkSsoServiceAuthentication.ApiPolicy);

app.Run();
