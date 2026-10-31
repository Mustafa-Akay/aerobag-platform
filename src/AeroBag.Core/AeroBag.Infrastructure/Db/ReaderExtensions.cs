using System.Data.Common;

namespace AeroBag.Infrastructure.Db;

/// <summary>Boş olabilen sütunları okumak için kısaltmalar (<c>IsDBNull ? null : Get…</c> kalıbının yerine).</summary>
public static class ReaderExtensions
{
    public static string? GetNullableString(this DbDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    public static DateTimeOffset? GetNullableDateTimeOffset(this DbDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetFieldValue<DateTimeOffset>(ordinal);

    public static byte[]? GetNullableBytes(this DbDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetFieldValue<byte[]>(ordinal);
}
