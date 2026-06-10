using System.Buffers.Text;

namespace MoneyTransfer.Api.Infrastructure.Pagination;

/// <summary>
/// Opaque keyset cursor over the monotonically increasing ledger-entry id.
/// Encodes the last-seen id as URL-safe base64 so clients treat it as opaque.
/// </summary>
public static class Cursor
{
    public static string Encode(long lastId) => Base64Url.EncodeToString(BitConverter.GetBytes(lastId));

    public static long? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            return bytes.Length == sizeof(long) ? BitConverter.ToInt64(bytes) : null;
        }
        catch
        {
            return null; // malformed cursor => treat as "from the beginning"
        }
    }
}
