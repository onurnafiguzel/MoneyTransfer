using System.Security.Cryptography;
using System.Text;

namespace MoneyTransfer.Api.Infrastructure.Idempotency;

/// <summary>
/// Computes the canonical SHA256 of a logical write request. The hash is the idempotency collision guard:
/// the same <c>Idempotency-Key</c> replayed with identical content hashes equal (idempotent replay), while
/// the same key with a changed field hashes differently (key reuse → rejected). The canonical input has a
/// fixed field order per endpoint and DELIBERATELY excludes any server timestamp — a value that varies per
/// call would make the hash never match, so dedup would never trigger. Stateless (singleton).
/// </summary>
public sealed class RequestHasher
{
    public string ForTransfer(Guid from, Guid to, long amount, string? reason) =>
        Hash($"POST|/transfers|{from}|{to}|{amount}|{Norm(reason)}");

    public string ForDeposit(Guid account, long amount, string? reason) =>
        Hash($"POST|/deposits|{account}|{amount}|{Norm(reason)}");

    public string ForWithdrawal(Guid account, long amount, string? reason) =>
        Hash($"POST|/withdrawals|{account}|{amount}|{Norm(reason)}");

    public string ForReversal(Guid txId, string? reason) =>
        Hash($"POST|/transfers/{txId}/reverse|{Norm(reason)}");

    // null and empty reason hash identically; the '|' separators are fixed so fields can't run together.
    private static string Norm(string? reason) => reason ?? string.Empty;

    private static string Hash(string canonical)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant(); // 64-char lowercase hex → transfers.request_hash
    }
}
