using System.Data;
using System.Data.OleDb;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Mapping;
using MdbToSql.Core.Models;
using MdbToSql.Core.Utilities;

namespace MdbToSql.Access;

public sealed class AccessSchemaReader
{
    private const int DbColumnFlagsIsLong = 0x80;

    public IReadOnlyList<string> ListUserTables(OleDbConnection connection)
    {
        using var tables = connection.GetOleDbSchemaTable(
            OleDbSchemaGuid.Tables,
            [null, null, null, "TABLE"]);
        if (tables is null)
        {
            return [];
        }

        var names = new List<string>();
        foreach (DataRow row in tables.Rows)
        {
            var name = Convert.ToString(row["TABLE_NAME"]);
            if (string.IsNullOrWhiteSpace(name) || IsSystemTable(name))
            {
                continue;
            }

            names.Add(name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public IReadOnlyList<AccessColumnSchema> ReadColumns(
        OleDbConnection connection,
        string mdbPath,
        string tableName)
    {
        var schemaInfo = ReadSchemaTable(connection, tableName);
        using var columns = connection.GetOleDbSchemaTable(
            OleDbSchemaGuid.Columns,
            [null, null, tableName, null]);
        if (columns is null)
        {
            throw new SchemaReadException(
                $"OleDb no devolvió columnas para '{tableName}' en '{Path.GetFileName(mdbPath)}'.");
        }

        var result = new List<AccessColumnSchema>();
        foreach (DataRow row in columns.Rows)
        {
            var name = Convert.ToString(row["COLUMN_NAME"])
                ?? throw new SchemaReadException($"Una columna de '{tableName}' no tiene nombre.");
            var ordinal = Convert.ToInt32(row["ORDINAL_POSITION"]) - 1;
            var providerType = Convert.ToInt32(row["DATA_TYPE"]);
            var size = ReadNullableInt(row, "CHARACTER_MAXIMUM_LENGTH");
            var precision = ReadNullableByte(row, "NUMERIC_PRECISION");
            var scale = ReadNullableByte(row, "NUMERIC_SCALE");
            var flags = ReadNullableInt(row, "COLUMN_FLAGS") ?? 0;
            var isLong = (flags & DbColumnFlagsIsLong) != 0;
            var nullable = ReadNullableFlag(row, "IS_NULLABLE");
            schemaInfo.TryGetValue(name, out var extra);

            var effectiveSize = extra?.ColumnSize ?? size;
            var effectiveLong = isLong || extra?.IsLong == true;
            var kind = AccessTypeClassifier.ClassifyOrThrow(
                mdbPath,
                tableName,
                name,
                extra?.ProviderType ?? providerType,
                effectiveSize,
                effectiveLong,
                extra?.DataTypeName);
            if (kind is AccessTypeKind.Text or AccessTypeKind.Memo)
            {
                effectiveSize = extra?.ColumnSize ?? size;
            }

            result.Add(new AccessColumnSchema(
                name,
                kind,
                FormatSourceType(kind, extra?.DataTypeName, extra?.ProviderType ?? providerType),
                extra?.ProviderType ?? providerType,
                effectiveSize,
                extra?.Precision ?? precision,
                extra?.Scale ?? scale,
                extra?.Ordinal ?? ordinal,
                extra?.AllowDbNull ?? nullable,
                extra?.IsAutoIncrement ?? false));
        }

        return result
            .OrderBy(column => column.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, ColumnExtra> ReadSchemaTable(
        OleDbConnection connection,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {AccessIdentifier.Quote(tableName)} WHERE 1=0";
        using var reader = command.ExecuteReader(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo);
        using var schema = reader.GetSchemaTable();
        var extras = new Dictionary<string, ColumnExtra>(StringComparer.OrdinalIgnoreCase);
        if (schema is null)
        {
            return extras;
        }

        foreach (DataRow row in schema.Rows)
        {
            var name = Convert.ToString(row["ColumnName"]);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            extras[name] = new ColumnExtra(
                Convert.ToInt32(row["ColumnOrdinal"]),
                ReadNullableInt(row, "ColumnSize"),
                ReadNullableByte(row, "NumericPrecision"),
                ReadNullableByte(row, "NumericScale"),
                ReadBool(row, "AllowDBNull", true),
                ReadBool(row, "IsAutoIncrement", false),
                ReadBool(row, "IsLong", false),
                ReadNullableInt(row, "ProviderType") ?? 0,
                row.Table.Columns.Contains("DataType")
                    ? ((Type?)row["DataType"])?.Name
                    : null);
        }

        return extras;
    }

    public static bool IsSystemTable(string tableName)
    {
        return tableName.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)
            || tableName.StartsWith("~", StringComparison.Ordinal);
    }

    private static string FormatSourceType(AccessTypeKind kind, string? dataTypeName, int providerType)
    {
        var suffix = string.IsNullOrWhiteSpace(dataTypeName)
            ? $"OleDbType={providerType}"
            : dataTypeName;
        return $"{kind} ({suffix})";
    }

    private static int? ReadNullableInt(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] is DBNull)
        {
            return null;
        }

        return Convert.ToInt32(row[column]);
    }

    private static byte? ReadNullableByte(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] is DBNull)
        {
            return null;
        }

        return Convert.ToByte(row[column]);
    }

    private static bool ReadNullableFlag(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column) || row[column] is DBNull)
        {
            return true;
        }

        var value = row[column];
        return value switch
        {
            bool flag => flag,
            string text => text.Equals("YES", StringComparison.OrdinalIgnoreCase)
                || text.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || text == "1",
            _ => Convert.ToBoolean(value)
        };
    }

    private static bool ReadBool(DataRow row, string column, bool fallback)
    {
        if (!row.Table.Columns.Contains(column) || row[column] is DBNull)
        {
            return fallback;
        }

        return Convert.ToBoolean(row[column]);
    }

    private sealed record ColumnExtra(
        int Ordinal,
        int? ColumnSize,
        byte? Precision,
        byte? Scale,
        bool AllowDbNull,
        bool IsAutoIncrement,
        bool IsLong,
        int ProviderType,
        string? DataTypeName);
}
