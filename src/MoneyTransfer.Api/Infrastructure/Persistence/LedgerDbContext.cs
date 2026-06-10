using Microsoft.EntityFrameworkCore;
using MoneyTransfer.Api.Domain;

namespace MoneyTransfer.Api.Infrastructure.Persistence;

/// <summary>
/// EF Core model + migrations for the ledger. Column/table names are snake_case
/// (UseSnakeCaseNamingConvention), so the raw SQL in CHECK constraints and partial index
/// filters below references snake_case identifiers.
/// </summary>
public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Balance> Balances => Set<Balance>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Entities are encapsulated (private/init setters, factory methods, read-only Entries collection).
        // Tell EF to read/write through backing fields so it can materialize them without public setters.
        b.UsePropertyAccessMode(PropertyAccessMode.Field);

        b.Entity<Account>(e =>
        {
            e.ToTable("accounts", t =>
            {
                t.HasCheckConstraint("currency_upper", "currency = upper(currency)");
                t.HasCheckConstraint("currency_alpha", "currency ~ '^[A-Z]{3}$'");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.OwnerRef).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            e.Property(x => x.AllowsNegative).HasDefaultValue(false);
            e.Property(x => x.IsSystem).HasDefaultValue(false);
            e.HasIndex(x => x.Currency);
            e.HasOne(x => x.Balance).WithOne(x => x.Account).HasForeignKey<Balance>(x => x.AccountId);
        });

        b.Entity<Balance>(e =>
        {
            e.ToTable("balances");
            e.HasKey(x => x.AccountId);
            e.Property(x => x.AccountId).ValueGeneratedNever();
            e.Property(x => x.Amount).HasDefaultValue(0L);
            e.Property(x => x.Version).HasDefaultValue(0L);
            // NOTE (Phase 1): Version is a plain counter, NOT a concurrency token — the naive
            // read-modify-write is intentionally race-prone. Optimistic CAS is a later feature.
        });

        b.Entity<Transfer>(e =>
        {
            e.ToTable("transfers", t =>
                t.HasCheckConstraint("kind_valid", "kind IN ('transfer','deposit','withdrawal','reversal')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind)
                .HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<TransferKind>(v, true))
                .HasColumnType("text")
                .IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength();

            e.HasOne<Transfer>().WithMany().HasForeignKey(x => x.ReversedTxId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ReversedTxId).IsUnique()
                .HasFilter("reversed_tx_id IS NOT NULL").HasDatabaseName("ux_transfers_reversed_once");
            e.HasIndex(x => x.IdempotencyKey).IsUnique()
                .HasFilter("idempotency_key IS NOT NULL").HasDatabaseName("ux_transfers_idem");
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries", t =>
            {
                t.HasCheckConstraint("amount_nonzero", "amount <> 0");
                t.HasCheckConstraint("amount_bounded", "abs(amount) BETWEEN 1 AND 1000000000000000");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();

            e.HasOne(x => x.Transfer).WithMany(t => t.Entries).HasForeignKey(x => x.TxId);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.AccountId, x.Id }).IsDescending(false, true)
                .HasDatabaseName("ix_entries_account_id_desc");
            e.HasIndex(x => x.TxId).HasDatabaseName("ix_entries_tx");
        });
    }
}
