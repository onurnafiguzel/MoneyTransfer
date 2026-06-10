using Microsoft.EntityFrameworkCore;
using MoneyTransfer.Api.Infrastructure.Errors;
using MoneyTransfer.Api.Infrastructure.Pagination;
using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Features.Accounts;

/// <summary>GET /accounts/{id}/entries?cursor=&amp;size=50 — keyset-paginated ledger history (newest first).</summary>
public static class GetEntries
{
    private const int DefaultSize = 50;
    private const int MaxSize = 200;

    public static IEndpointRouteBuilder MapGetEntries(this IEndpointRouteBuilder app)
    {
        app.MapGet("/accounts/{id:guid}/entries", Handle);
        return app;
    }

    private static async Task<IResult> Handle(Guid id, LedgerDbContext db, string? cursor, int? size, CancellationToken ct)
    {
        if (!await db.Accounts.AnyAsync(a => a.Id == id, ct))
            return ApiResults.NotFound(ErrorCodes.AccountNotFound, "account not found");

        var take = Math.Clamp(size ?? DefaultSize, 1, MaxSize);
        var after = Cursor.Decode(cursor);

        // Keyset pagination over the monotonic id; (account_id, id DESC) index makes this O(take).
        var rows = await db.LedgerEntries
            .Where(e => e.AccountId == id && (after == null || e.Id < after))
            .OrderByDescending(e => e.Id)
            .Take(take + 1)
            .Select(e => new { e.Id, e.TxId, e.Amount, e.BalanceAfter, e.CreatedAt })
            .ToListAsync(ct);

        var hasMore = rows.Count > take;
        var page = hasMore ? rows.GetRange(0, take) : rows;
        var nextCursor = hasMore ? Cursor.Encode(page[^1].Id) : null;

        return Results.Ok(new
        {
            entries = page.Select(e => new
            {
                txId = e.TxId,
                amount = e.Amount,
                balanceAfter = e.BalanceAfter,
                createdAt = e.CreatedAt,
            }),
            nextCursor,
        });
    }
}
