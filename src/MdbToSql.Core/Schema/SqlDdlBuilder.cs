using MdbToSql.Core.Mapping;
using MdbToSql.Core.Models;
using MdbToSql.Core.Utilities;

namespace MdbToSql.Core.Schema;

public sealed class SqlDdlBuilder
{
    private readonly AccessToSqlTypeMapper _typeMapper;

    public SqlDdlBuilder(AccessToSqlTypeMapper typeMapper)
    {
        _typeMapper = typeMapper;
    }

    public string BuildCreateTableSql(AccessTableSchema table, string destinationTableName)
    {
        var primaryColumns = table.Indexes
            .FirstOrDefault(index => index.IsPrimaryKey)
            ?.Columns
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];

        var columnDefinitions = table.Columns
            .OrderBy(column => column.Ordinal)
            .Select(column =>
            {
                var identity = column.IsAutoIncrement ? " IDENTITY(1,1)" : string.Empty;
                var notNull = primaryColumns.Contains(column.Name) || !column.IsNullable;
                var nullability = notNull ? " NOT NULL" : " NULL";
                return $"    {SqlIdentifier.Quote(column.Name)} " +
                    $"{_typeMapper.Map(column).ToSql()}{identity}{nullability}";
            });

        return $"CREATE TABLE dbo.{SqlIdentifier.Quote(destinationTableName)}{Environment.NewLine}" +
            $"({Environment.NewLine}{string.Join($",{Environment.NewLine}", columnDefinitions)}" +
            $"{Environment.NewLine});";
    }

    public IReadOnlyList<string> BuildCreateIndexSql(
        AccessTableSchema table,
        string destinationTableName,
        string? uniqueNameSuffix = null)
    {
        var statements = new List<string>();
        foreach (var index in table.Indexes)
        {
            var canonical = StagingNames.CanonicalIndexName(
                table.Name,
                index.Name,
                index.IsPrimaryKey);
            var indexName = StagingNames.SuffixedIndex(canonical, uniqueNameSuffix);
            var columns = string.Join(
                ", ",
                index.Columns
                    .OrderBy(column => column.Ordinal)
                    .Select(column =>
                        $"{SqlIdentifier.Quote(column.Name)}" +
                        (column.IsDescending ? " DESC" : " ASC")));

            if (index.IsPrimaryKey)
            {
                statements.Add(
                    $"ALTER TABLE dbo.{SqlIdentifier.Quote(destinationTableName)} " +
                    $"ADD CONSTRAINT {SqlIdentifier.Quote(indexName)} PRIMARY KEY ({columns});");
                continue;
            }

            var unique = index.IsUnique ? "UNIQUE " : string.Empty;
            var filter = index.IsUnique
                ? BuildUniqueNullFilter(table, index)
                : null;
            statements.Add(
                $"CREATE {unique}INDEX {SqlIdentifier.Quote(indexName)} ON " +
                $"dbo.{SqlIdentifier.Quote(destinationTableName)} ({columns})" +
                $"{filter};");
        }

        return statements;
    }

    private static string? BuildUniqueNullFilter(
        AccessTableSchema table,
        AccessIndexSchema index)
    {
        var nullableColumns = index.Columns
            .Where(indexColumn => table.Columns.Any(column =>
                string.Equals(column.Name, indexColumn.Name, StringComparison.OrdinalIgnoreCase) &&
                column.IsNullable))
            .Select(indexColumn => SqlIdentifier.Quote(indexColumn.Name))
            .ToArray();
        if (nullableColumns.Length == 0)
        {
            return null;
        }

        return " WHERE " + string.Join(
            " AND ",
            nullableColumns.Select(column => $"{column} IS NOT NULL"));
    }
}
