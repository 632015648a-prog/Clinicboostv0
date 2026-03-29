using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace ClinicBoost.Api.Features.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness — el proceso está vivo
        app.MapGet("/health/live", () => Results.Ok(new { status = "live", ts = DateTime.UtcNow }))
           .WithTags("Health")
           .AllowAnonymous();

        // Readiness — la app + sus dependencias están listas
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteJsonResponse
        })
        .AllowAnonymous()
        .WithTags("Health");

        // Dependencies — estado de cada integración externa
        app.MapHealthChecks("/health/deps", new HealthCheckOptions
        {
            ResponseWriter = WriteJsonResponse
        })
        .RequireAuthorization()   // solo interna/admin
        .WithTags("Health");

        return app;
    }

    private static async Task WriteJsonResponse(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";

        var result = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.TotalMilliseconds,
            entries = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                duration = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
                exception = e.Value.Exception?.Message
            })
        };

        await ctx.Response.WriteAsync(
            JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
    }
}
