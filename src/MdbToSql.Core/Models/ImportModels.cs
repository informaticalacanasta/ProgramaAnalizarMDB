namespace MdbToSql.Core.Models;

public enum ImportMode
{
    Analyze,
    Import
}

public enum ImportStatus
{
    Success,
    SkippedAlreadyImported,
    Collision,
    Protected,
    Cancelled,
    Failed
}

public enum SchemaStatus
{
    Created,
    Replaced,
    AlreadyExists,
    Collision,
    Protected,
    Failed,
    NotProcessed
}

public enum DataStatus
{
    Imported,
    AlreadyImported,
    Failed,
    NotProcessed
}

public sealed class ImportSettings
{
    public string SqlServerConnectionString { get; set; } =
        "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true";

    public string SourceDirectory { get; set; } =
        @"C:\Users\Usuario\Desktop\LaCanasta\ORIGENMDB";

    public string DestinationDatabase { get; set; } = "TPVONEMDB";

    public int BatchSize { get; set; } = 5000;

    public int CommandTimeoutSeconds { get; set; } = 600;

    public bool ForceImport { get; set; }

    public ImportMode Mode { get; set; } = ImportMode.Analyze;
}

public sealed record MdbFileInfo(
    string FullPath,
    string Name,
    long Size,
    DateTime LastWriteTimeUtc);

public sealed record TableCollision(
    string TableName,
    IReadOnlyList<string> MdbNames);

public sealed record ColumnAnalysis(
    string Name,
    string SourceTypeName,
    string SqlType,
    bool IsNullable,
    bool IsAutoIncrement);

public sealed record IndexAnalysis(
    string Name,
    bool IsPrimaryKey,
    bool IsUnique,
    IReadOnlyList<string> Columns);

public sealed record TableAnalysis(
    string MdbName,
    string MdbPath,
    string TableName,
    int ColumnCount,
    int IndexCount,
    long RowCount,
    IReadOnlyList<ColumnAnalysis> Columns,
    IReadOnlyList<IndexAnalysis> Indexes,
    string? Error);

public sealed record MdbAnalysis(
    MdbFileInfo File,
    string Hash,
    IReadOnlyList<TableAnalysis> Tables,
    string? Error);

public sealed record TableImportResult(
    string MdbPath,
    string MdbName,
    string MdbHash,
    long MdbSize,
    DateTime MdbLastWriteTimeUtc,
    string TableName,
    SchemaStatus SchemaStatus,
    DataStatus DataStatus,
    ImportStatus Status,
    long SourceRowCount,
    long ImportedRowCount,
    string? Error);

public sealed record CopyResult(long RowsCopied, int BatchCount);

public sealed record CopyProgress(long RowsCopied, long ExpectedRows, int BatchCount);

public sealed record PreviousSuccessfulImport(
    string SourceMdb,
    string SourceMdbName,
    string SourceMdbHash);

public sealed record PipelineResult(
    ImportMode Mode,
    IReadOnlyList<MdbFileInfo> DiscoveredFiles,
    IReadOnlyList<MdbAnalysis> Analyses,
    IReadOnlyList<TableCollision> Collisions,
    IReadOnlyList<TableImportResult> Tables,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public int MdbFound => DiscoveredFiles.Count;

    public int MdbProcessed => Analyses.Count(item => item.Error is null);

    public int MdbFailed => Analyses.Count(item => item.Error is not null);

    public int TablesFound => Analyses.Sum(item => item.Tables.Count);

    public int TablesImported =>
        Tables.Count(item => item.Status == ImportStatus.Success);

    public int TablesSkipped =>
        Tables.Count(item => item.Status == ImportStatus.SkippedAlreadyImported);

    public int TablesWithError =>
        Tables.Count(item => item.Status is ImportStatus.Failed
            or ImportStatus.Collision
            or ImportStatus.Protected
            or ImportStatus.Cancelled)
        + Analyses.SelectMany(item => item.Tables).Count(item => item.Error is not null
            && Mode == ImportMode.Analyze);

    public long RecordsImported =>
        Tables.Where(item => item.Status == ImportStatus.Success)
            .Sum(item => item.ImportedRowCount);

    public bool IsSuccessful =>
        Errors.Count == 0 &&
        Analyses.All(item => item.Error is null) &&
        Analyses.SelectMany(item => item.Tables).All(item => item.Error is null) &&
        Tables.All(item => item.Status is ImportStatus.Success
            or ImportStatus.SkippedAlreadyImported);
}
