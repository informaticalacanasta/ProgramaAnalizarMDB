namespace MdbToSql.Core.Models;

public sealed record AccessLocalFieldSchema(
    string Name,
    int Ordinal,
    int? DaoType,
    string DaoTypeName,
    int? Size,
    bool? Required,
    bool AutoIncrement);

public sealed record AccessLocalIndexSchema(
    string Name,
    bool Unique,
    bool Primary,
    IReadOnlyList<string> Fields);

public sealed record AccessLocalTableSchema(
    string Name,
    IReadOnlyList<AccessLocalFieldSchema> Fields,
    IReadOnlyList<AccessLocalIndexSchema> Indexes);

public sealed record AccessVbaFieldMention(
    string Procedure,
    int Line,
    string? RecordsetVariable,
    string? ObjectName,
    string? FieldName,
    int? Ordinal,
    string Role,
    string Evidence,
    bool NameVerified);

public sealed record AccessTableOperation(
    string Procedure,
    int Line,
    string Kind,
    string? ObjectName,
    string Evidence);

public sealed record AccessSeekUse(
    string Procedure,
    int Line,
    string? RecordsetVariable,
    string? ObjectName,
    string? IndexName,
    IReadOnlyList<string> Keys,
    string Evidence);

public sealed record AccessRecepcionTableContract(
    string Name,
    string Kind,
    string? OriginDatabase,
    string? OriginPathKnown,
    AccessLinkKind LinkKind,
    bool IsLinked,
    bool IsUnc,
    string? SourceTableName,
    bool SchemaVerified,
    string? SchemaSourceMdb,
    bool SameVersionUncertain,
    IReadOnlyList<string> UsedBy,
    IReadOnlyList<AccessTableOperation> Operations,
    IReadOnlyList<AccessVbaFieldMention> FieldsMentioned,
    IReadOnlyList<AccessLocalFieldSchema> VerifiedFields,
    IReadOnlyList<AccessSeekUse> Searches,
    IReadOnlyList<string> Pending);

public sealed record AccessTpvFormatDoc(
    string TypeCode,
    string Pattern,
    string ProcessedBy,
    string DirectoryVariable,
    IReadOnlyList<string> DateFromName,
    IReadOnlyList<string> StoreFromName,
    IReadOnlyList<string> Separators,
    IReadOnlyList<string> AuxiliaryFiles,
    IReadOnlyList<string> Companions,
    IReadOnlyList<string> Demonstrated,
    IReadOnlyList<string> Pending);

public sealed record AccessOrigenMdbInventory(
    string FileName,
    string FullPath,
    FileIntegritySnapshot Integrity,
    bool OriginalIntegrityVerified,
    bool TempsDeleted,
    int LocalTables,
    int LinkedTables,
    IReadOnlyList<AccessTableReference> TableDefs,
    IReadOnlyList<AccessLocalTableSchema> LocalSchemas,
    string SameVersionNote,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public sealed record AccessRecepcionDataContractResult(
    IReadOnlyList<AccessOrigenMdbInventory> OrigenMdbs,
    IReadOnlyList<AccessRecepcionTableContract> Tables,
    IReadOnlyList<AccessTpvFormatDoc> TpvFormats,
    IReadOnlyList<string> LegacyBehavior,
    IReadOnlyList<string> PossibleDefects,
    IReadOnlyList<string> PendingDecisions,
    IReadOnlyList<string> MissingForReconstruction,
    string ApiUsed,
    string JsonPath,
    bool OriginalImportarIntact,
    FileIntegritySnapshot? ImportarIntegrity,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public static class AccessDaoTypeNames
{
    public static string Name(int? type) => type switch
    {
        1 => "Boolean",
        2 => "Byte",
        3 => "Integer",
        4 => "Long",
        5 => "Currency",
        6 => "Single",
        7 => "Double",
        8 => "Date",
        9 => "Binary",
        10 => "Text",
        11 => "LongBinary",
        12 => "Memo",
        15 => "Guid",
        _ => type?.ToString() ?? "Unknown"
    };
}
