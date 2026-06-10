using Microsoft.Extensions.Options;
using MoneyTransfer.Api.Infrastructure;

namespace MoneyTransfer.Api.Infrastructure.Movement;

/// <summary>
/// Resolves the active <see cref="IBalanceMutator"/>. The strategy is a deployment decision (config), the way
/// large systems run it — clients do NOT choose it. The <c>X-Concurrency-Strategy</c> header is a TEST hook,
/// honored only when <c>AllowStrategyOverride</c> is explicitly enabled (Development/compose), never in prod.
/// </summary>
public sealed class BalanceMutatorResolver(
    IServiceProvider services,
    IHttpContextAccessor httpContextAccessor,
    IOptions<LedgerOptions> options) : IBalanceMutatorResolver
{
    public const string HeaderName = "X-Concurrency-Strategy";

    public IBalanceMutator Resolve()
    {
        var key = options.Value.ConcurrencyStrategy;

        if (options.Value.AllowStrategyOverride)
        {
            var header = httpContextAccessor.HttpContext?.Request.Headers[HeaderName].ToString();
            if (!string.IsNullOrWhiteSpace(header)) key = header;
        }

        return services.GetRequiredKeyedService<IBalanceMutator>(Normalize(key));
    }

    private static string Normalize(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "naive" => "naive",
        "pessimistic" or "a" => "pessimistic",
        "optimistic" or "optimisticcas" or "cas" or "c" => "optimistic",
        _ => "conditional", // conditional / conditionalupdate / b / unrecognized → safe default
    };
}
