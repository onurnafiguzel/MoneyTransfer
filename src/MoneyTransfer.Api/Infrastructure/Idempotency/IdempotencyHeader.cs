namespace MoneyTransfer.Api.Infrastructure.Idempotency;

/// <summary>The HTTP header carrying the client-supplied idempotency key on write requests.</summary>
public static class IdempotencyHeader
{
    public const string Name = "Idempotency-Key";

    /// <summary>Reads the Idempotency-Key header value (trimmed); empty string when absent.</summary>
    public static string IdempotencyKeyValue(this IHeaderDictionary headers) =>
        headers.TryGetValue(Name, out var v) ? v.ToString().Trim() : string.Empty;
}
