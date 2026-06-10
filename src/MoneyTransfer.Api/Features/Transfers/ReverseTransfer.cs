using MoneyTransfer.Api.Infrastructure.Errors;
using MoneyTransfer.Api.Infrastructure.Idempotency;
using MoneyTransfer.Api.Infrastructure.Movement;

namespace MoneyTransfer.Api.Features.Transfers;

/// <summary>POST /transfers/{txId}/reverse — create a compensating reversal transfer.</summary>
public static class ReverseTransfer
{
    public sealed record Request(string? Reason);

    public static IEndpointRouteBuilder MapReverseTransfer(this IEndpointRouteBuilder app)
    {
        app.MapPost("/transfers/{txId:guid}/reverse", Handle);
        return app;
    }

    private static async Task<IResult> Handle(
        Guid txId, Request? req, LedgerService ledger, RequestHasher hasher, IdempotencyService idem,
        HttpContext http, CancellationToken ct)
    {
        var key = http.Request.Headers.IdempotencyKeyValue();
        if (string.IsNullOrWhiteSpace(key))
            return ApiResults.BadRequest(ErrorCodes.IdempotencyKeyRequired, "Idempotency-Key header is required");

        var hash = hasher.ForReversal(txId, req?.Reason);
        var pre = await idem.CheckAsync(key, hash, ct);
        if (pre.Decision == IdempotencyDecision.Reuse)
            return ApiResults.UnprocessableEntity(ErrorCodes.IdempotencyKeyReuse, "Idempotency-Key was already used with a different request");
        if (pre.Decision == IdempotencyDecision.Replay)
            return Results.Created($"/transfers/{pre.TxId}", new { reversalTxId = pre.TxId });

        var (reversal, error) = await ledger.ReverseAsync(txId, req?.Reason, key, hash, ct);
        return error switch
        {
            ReverseError.None => Results.Created($"/transfers/{reversal!.Id}", new { reversalTxId = reversal.Id }),
            ReverseError.DuplicateRequest => ApiResults.Conflict(ErrorCodes.RequestInProgress, "a concurrent request with the same Idempotency-Key is being processed"),
            ReverseError.NotFound => ApiResults.NotFound(ErrorCodes.NotFound, "transfer not found"),
            ReverseError.AlreadyReversed => ApiResults.Conflict(ErrorCodes.AlreadyReversed, "transfer has already been reversed"),
            ReverseError.InsufficientFunds => ApiResults.UnprocessableEntity(ErrorCodes.InsufficientFunds, "counterparty has insufficient funds to reverse"),
            _ => ApiResults.NotFound(ErrorCodes.AccountNotFound, "account not found"),
        };
    }
}
