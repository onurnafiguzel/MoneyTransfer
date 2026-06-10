using MoneyTransfer.Api.Domain;
using MoneyTransfer.Api.Infrastructure.Errors;
using MoneyTransfer.Api.Infrastructure.Persistence;

namespace MoneyTransfer.Api.Features.Accounts;

/// <summary>POST /accounts — create an account (with a zero balance). Enables isolated, repeatable load tests.</summary>
public static class CreateAccount
{
    public sealed record Request(string OwnerRef, string Currency, bool AllowsNegative);

    public static IEndpointRouteBuilder MapCreateAccount(this IEndpointRouteBuilder app)
    {
        app.MapPost("/accounts", Handle);
        return app;
    }

    private static async Task<IResult> Handle(Request req, LedgerDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.OwnerRef))
            return ApiResults.UnprocessableEntity(ErrorCodes.InvalidOwner, "ownerRef is required");

        var currency = (req.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
            return ApiResults.UnprocessableEntity(ErrorCodes.InvalidCurrency, "currency must be a 3-letter ISO code");

        var account = Account.Create(req.OwnerRef, currency, req.AllowsNegative, isSystem: false, DateTimeOffset.UtcNow);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/accounts/{account.Id}", new { id = account.Id, currency = account.Currency });
    }
}
