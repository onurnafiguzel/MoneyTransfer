using Microsoft.Extensions.Options;
using MoneyTransfer.Api.Domain;
using MoneyTransfer.Api.Infrastructure;
using MoneyTransfer.Api.Infrastructure.Errors;
using MoneyTransfer.Api.Infrastructure.Movement;

namespace MoneyTransfer.Api.Features.Transfers;

/// <summary>POST /transfers — move money between two accounts of the same currency.</summary>
public static class CreateTransfer
{
    public sealed record Request(Guid FromAccount, Guid ToAccount, long Amount, string? Reason);

    public static IEndpointRouteBuilder MapCreateTransfer(this IEndpointRouteBuilder app)
    {
        app.MapPost("/transfers", Handle);
        return app;
    }

    private static async Task<IResult> Handle(Request req, LedgerService ledger, IOptions<LedgerOptions> options, CancellationToken ct)
    {
        var max = options.Value.MaxTransfer;
        if (req.Amount <= 0 || req.Amount > max)
            return ApiResults.UnprocessableEntity(ErrorCodes.InvalidAmount, $"amount must be between 1 and {max} minor units");
        if (req.FromAccount == req.ToAccount)
            return ApiResults.UnprocessableEntity(ErrorCodes.SelfTransfer, "source and destination accounts must differ");

        var (tx, error) = await ledger.MoveAsync(req.FromAccount, req.ToAccount, req.Amount, TransferKind.Transfer, req.Reason, null, ct);
        return error switch
        {
            MovementError.None => Results.Created($"/transfers/{tx!.Id}", new { txId = tx.Id, createdAt = tx.CreatedAt }),
            MovementError.AccountNotFound => ApiResults.NotFound(ErrorCodes.AccountNotFound, "source or destination account not found"),
            MovementError.CurrencyMismatch => ApiResults.UnprocessableEntity(ErrorCodes.CurrencyMismatch, "accounts have different currencies"),
            MovementError.InsufficientFunds => ApiResults.UnprocessableEntity(ErrorCodes.InsufficientFunds, "source account has insufficient funds"),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
