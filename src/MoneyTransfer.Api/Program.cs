using Microsoft.EntityFrameworkCore;
using MoneyTransfer.Api.Features.Accounts;
using MoneyTransfer.Api.Features.Deposits;
using MoneyTransfer.Api.Features.Transfers;
using MoneyTransfer.Api.Features.Withdrawals;
using MoneyTransfer.Api.Infrastructure;
using MoneyTransfer.Api.Infrastructure.Movement;
using MoneyTransfer.Api.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LedgerOptions>(builder.Configuration.GetSection(LedgerOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("Ledger")
    ?? "Host=localhost;Port=5432;Database=ledger;Username=ledger;Password=ledger";

builder.Services.AddDbContext<LedgerDbContext>(o =>
    o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

// Concurrency strategies (swappable behind IBalanceMutator; selection is a config decision).
builder.Services.AddHttpContextAccessor();
builder.Services.AddKeyedScoped<IBalanceMutator, NaiveMutator>("naive");
builder.Services.AddKeyedScoped<IBalanceMutator, PessimisticMutator>("pessimistic");
builder.Services.AddKeyedScoped<IBalanceMutator, ConditionalUpdateMutator>("conditional");
builder.Services.AddKeyedScoped<IBalanceMutator, OptimisticCasMutator>("optimistic");
builder.Services.AddScoped<IBalanceMutatorResolver, BalanceMutatorResolver>();

builder.Services.AddScoped<LedgerService>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();

// Apply migrations and seed demo data on startup (development convenience).
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(scope.ServiceProvider);
}

app.MapGet("/", () => Results.Ok(new { service = "MoneyTransfer", phase = 1, status = "ok" }));
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// Vertical-slice endpoints
app.MapCreateAccount();
app.MapCreateTransfer();
app.MapGetTransfer();
app.MapReverseTransfer();
app.MapGetBalance();
app.MapGetEntries();
app.MapCreateDeposit();
app.MapCreateWithdrawal();

app.Run();
