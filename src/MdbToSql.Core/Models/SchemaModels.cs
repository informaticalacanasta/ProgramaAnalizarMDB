namespace MdbToSql.Core.Models;

public enum AccessTypeKind
{
    Byte,
    Integer,
    Long,
    BigInt,
    Single,
    Double,
    Currency,
    Decimal,
    Boolean,
    DateTime,
    Text,
    Memo,
    Binary,
    LongBinary,
    Guid
}

public sealed record AccessTableSchema(
    string Name,
    IReadOnlyList<AccessColumnSchema> Columns,
    IReadOnlyList<AccessIndexSchema> Indexes,
    long RowCount = 0);

public sealed record AccessColumnSchema(
    string Name,
    AccessTypeKind TypeKind,
    string SourceTypeName,
    int ProviderType,
    int? Size,
    byte? Precision,
    byte? Scale,
    int Ordinal,
    bool IsNullable,
    bool IsAutoIncrement);

public sealed record AccessIndexSchema(
    string Name,
    bool IsUnique,
    bool IsPrimaryKey,
    IReadOnlyList<AccessIndexColumn> Columns);

public sealed record AccessIndexColumn(
    string Name,
    int Ordinal,
    bool IsDescending);

public sealed record SqlTypeDefinition(
    string TypeName,
    int? MaxLength = null,
    byte? Precision = null,
    byte? Scale = null)
{
    public string ToSql()
    {
        return TypeName switch
        {
            "nvarchar" or "varbinary" =>
                $"{TypeName}({(MaxLength is null or < 0 ? "max" : MaxLength.Value)})",
            "decimal" => $"decimal({Precision ?? 18},{Scale ?? 0})",
            _ => TypeName
        };
    }
}

public sealed record SqlColumnSchema(
    string Name,
    SqlTypeDefinition Type,
    bool IsNullable,
    bool IsIdentity,
    int Ordinal);

public sealed record SqlTableSchema(
    string Name,
    IReadOnlyList<SqlColumnSchema> Columns,
    IReadOnlyList<AccessIndexSchema> Indexes);
