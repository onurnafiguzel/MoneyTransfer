using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MoneyTransfer.Api.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by `dotnet ef` (migrations) so the tooling can build the model
/// without a running database or runtime configuration. Must mirror the runtime options
/// (snake_case convention) so generated migrations match the runtime model.
/// </summary>
public sealed class LedgerDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=ledger;Username=ledger;Password=ledger")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new LedgerDbContext(options);
    }
}
