namespace MdbToSql.Core.Import;

public static class ProtectedInfrastructureTables
{
    public static readonly IReadOnlyList<string> Names =
    [
        "SchemaMigrations",
        "MdbImportHistory",
        "MdbImportRun",
        "MdbImportTableHistory"
    ];

    public static bool Contains(string tableName)
    {
        return Names.Contains(tableName, StringComparer.OrdinalIgnoreCase);
    }
}
