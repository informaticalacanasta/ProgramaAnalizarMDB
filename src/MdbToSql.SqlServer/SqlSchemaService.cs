using Microsoft.Data.SqlClient;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Import;
using MdbToSql.Core.Mapping;
using MdbToSql.Core.Models;
using MdbToSql.Core.Schema;
using MdbToSql.Core.Utilities;

namespace MdbToSql.SqlServer;

public sealed class SqlSchemaService : ISqlSchemaPort
{
    private readonly string _connectionString;
    private readonly SqlDdlBuilder _ddlBuilder;
    private readonly int _commandTimeout;

    public SqlSchemaService(
        string connectionString,
        AccessToSqlTypeMapper typeMapper,
        int commandTimeout)
    {
        _connectionString = connectionString;
        _ddlBuilder = new SqlDdlBuilder(typeMapper);
        _commandTimeout = commandTimeout;
    }

    public async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CASE WHEN OBJECT_ID(
                N'dbo.' + QUOTENAME(@TableName), N'U') IS NULL
                THEN 0 ELSE 1 END;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        command.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task DropTableAsync(string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U') IS NOT NULL
            BEGIN
                DECLARE @drop nvarchar(max) = N'DROP TABLE dbo.' + QUOTENAME(@TableName);
                EXEC (@drop);
            END
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        command.Parameters.AddWithValue("@TableName", tableName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateTableAsync(
        AccessTableSchema table,
        string destinationTableName,
        CancellationToken cancellationToken)
    {
        var sql = _ddlBuilder.BuildCreateTableSql(table, destinationTableName);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListUserTablesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT name
            FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo')
            ORDER BY name;
            """;
        var tables = new List<string>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public async Task CreateIndexesAsync(
        AccessTableSchema table,
        string destinationTableName,
        string? uniqueNameSuffix,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var statements = _ddlBuilder.BuildCreateIndexSql(
            table,
            destinationTableName,
            uniqueNameSuffix);
        for (var index = 0; index < table.Indexes.Count; index++)
        {
            var current = table.Indexes[index];
            var canonical = StagingNames.CanonicalIndexName(
                table.Name,
                current.Name,
                current.IsPrimaryKey);
            var indexName = StagingNames.SuffixedIndex(canonical, uniqueNameSuffix);
            if (await IndexExistsAsync(
                    connection,
                    destinationTableName,
                    indexName,
                    cancellationToken))
            {
                continue;
            }

            await using var command = new SqlCommand(statements[index], connection)
            {
                CommandTimeout = _commandTimeout
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task SwapAtomicAsync(
        string destinationTableName,
        string stagingTableName,
        string? backupTableName,
        IReadOnlyList<AccessIndexSchema> indexes,
        string tableLogicalName,
        string? uniqueNameSuffix,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (backupTableName is not null)
            {
                await RenameObjectAsync(
                    connection,
                    transaction,
                    destinationTableName,
                    backupTableName,
                    cancellationToken);
            }

            await RenameObjectAsync(
                connection,
                transaction,
                stagingTableName,
                destinationTableName,
                cancellationToken);

            await RenameIndexesToCanonicalAsync(
                connection,
                transaction,
                destinationTableName,
                indexes,
                tableLogicalName,
                uniqueNameSuffix,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Conserva el error original del swap.
            }

            throw new DataImportException(
                $"Error sustituyendo tabla '{destinationTableName}'.",
                exception);
        }
    }

    public async Task<long> CountRowsAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var sql = $"SELECT COUNT_BIG(*) FROM dbo.{SqlIdentifier.Quote(tableName)};";
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<bool> IndexExistsAsync(
        SqlConnection connection,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U')
              AND name = @IndexName;
            """;
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = _commandTimeout
        };
        command.Parameters.AddWithValue("@TableName", tableName);
        command.Parameters.AddWithValue("@IndexName", indexName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private async Task RenameObjectAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string currentName,
        string newName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            EXEC sys.sp_rename
                @objname = @ObjName,
                @newname = @NewName,
                @objtype = N'OBJECT';
            """;
        await using var command = new SqlCommand(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeout
        };
        command.Parameters.AddWithValue("@ObjName", $"dbo.{currentName}");
        command.Parameters.AddWithValue("@NewName", newName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RenameIndexesToCanonicalAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableName,
        IReadOnlyList<AccessIndexSchema> indexes,
        string tableLogicalName,
        string? uniqueNameSuffix,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(uniqueNameSuffix))
        {
            return;
        }

        foreach (var index in indexes)
        {
            var canonical = StagingNames.CanonicalIndexName(
                tableLogicalName,
                index.Name,
                index.IsPrimaryKey);
            var currentName = StagingNames.SuffixedIndex(canonical, uniqueNameSuffix);
            if (string.Equals(currentName, canonical, StringComparison.Ordinal))
            {
                continue;
            }

            if (index.IsPrimaryKey)
            {
                const string renameConstraint = """
                    EXEC sys.sp_rename
                        @objname = @ObjName,
                        @newname = @NewName,
                        @objtype = N'OBJECT';
                    """;
                await using var command = new SqlCommand(renameConstraint, connection, transaction)
                {
                    CommandTimeout = _commandTimeout
                };
                command.Parameters.AddWithValue("@ObjName", $"dbo.{currentName}");
                command.Parameters.AddWithValue("@NewName", canonical);
                await command.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            const string renameIndex = """
                EXEC sys.sp_rename
                    @objname = @ObjName,
                    @newname = @NewName,
                    @objtype = N'INDEX';
                """;
            await using var indexCommand = new SqlCommand(renameIndex, connection, transaction)
            {
                CommandTimeout = _commandTimeout
            };
            indexCommand.Parameters.AddWithValue("@ObjName", $"dbo.{tableName}.{currentName}");
            indexCommand.Parameters.AddWithValue("@NewName", canonical);
            await indexCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
