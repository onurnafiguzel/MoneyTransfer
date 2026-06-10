using Microsoft.Extensions.Options;
using MoneyTransfer.Api.Infrastructure;
using MoneyTransfer.Api.Infrastructure.Errors;
using MoneyTransfer.Api.Infrastructure.Idempotency;
using MoneyTransfer.Api.Infrastructure.Movement;

namespace MoneyTransfer.Api.Features.Deposits;

/// <summary>POST /deposits — credit an account from its currency's system (external) account.</summary>
public static class CreateDeposit
{
    public sealed record Request(Guid Account, long Amount, string? Reason);

    public static IEndpointRouteBuilder MapCreateDeposit(this IEndpointRouteBuilder app)
    {
        app.MapPost("/deposits", Handle);
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

        var hash = hasher.ForDeposit(req.Account, req.Amount, req.Reason);
        var pre = await idem.BeginAsync(key, hash, ct);
        if (pre.Decision == IdempotencyDecision.Reuse)
            return ApiResults.UnprocessableEntity(ErrorCodes.IdempotencyKeyReuse, "Idempotency-Key was already used with a different request");
        if (pre.Decision == IdempotencyDecision.InProgress)
            return ApiResults.Conflict(ErrorCodes.RequestInProgress, "a concurrent request with the same Idempotency-Key is being processed");
        if (pre.Decision == IdempotencyDecision.Replay)
            return Results.Created($"/transfers/{pre.TxId}", new { txId = pre.TxId, createdAt = pre.CreatedAt });

        var (tx, error) = await ledger.DepositAsync(req.Account, req.Amount, req.Reason, key, hash, ct);
        if (error == MovementError.None)
        {
            await idem.CompleteAsync(key, hash, tx!.Id, tx.CreatedAt);
            return Results.Created($"/transfers/{tx.Id}", new { txId = tx.Id, createdAt = tx.CreatedAt });
        }

        await idem.ReleaseAsync(key);
        return error switch
        {
            MovementError.DuplicateRequest => ApiResults.Conflict(ErrorCodes.RequestInProgress, "a concurrent request with the same Idempotency-Key is being processed"),
            MovementError.Contention => ApiResults.ServiceUnavailable(ErrorCodes.TooMuchContention, "the deposit could not complete due to contention; retry shortly"),
            MovementError.AccountNotFound => ApiResults.NotFound(ErrorCodes.AccountNotFound, "account not found"),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
