using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MoneyTransfer.Api.Infrastructure.Idempotency;

public enum RedisIdemState
{
    /// <summary>Redis is not configured or unreachable — caller must fall back to the DB backstop.</summary>
    Unavailable,

    /// <summary>We won the lock (no prior entry) — caller proceeds and must Complete/Release afterwards.</summary>
    Acquired,

    /// <summary>Another request holds the in_progress lock for this key — reject as request_in_progress.</summary>
    InProgress,

    /// <summary>A completed result is cached — replay it (carries the original txId/createdAt).</summary>
    Completed,

    /// <summary>An entry exists but its request hash differs — the key was reused with a different payload.</summary>
    HashMismatch,
}

public readonly record struct RedisIdemResult(RedisIdemState State, Guid TxId, DateTimeOffset CreatedAt)
{
    public static readonly RedisIdemResult Unavailable = new(RedisIdemState.Unavailable, default, default);
    public static readonly RedisIdemResult Acquired = new(RedisIdemState.Acquired, default, default);
    public static readonly RedisIdemResult InProgress = new(RedisIdemState.InProgress, default, default);
    public static readonly RedisIdemResult HashMismatch = new(RedisIdemState.HashMismatch, default, default);
    public static RedisIdemResult Completed(Guid txId, DateTimeOffset createdAt) =>
        new(RedisIdemState.Completed, txId, createdAt);
}

/// <summary>
/// Redis-backed idempotency fast path (Step B). Implements a small state machine per Idempotency-Key:
///   <c>SET idem:{key} {hash, in_progress} NX EX lockTtl</c> — first request wins the lock and proceeds;
///   a concurrent copy sees in_progress (→409) or, once the winner finishes, the cached completed result (→replay).
///
/// This is a PERFORMANCE + UX layer, never the source of truth: every Redis operation is best-effort and any
/// connection error degrades to <see cref="RedisIdemState.Unavailable"/>, so the caller falls through to the
/// permanent DB unique-index backstop. Redis being down (or a key evicted) never breaks correctness.
/// </summary>
public sealed class RedisIdempotencyStore(
    IConnectionMultiplexer? multiplexer,
    IOptions<LedgerOptions> options,
    ILogger<RedisIdempotencyStore> logger)
{
    private readonly TimeSpan _lockTtl = TimeSpan.FromSeconds(options.Value.IdempotencyLockTtlSeconds);
    private readonly TimeSpan _completedTtl = TimeSpan.FromSeconds(options.Value.IdempotencyCompletedTtlSeconds);

    public bool Enabled => multiplexer is { IsConnected: true };

    private static string Key(string idempotencyKey) => $"idem:{idempotencyKey}";

    /// <summary>Try to acquire the in_progress lock, or report the existing entry's state.</summary>
    public async Task<RedisIdemResult> BeginAsync(string key, string hash)
    {
        if (multiplexer is null) return RedisIdemResult.Unavailable;
        try
        {
            var db = multiplexer.GetDatabase();
            var redisKey = Key(key);
            var inProgress = JsonSerializer.Serialize(new Entry(hash, StatusInProgress, null, null));

            // Atomic claim: only the first request sets the key (NX) and proceeds.
            if (await db.StringSetAsync(redisKey, inProgress, _lockTtl, When.NotExists))
                return RedisIdemResult.Acquired;

            // Key already present — inspect it.
            var raw = await db.StringGetAsync(redisKey);
            if (raw.IsNullOrEmpty) return RedisIdemResult.Unavailable; // raced expiry; let the DB decide
            return Interpret(raw!, hash);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis idempotency Begin failed; degrading to DB backstop");
            return RedisIdemResult.Unavailable;
        }
    }

    /// <summary>Overwrite the lock with the completed result so future replays are served from Redis.</summary>
    public async Task CompleteAsync(string key, string hash, Guid txId, DateTimeOffset createdAt)
    {
        if (multiplexer is null) return;
        try
        {
            var value = JsonSerializer.Serialize(new Entry(hash, StatusCompleted, txId, createdAt));
            await multiplexer.GetDatabase().StringSetAsync(Key(key), value, _completedTtl);
        }
        catch (RedisException ex)
        {
            // Non-fatal: the transfer is already durably committed; the DB backstop will serve replays.
            logger.LogWarning(ex, "Redis idempotency Complete failed; DB backstop remains authoritative");
        }
    }

    /// <summary>Release the in_progress lock after a non-success outcome so retries aren't blocked.</summary>
    public async Task ReleaseAsync(string key)
    {
        if (multiplexer is null) return;
        try
        {
            await multiplexer.GetDatabase().KeyDeleteAsync(Key(key));
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Redis idempotency Release failed; lock will self-expire");
        }
    }

    private static RedisIdemResult Interpret(string raw, string hash)
    {
        Entry? entry;
        try { entry = JsonSerializer.Deserialize<Entry>(raw); }
        catch (JsonException) { return RedisIdemResult.Unavailable; }
        if (entry is null) return RedisIdemResult.Unavailable;

        if (!string.Equals(entry.H, hash, StringComparison.Ordinal)) return RedisIdemResult.HashMismatch;
        return entry.S == StatusCompleted && entry.T is { } txId
            ? RedisIdemResult.Completed(txId, entry.C ?? default)
            : RedisIdemResult.InProgress;
    }

    private const string StatusInProgress = "in_progress";
    private const string StatusCompleted = "completed";

    // Compact JSON shape stored in Redis: h=hash, s=status, t=txId, c=createdAt.
    private sealed record Entry(string H, string S, Guid? T, DateTimeOffset? C);
}
