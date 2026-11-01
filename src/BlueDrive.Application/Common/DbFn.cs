namespace BlueDrive.Application.Common;

/// <summary>SQL functions usable in LINQ queries (mapped in AppDbContext).</summary>
public static class DbFn
{
    /// <summary>Maps to f_unaccent(text): lowercase + strip Vietnamese diacritics.</summary>
    public static string Unaccent(string? input) => throw new NotSupportedException("Chỉ dùng trong truy vấn LINQ tới CSDL.");
}
