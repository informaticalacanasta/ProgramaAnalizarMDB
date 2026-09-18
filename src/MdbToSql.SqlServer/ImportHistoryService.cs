using Microsoft.Data.SqlClient;
using MdbToSql.Core.Import;
using MdbToSql.Core.Models;

namespace MdbToSql.SqlServer;

public sealed class ImportHistoryService : IImportHistoryPort
{
    private readonly string _connectionString;
    private readonly int _commandTimeout;

    public ImportHistoryService(string connectionString, int commandTimeout)
    {
        _connectionString = connectionString;
        _commandTimeout = commandTimeout;
    }

    public async Task<long> RecordRunStartAsync(
        MdbFileInfo file,
        string hash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.MdbImportRun
            (
                SourceMdb, SourceMdbName, SourceMdbSize,
                SourceMdbLastWriteTime, SourceMdbHash, StartedAt, Status
            )
            OUTPUT INSERTED.Id
            VALUES
            (
                @SourceMdb, @SourceMdbName, @SourceMdbSize,
                @SourceMdbLastWriteTime, @SourceMdbHash, SYSDATETIME(), N'Running'
            );
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        command.Parameters.AddWithValue("@SourceMdb", file.FullPath);
        command.Parameters.AddWithValue("@SourceMdbName", file.Name);
        command.Parameters.AddWithValue("@SourceMdbSize", file.Size);
        command.Parameters.AddWithValue("@SourceMdbLastWriteTime", file.LastWriteTimeUtc);
        command.Parameters.AddWithValue("@SourceMdbHash", hash);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task RecordRunFinishAsync(
        long runId,
        ImportStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.MdbImportRun
            SET FinishedAt = SYSDATETIME(),
                Status = @Status,
                ErrorMessage = @ErrorMessage
            WHERE Id = @Id;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        command.Parameters.AddWithValue("@Id", runId);
        command.Parameters.AddWithValue("@Status", status.ToString());
        command.Parameters.AddWithValue("@ErrorMessage", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> WasTableImportedAsync(
        string tableName,
        string mdbHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS
            (
                SELECT 1
                FROM dbo.MdbImportTableHistory
                WHERE TableName = @TableName
                  AND SourceMdbHash = @SourceMdbHash
                  AND Status = N'Success'
            ) THEN 1 ELSE 0 END;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        command.Parameters.AddWithValue("@TableName", tableName);
        command.Parameters.AddWithValue("@SourceMdbHash", mdbHash);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<PreviousSuccessfulImport?> GetLastSuccessfulImportAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) SourceMdb, SourceMdbName, SourceMdbHash
            FROM dbo.MdbImportTableHistory
            WHERE TableName = @TableName
              AND Status = N'Success'
            ORDER BY Id DESC;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        command.Parameters.AddWithValue("@TableName", tableName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PreviousSuccessfulImport(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2));
    }

    public async Task<long> RecordTableStartAsync(
        long runId,
        TableImportResult draft,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.MdbImportTableHistory
            (
                RunId, SourceMdb, SourceMdbName, SourceMdbSize,
                SourceMdbLastWriteTime, SourceMdbHash, TableName,
                SourceRowCount, StartedAt, Status, Action
            )
            OUTPUT INSERTED.Id
            VALUES
            (
                @RunId, @SourceMdb, @SourceMdbName, @SourceMdbSize,
                @SourceMdbLastWriteTime, @SourceMdbHash, @TableName,
                @SourceRowCount, SYSDATETIME(), @Status, @Action
            );
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        command.Parameters.AddWithValue("@RunId", runId == 0 ? DBNull.Value : runId);
        command.Parameters.AddWithValue("@SourceMdb", draft.MdbPath);
        command.Parameters.AddWithValue("@SourceMdbName", draft.MdbName);
        command.Parameters.AddWithValue("@SourceMdbSize", draft.MdbSize);
        command.Parameters.AddWithValue("@SourceMdbLastWriteTime", draft.MdbLastWriteTimeUtc);
        command.Parameters.AddWithValue("@SourceMdbHash", draft.MdbHash);
        command.Parameters.AddWithValue("@TableName", draft.TableName);
        command.Parameters.AddWithValue("@SourceRowCount", draft.SourceRowCount);
        command.Parameters.AddWithValue("@Status", draft.Status.ToString());
        command.Parameters.AddWithValue("@Action", draft.SchemaStatus.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task RecordTableFinishAsync(
        long id,
        TableImportResult result,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.MdbImportTableHistory
            SET ImportedRowCount = @ImportedRowCount,
                FinishedAt = SYSDATETIME(),
                Status = @Status,
                Action = @Action,
                ErrorMessage = @ErrorMessage
            WHERE Id = @Id;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@ImportedRowCount", result.ImportedRowCount);
        command.Parameters.AddWithValue("@Status", result.Status.ToString());
        command.Parameters.AddWithValue("@Action", result.SchemaStatus.ToString());
        command.Parameters.AddWithValue("@ErrorMessage", (object?)result.Error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
