using System.Data;
using System.Data.OleDb;
using MdbToSql.Core.Models;

namespace MdbToSql.Access;

public sealed class AccessIndexReader
{
    public IReadOnlyList<AccessIndexSchema> ReadIndexes(OleDbConnection connection, string tableName)
    {
        var rows = new List<IndexRow>();
        using (var indexes = connection.GetOleDbSchemaTable(
                   OleDbSchemaGuid.Indexes,
                   [null, null, null, null, tableName]))
        {
            if (indexes is not null)
            {
                foreach (DataRow row in indexes.Rows)
                {
                    if (row.Table.Columns.Contains("TABLE_NAME") &&
                        row["TABLE_NAME"] is not DBNull &&
                        !string.Equals(
                            Convert.ToString(row["TABLE_NAME"]),
                            tableName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var indexName = Convert.ToString(row["INDEX_NAME"]);
                    var columnName = Convert.ToString(row["COLUMN_NAME"]);
                    if (string.IsNullOrWhiteSpace(indexName) || string.IsNullOrWhiteSpace(columnName))
                    {
                        continue;
                    }

                    rows.Add(new(
                        indexName,
                        ReadBool(row, "UNIQUE"),
                        ReadBool(row, "PRIMARY_KEY"),
                        columnName,
                        ReadOrdinal(row),
                        IsDescending(row)));
                }
            }
        }

        using (var primaryKeys = connection.GetOleDbSchemaTable(
                   OleDbSchemaGuid.Primary_Keys,
                   [null, null, tableName]))
        {
            if (primaryKeys is not null)
            {
                foreach (DataRow row in primaryKeys.Rows)
                {
                    var columnName = Convert.ToString(row["COLUMN_NAME"]);
                    if (string.IsNullOrWhiteSpace(columnName))
                    {
                        continue;
                    }

                    var indexName = Convert.ToString(row["PK_NAME"]);
                    if (string.IsNullOrWhiteSpace(indexName))
                    {
                        indexName = "PrimaryKey";
                    }

                    rows.Add(new(
                        indexName,
                        true,
                        true,
                        columnName,
                        ReadOrdinal(row),
                        false));
                }
            }
        }

        var grouped = rows
            .GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AccessIndexSchema(
                group.Key,
                group.Any(row => row.IsUnique || row.IsPrimaryKey),
                group.Any(row => row.IsPrimaryKey),
                group
                    .GroupBy(row => row.ColumnName, StringComparer.OrdinalIgnoreCase)
                    .Select(columnGroup => columnGroup.First())
                    .Select(row => new AccessIndexColumn(row.ColumnName, row.Ordinal, row.IsDescending))
                    .OrderBy(column => column.Ordinal)
                    .ToArray()))
            .Where(index => index.Columns.Count > 0)
            .ToArray();

        return Deduplicate(grouped);
    }

    private static IReadOnlyList<AccessIndexSchema> Deduplicate(
        IReadOnlyList<AccessIndexSchema> indexes)
    {
        var result = new List<AccessIndexSchema>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in indexes.OrderByDescending(item => item.IsPrimaryKey).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var signature =
                $"{index.IsPrimaryKey}:{index.IsUnique}:" +
                string.Join(",", index.Columns.Select(column =>
                    $"{column.Name}:{(column.IsDescending ? "D" : "A")}"));
            if (!seen.Add(signature))
            {
                continue;
            }

            result.Add(index);
        }

        return result;
    }

    private static int ReadOrdinal(DataRow row)
    {
        if (row.Table.Columns.Contains("ORDINAL_POSITION") && row["ORDINAL_POSITION"] is not DBNull)
        {
            return Convert.ToInt32(row["ORDINAL_POSITION"]);
        }

        return 1;
    }

    private static bool IsDescending(DataRow row)
    {
        if (!row.Table.Columns.Contains("COLLATION") || row["COLLATION"] is DBNull)
        {
            return false;
        }

        return Convert.ToInt32(row["COLLATION"]) == 2;
    }

    private static bool ReadBool(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] is DBNull)
        {
            return false;
        }

        return Convert.ToBoolean(row[column]);
    }

    private sealed record IndexRow(
        string Name,
        bool IsUnique,
        bool IsPrimaryKey,
        string ColumnName,
        int Ordinal,
        bool IsDescending);
}
