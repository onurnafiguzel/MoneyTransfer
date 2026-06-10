using MoneyTransfer.Api.Infrastructure.Errors;
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

    private static async Task<IResult> Handle(Guid txId, Request? req, LedgerService ledger, CancellationToken ct)
    {
        var (reversal, error) = await ledger.ReverseAsync(txId, req?.Reason, ct);
        return error switch
        {
            ReverseError.None => Results.Created($"/transfers/{reversal!.Id}", new { reversalTxId = reversal.Id }),
            ReverseError.NotFound => ApiResults.NotFound(ErrorCodes.NotFound, "transfer not found"),
            ReverseError.AlreadyReversed => ApiResults.Conflict(ErrorCodes.AlreadyReversed, "transfer has already been reversed"),
            ReverseError.InsufficientFunds => ApiResults.UnprocessableEntity(ErrorCodes.InsufficientFunds, "counterparty has insufficient funds to reverse"),
            _ => ApiResults.NotFound(ErrorCodes.AccountNotFound, "account not found"),
        };
    }
}
