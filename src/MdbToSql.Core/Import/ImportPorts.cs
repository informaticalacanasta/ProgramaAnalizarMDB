using System.Data.Common;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Import;

public interface IAccessDatabaseFactory
{
    IAccessDatabase Open(string mdbPath);
}

public interface IAccessDatabase : IDisposable
{
    IReadOnlyList<string> ListUserTables();

    AccessTableSchema ReadTable(string tableName);

    long CountRows(string tableName);

    DbDataReader OpenReader(AccessTableSchema schema);
}

public interface IMdbScanner
{
    IReadOnlyList<MdbFileInfo> Discover(string sourceDirectory);
}

public interface ISqlDatabaseInstaller
{
    Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken = default);

    Task EnsureInfrastructureAsync(CancellationToken cancellationToken = default);

    Task<bool> DestinationExistsAsync(CancellationToken cancellationToken = default);

    string GetDestinationConnectionString();
}

public interface ISqlSchemaPort
{
    Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken);

    Task DropTableAsync(string tableName, CancellationToken cancellationToken);

    Task CreateTableAsync(
        AccessTableSchema table,
        string destinationTableName,
        CancellationToken cancellationToken);

    Task CreateIndexesAsync(
        AccessTableSchema table,
        string destinationTableName,
        string? uniqueNameSuffix,
        CancellationToken cancellationToken);

    Task SwapAtomicAsync(
        string destinationTableName,
        string stagingTableName,
        string? backupTableName,
        IReadOnlyList<AccessIndexSchema> indexes,
        string tableLogicalName,
        string? uniqueNameSuffix,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListUserTablesAsync(CancellationToken cancellationToken);

    Task<long> CountRowsAsync(string tableName, CancellationToken cancellationToken);
}

public interface IDataCopyPort
{
    Task<CopyResult> CopyAsync(
        AccessTableSchema schema,
        DbDataReader reader,
        string destinationTableName,
        long expectedRowCount,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IImportHistoryPort
{
    Task<long> RecordRunStartAsync(
        MdbFileInfo file,
        string hash,
        CancellationToken cancellationToken);

    Task RecordRunFinishAsync(
        long runId,
        ImportStatus status,
        string? error,
        CancellationToken cancellationToken);

    Task<bool> WasTableImportedAsync(
        string tableName,
        string mdbHash,
        CancellationToken cancellationToken);

    Task<PreviousSuccessfulImport?> GetLastSuccessfulImportAsync(
        string tableName,
        CancellationToken cancellationToken);

    Task<long> RecordTableStartAsync(
        long runId,
        TableImportResult draft,
        CancellationToken cancellationToken);

    Task RecordTableFinishAsync(
        long id,
        TableImportResult result,
        CancellationToken cancellationToken);
}

public interface IImportInteraction
{
    void Inform(string message);
}
