using MoneyTransfer.Api.Infrastructure.Movement;

namespace MoneyTransfer.Api.Features.Metrics;

/// <summary>
/// GET /metrics/contention — cumulative concurrency counters since process start. Makes deadlock/serialization
/// retries observable so load tests can prove contention occurred AND was recovered (exhaustions == 0).
/// </summary>
public static class GetContentionMetrics
{
    public static IEndpointRouteBuilder MapGetContentionMetrics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics/contention", (ContentionMetrics metrics) => Results.Ok(metrics.Snapshot()));
        return app;
    }
}
