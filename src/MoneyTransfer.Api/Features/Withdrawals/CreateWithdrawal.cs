using Microsoft.Extensions.Options;
using MoneyTransfer.Api.Infrastructure;
using MoneyTransfer.Api.Infrastructure.Errors;
using MoneyTransfer.Api.Infrastructure.Idempotency;
using MoneyTransfer.Api.Infrastructure.Movement;

namespace MoneyTransfer.Api.Features.Withdrawals;

/// <summary>POST /withdrawals — debit an account into its currency's system (external) account.</summary>
public static class CreateWithdrawal
{
    public sealed record Request(Guid Account, long Amount, string? Reason);

    public static IEndpointRouteBuilder MapCreateWithdrawal(this IEndpointRouteBuilder app)
    {
        app.MapPost("/withdrawals", Handle);
        return app;
    }

    private static async Task<IResult> Handle(
        Request req, LedgerService ledger, RequestHasher hasher, IdempotencyService idem,
        IOptions<LedgerOptions> options, HttpContext http, CancellationToken ct)
    {
        var key = http.Request.Headers.IdempotencyKeyValue();
        if (string.IsNullOrWhiteSpace(key))
            return ApiResults.BadRequest(ErrorCodes.IdempotencyKeyRequired, "Idempotency-Key header is required");

        var max = options.Value.MaxTransfer;
        if (req.Amount <= 0 || req.Amount > max)
            return ApiResults.UnprocessableEntity(ErrorCodes.InvalidAmount, $"amount must be between 1 and {max} minor units");

        var hash = hasher.ForWithdrawal(req.Account, req.Amount, req.Reason);
        var pre = await idem.CheckAsync(key, hash, ct);
        if (pre.Decision == IdempotencyDecision.Reuse)
            return ApiResults.UnprocessableEntity(ErrorCodes.IdempotencyKeyReuse, "Idempotency-Key was already used with a different request");
        if (pre.Decision == IdempotencyDecision.Replay)
            return Results.Created($"/transfers/{pre.TxId}", new { txId = pre.TxId, createdAt = pre.CreatedAt });

        var (tx, error) = await ledger.WithdrawAsync(req.Account, req.Amount, req.Reason, key, hash, ct);
        return error switch
        {
            MovementError.None => Results.Created($"/transfers/{tx!.Id}", new { txId = tx.Id, createdAt = tx.CreatedAt }),
            MovementError.DuplicateRequest => ApiResults.Conflict(ErrorCodes.RequestInProgress, "a concurrent request with the same Idempotency-Key is being processed"),
            MovementError.AccountNotFound => ApiResults.NotFound(ErrorCodes.AccountNotFound, "account not found"),
            MovementError.InsufficientFunds => ApiResults.UnprocessableEntity(ErrorCodes.InsufficientFunds, "account has insufficient funds"),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
