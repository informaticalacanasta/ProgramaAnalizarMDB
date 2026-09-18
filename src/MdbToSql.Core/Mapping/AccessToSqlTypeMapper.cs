using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Mapping;

public static class AccessTypeClassifier
{
    public static bool TryClassify(
        int providerType,
        int? columnSize,
        bool isLong,
        out AccessTypeKind kind)
    {
        switch (providerType)
        {
            case 16:
            case 17:
                kind = AccessTypeKind.Byte;
                return true;
            case 2:
                kind = AccessTypeKind.Integer;
                return true;
            case 3:
                kind = AccessTypeKind.Long;
                return true;
            case 18:
                kind = AccessTypeKind.Long;
                return true;
            case 19:
            case 20:
                kind = AccessTypeKind.BigInt;
                return true;
            case 4:
                kind = AccessTypeKind.Single;
                return true;
            case 5:
                kind = AccessTypeKind.Double;
                return true;
            case 6:
                kind = AccessTypeKind.Currency;
                return true;
            case 14:
            case 131:
            case 139:
                kind = AccessTypeKind.Decimal;
                return true;
            case 11:
                kind = AccessTypeKind.Boolean;
                return true;
            case 7:
            case 64:
            case 133:
            case 134:
            case 135:
                kind = AccessTypeKind.DateTime;
                return true;
            case 72:
                kind = AccessTypeKind.Guid;
                return true;
            case 128:
            case 204:
                kind = isLong || columnSize is null or <= 0 or > 8000
                    ? AccessTypeKind.LongBinary
                    : AccessTypeKind.Binary;
                return true;
            case 205:
                kind = AccessTypeKind.LongBinary;
                return true;
            case 129:
            case 130:
            case 200:
            case 202:
                kind = isLong || columnSize is null or <= 0 or > 4000
                    ? AccessTypeKind.Memo
                    : AccessTypeKind.Text;
                return true;
            case 8:
            case 201:
            case 203:
                kind = AccessTypeKind.Memo;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public static AccessTypeKind ClassifyOrThrow(
        string mdbPath,
        string tableName,
        string columnName,
        int providerType,
        int? columnSize,
        bool isLong,
        string? providerTypeName)
    {
        if (TryClassify(providerType, columnSize, isLong, out var kind))
        {
            return kind;
        }

        var found = string.IsNullOrWhiteSpace(providerTypeName)
            ? $"OleDbType={providerType}"
            : $"{providerTypeName} (OleDbType={providerType})";
        throw new UnsupportedAccessTypeException(mdbPath, tableName, columnName, found);
    }
}

public sealed class AccessToSqlTypeMapper
{
    public SqlTypeDefinition Map(AccessColumnSchema column)
    {
        return column.TypeKind switch
        {
            AccessTypeKind.Byte => new("tinyint"),
            AccessTypeKind.Integer => new("smallint"),
            AccessTypeKind.Long => new("int"),
            AccessTypeKind.BigInt => new("bigint"),
            AccessTypeKind.Single => new("real"),
            AccessTypeKind.Double => new("float"),
            AccessTypeKind.Currency => new("decimal", Precision: 19, Scale: 4),
            AccessTypeKind.Decimal => new(
                "decimal",
                Precision: NormalizePrecision(column.Precision),
                Scale: NormalizeScale(column.Precision, column.Scale)),
            AccessTypeKind.Boolean => new("bit"),
            AccessTypeKind.DateTime => new("datetime2"),
            AccessTypeKind.Text => new("nvarchar", NormalizeTextLength(column.Size)),
            AccessTypeKind.Memo => new("nvarchar", -1),
            AccessTypeKind.Binary => new("varbinary", NormalizeBinaryLength(column.Size)),
            AccessTypeKind.LongBinary => new("varbinary", -1),
            AccessTypeKind.Guid => new("uniqueidentifier"),
            _ => throw new UnsupportedAccessTypeException(
                string.Empty,
                string.Empty,
                column.Name,
                column.SourceTypeName)
        };
    }

    public Type GetClrType(AccessColumnSchema column)
    {
        return column.TypeKind switch
        {
            AccessTypeKind.Byte => typeof(byte),
            AccessTypeKind.Integer => typeof(short),
            AccessTypeKind.Long => typeof(int),
            AccessTypeKind.BigInt => typeof(long),
            AccessTypeKind.Single => typeof(float),
            AccessTypeKind.Double => typeof(double),
            AccessTypeKind.Currency or AccessTypeKind.Decimal => typeof(decimal),
            AccessTypeKind.Boolean => typeof(bool),
            AccessTypeKind.DateTime => typeof(DateTime),
            AccessTypeKind.Text or AccessTypeKind.Memo => typeof(string),
            AccessTypeKind.Binary or AccessTypeKind.LongBinary => typeof(byte[]),
            AccessTypeKind.Guid => typeof(Guid),
            _ => typeof(object)
        };
    }

    private static int NormalizeTextLength(int? length)
    {
        return length is > 0 and <= 4000 ? length.Value : -1;
    }

    private static int NormalizeBinaryLength(int? length)
    {
        return length is > 0 and <= 8000 ? length.Value : -1;
    }

    private static byte NormalizePrecision(byte? precision)
    {
        return precision is > 0 and <= 38 ? precision.Value : (byte)18;
    }

    private static byte NormalizeScale(byte? precision, byte? scale)
    {
        var normalizedPrecision = NormalizePrecision(precision);
        return scale is not null && scale <= normalizedPrecision
            ? scale.Value
            : (byte)0;
    }
}
