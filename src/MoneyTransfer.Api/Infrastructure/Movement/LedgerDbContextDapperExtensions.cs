using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MoneyTransfer.Api.Infrastructure.Persistence;
using Npgsql;

namespace MoneyTransfer.Api.Infrastructure.Movement;

internal static class LedgerDbContextDapperExtensions
{
    /// <summary>
    /// Exposes the EF connection and the ambient transaction so Dapper participates in the SAME DB
    /// transaction EF opened (the basis of atomicity for the hybrid path). An active transaction is required
    /// — LedgerService begins one per movement before invoking a mutator.
    /// </summary>
    public static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) GetDapperContext(this LedgerDbContext db)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var current = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("An active transaction is required before mutating balances.");
        return (connection, (NpgsqlTransaction)current.GetDbTransaction());
    }
}
