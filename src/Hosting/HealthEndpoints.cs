using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Hosting;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapFleetHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Text("live"));
        app.MapGet("/health/ready", (ReadinessState state) =>
        {
            var (status, body) = Ready(state);
            return Results.Json(body, statusCode: status);
        });
        return app;
    }

    internal static (int StatusCode, IReadOnlyDictionary<string, bool> Body) Ready(ReadinessState state) =>
        (state.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, state.Snapshot());
}
