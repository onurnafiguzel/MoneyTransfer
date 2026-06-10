using Microsoft.EntityFrameworkCore;
using MoneyTransfer.Api.Infrastructure.Errors;
using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Features.Transfers;

/// <summary>GET /transfers/{txId} — transfer header plus its double-entry lines.</summary>
public static class GetTransfer
{
    public static IEndpointRouteBuilder MapGetTransfer(this IEndpointRouteBuilder app)
    {
        app.MapGet("/transfers/{txId:guid}", Handle);
        return app;
    }

    private static async Task<IResult> Handle(Guid txId, LedgerDbContext db, CancellationToken ct)
    {
        var tx = await db.Transfers
            .Include(t => t.Entries.OrderBy(e => e.Id))
            .FirstOrDefaultAsync(t => t.Id == txId, ct);

        if (tx is null) return ApiResults.NotFound(ErrorCodes.NotFound, "transfer not found");

        return Results.Ok(new
        {
            id = tx.Id,
            kind = tx.Kind.ToString().ToLowerInvariant(),
            reason = tx.Reason,
            reversedTxId = tx.ReversedTxId,
            entries = tx.Entries.Select(e => new
            {
                accountId = e.AccountId,
                amount = e.Amount,
                balanceAfter = e.BalanceAfter,
                createdAt = e.CreatedAt,
            }),
        });
    }
}
