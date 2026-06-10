using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneyTransfer.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_ref = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    allows_negative = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("currency_alpha", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("currency_upper", "currency = upper(currency)");
                });

            migrationBuilder.CreateTable(
                name: "transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    reversed_tx_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: true),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transfers", x => x.id);
                    table.CheckConstraint("kind_valid", "kind IN ('transfer','deposit','withdrawal','reversal')");
                    table.ForeignKey(
                        name: "fk_transfers_transfers_reversed_tx_id",
                        column: x => x.reversed_tx_id,
                        principalTable: "transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "balances",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_balances", x => x.account_id);
                    table.ForeignKey(
                        name: "fk_balances_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    balance_after = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => x.id);
                    table.CheckConstraint("amount_bounded", "abs(amount) BETWEEN 1 AND 1000000000000000");
                    table.CheckConstraint("amount_nonzero", "amount <> 0");
                    table.ForeignKey(
                        name: "fk_ledger_entries_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ledger_entries_transfers_tx_id",
                        column: x => x.tx_id,
                        principalTable: "transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_currency",
                table: "accounts",
                column: "currency");

            migrationBuilder.CreateIndex(
                name: "ix_entries_account_id_desc",
                table: "ledger_entries",
                columns: new[] { "account_id", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_entries_tx",
                table: "ledger_entries",
                column: "tx_id");

            migrationBuilder.CreateIndex(
                name: "ix_transfers_created_at",
                table: "transfers",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ux_transfers_idem",
                table: "transfers",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_transfers_reversed_once",
                table: "transfers",
                column: "reversed_tx_id",
                unique: true,
                filter: "reversed_tx_id IS NOT NULL");

            // Append-only immutability: reject UPDATE/DELETE on the ledger tables at the database level.
            // Corrections happen only via compensating reversal transfers, never by mutation/deletion.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION prevent_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'append-only ledger: % on % is not permitted', TG_OP, TG_TABLE_NAME;
END;
$$ LANGUAGE plpgsql;");
            migrationBuilder.Sql(@"
CREATE TRIGGER trg_ledger_entries_immutable
    BEFORE UPDATE OR DELETE ON ledger_entries
    FOR EACH ROW EXECUTE FUNCTION prevent_mutation();");
            migrationBuilder.Sql(@"
CREATE TRIGGER trg_transfers_immutable
    BEFORE UPDATE OR DELETE ON transfers
    FOR EACH ROW EXECUTE FUNCTION prevent_mutation();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_transfers_immutable ON transfers;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_ledger_entries_immutable ON ledger_entries;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS prevent_mutation();");

            migrationBuilder.DropTable(
                name: "balances");

            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "transfers");
        }
    }
}
