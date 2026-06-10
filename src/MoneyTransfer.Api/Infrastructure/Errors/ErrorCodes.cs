namespace MoneyTransfer.Api.Infrastructure.Errors;

/// <summary>Machine-readable error codes returned in the ProblemDetails "code" extension.</summary>
public static class ErrorCodes
{
    public const string InvalidAmount = "invalid_amount";
    public const string SelfTransfer = "self_transfer";
    public const string CurrencyMismatch = "currency_mismatch";
    public const string InsufficientFunds = "insufficient_funds";
    public const string AccountNotFound = "account_not_found";
    public const string NotFound = "not_found";
    public const string AlreadyReversed = "already_reversed";
    public const string InvalidCurrency = "invalid_currency";
    public const string InvalidOwner = "invalid_owner";

    // Idempotency / request hashing
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string IdempotencyKeyReuse = "idempotency_key_reuse";
    public const string RequestInProgress = "request_in_progress";
}
