using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MoneyTransfer.Api.Domain;
using MoneyTransfer.Api.Infrastructure.Movement;

namespace MoneyTransfer.Api.Infrastructure.Persistence;

/// <summary>
/// Seeds per-currency system accounts and a few demo user accounts. User accounts are funded via real
/// deposit movements (double-entry) so the ledger stays self-consistent from the very first row —
/// the system account balances go negative to offset the credited users, keeping the global sum at zero.
/// </summary>
public static class DbSeeder
{
    // Fixed ids so manual testing, docs and the .http file can reference accounts by a stable value.
    public static readonly Guid Alice = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Bob = new("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Carol = new("33333333-3333-3333-3333-333333333333");

    public static async Task SeedAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var db = sp.GetRequiredService<LedgerDbContext>();
        if (await db.Accounts.AnyAsync(ct)) return;

        var options = sp.GetRequiredService<IOptions<LedgerOptions>>().Value;
        var now = DateTimeOffset.UtcNow;

        // Distinct + fallback: defend against duplicate/empty currency configuration.
        var currencies = (options.Currencies.Length > 0 ? options.Currencies : new[] { "USD", "EUR", "TRY" }).Distinct();
        foreach (var currency in currencies)
            db.Accounts.Add(Account.Create($"system:{currency}", currency, allowsNegative: true, isSystem: true, now));

        db.Accounts.Add(Account.Create("user:alice", "USD", allowsNegative: false, isSystem: false, now, Alice));
        db.Accounts.Add(Account.Create("user:bob", "USD", allowsNegative: false, isSystem: false, now, Bob));
        db.Accounts.Add(Account.Create("user:carol", "EUR", allowsNegative: false, isSystem: false, now, Carol));
        await db.SaveChangesAsync(ct);

        // Fund demo accounts through the same engine real requests use (system -> user).
        var ledger = sp.GetRequiredService<LedgerService>();
        await ledger.DepositAsync(Alice, 100_000, "seed funding", ct); // 1,000.00 USD
        await ledger.DepositAsync(Carol, 50_000, "seed funding", ct);  //   500.00 EUR
    }
}
