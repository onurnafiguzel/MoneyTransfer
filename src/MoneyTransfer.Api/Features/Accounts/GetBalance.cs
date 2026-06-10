using Microsoft.EntityFrameworkCore;
using MoneyTransfer.Api.Infrastructure.Errors;
using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Features.Accounts;

/// <summary>GET /accounts/{id}/balance — current balance, currency and version.</summary>
public static class GetBalance
{
    public static IEndpointRouteBuilder MapGetBalance(this IEndpointRouteBuilder app)
    {
        app.MapGet("/accounts/{id:guid}/balance", Handle);
        return app;
    }

    private static async Task<IResult> Handle(Guid id, LedgerDbContext db, CancellationToken ct)
    {
        var row = await db.Accounts
            .Where(a => a.Id == id)
            .Select(a => new { a.Balance.Amount, a.Currency, a.Balance.Version })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? ApiResults.NotFound(ErrorCodes.AccountNotFound, "account not found")
            : Results.Ok(new { balance = row.Amount, currency = row.Currency, version = row.Version });
    }
}
