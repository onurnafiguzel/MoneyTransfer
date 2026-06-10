using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MoneyTransfer.Api.Features.Accounts;
using MoneyTransfer.Api.Features.Deposits;
using MoneyTransfer.Api.Features.Metrics;
using MoneyTransfer.Api.Features.Transfers;
using MoneyTransfer.Api.Features.Withdrawals;
using MoneyTransfer.Api.Infrastructure;
using MoneyTransfer.Api.Infrastructure.Idempotency;
using MoneyTransfer.Api.Infrastructure.Movement;
using MoneyTransfer.Api.Infrastructure.Persistence;
using StackExchange.Redis;

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

// Process-wide contention counters (retries/deadlocks/serialization/exhaustions) — observability for the
// retry policy, so load tests can prove deadlocks happened and were recovered.
builder.Services.AddSingleton<ContentionMetrics>();

builder.Services.AddScoped<LedgerService>();

// Idempotency: request hashing + DB backstop (Step A) with an optional Redis fast path (Step B).
builder.Services.AddSingleton<RequestHasher>();
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    var redisOptions = ConfigurationOptions.Parse(redisConnection);
    redisOptions.AbortOnConnectFail = false; // don't crash on startup if Redis is briefly unavailable; degrade gracefully
    builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisOptions));
}
// Resolve the multiplexer optionally: absent (no Redis configured) → the store reports Unavailable and the
// coordinator runs on the DB backstop alone.
builder.Services.AddSingleton(sp => new RedisIdempotencyStore(
    sp.GetService<IConnectionMultiplexer>(),
    sp.GetRequiredService<IOptions<LedgerOptions>>(),
    sp.GetRequiredService<ILogger<RedisIdempotencyStore>>()));
builder.Services.AddScoped<IdempotencyService>();

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
app.MapGetContentionMetrics();

app.Run();
