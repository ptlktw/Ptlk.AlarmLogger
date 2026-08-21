using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ptlk.AlarmLogger.Services.Status;

public static class AlarmLoggerHealthEndpoints
{
    public static IEndpointRouteBuilder MapAlarmLoggerHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/healthz",
                (AlarmLoggerStatusQueryService status) => Results.Ok(status.GetHealth()))
            .AllowAnonymous();
        endpoints.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }))
            .AllowAnonymous();
        endpoints.MapGet(
                "/healthz/ready",
                async (IAlarmLoggerReadinessEvaluator readiness, CancellationToken cancellationToken) =>
                {
                    var result = await readiness.EvaluateAsync(cancellationToken);
                    return Results.Json(
                        result,
                        statusCode: result.IsReady
                            ? StatusCodes.Status200OK
                            : StatusCodes.Status503ServiceUnavailable);
                })
            .AllowAnonymous();

        return endpoints;
    }
}
