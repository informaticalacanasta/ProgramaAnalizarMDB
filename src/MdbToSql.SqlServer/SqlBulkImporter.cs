using System.Data.Common;
using Microsoft.Data.SqlClient;
using MdbToSql.Core.Data;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Import;
using MdbToSql.Core.Models;
using MdbToSql.Core.Planning;
using MdbToSql.Core.Utilities;

namespace MdbToSql.SqlServer;

public sealed class SqlBulkImporter : IDataCopyPort
{
    private readonly string _connectionString;
    private readonly ISqlSchemaPort _schemaService;
    private readonly ImportSettings _options;

    public SqlBulkImporter(
        string connectionString,
        ISqlSchemaPort schemaService,
        ImportSettings options)
    {
        _connectionString = connectionString;
        _schemaService = schemaService;
        _options = options;
    }

    public async Task<CopyResult> CopyAsync(
        AccessTableSchema schema,
        DbDataReader reader,
        string destinationTableName,
        long expectedRowCount,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var sqlConnection = new SqlConnection(_connectionString);
        await sqlConnection.OpenAsync(cancellationToken);

        var before = await _schemaService.CountRowsAsync(destinationTableName, cancellationToken);
        if (before != 0)
        {
            throw new DataImportException(
                $"La tabla destino '{destinationTableName}' no está vacía. " +
                "No se permite un SqlBulkCopy que duplique filas.");
        }

        var bulkOptions = SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.KeepNulls;
        if (schema.Columns.Any(column => column.IsAutoIncrement))
        {
            bulkOptions |= SqlBulkCopyOptions.KeepIdentity;
        }

        long total = 0;
        var batches = 0;
        var exhausted = false;
        try
        {
            while (!exhausted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var bounded = new BoundedDbDataReader(reader, _options.BatchSize);
                await using var transaction =
                    (SqlTransaction)await sqlConnection.BeginTransactionAsync(cancellationToken);
                try
                {
                    using var bulkCopy = new SqlBulkCopy(
                        sqlConnection,
                        bulkOptions,
                        transaction)
                    {
                        DestinationTableName =
                            $"dbo.{SqlIdentifier.Quote(destinationTableName)}",
                        BatchSize = _options.BatchSize,
                        BulkCopyTimeout = _options.CommandTimeoutSeconds,
                        EnableStreaming = true
                    };

                    foreach (var column in schema.Columns.OrderBy(item => item.Ordinal))
                    {
                        bulkCopy.ColumnMappings.Add(column.Name, column.Name);
                    }

                    await bulkCopy.WriteToServerAsync(bounded, cancellationToken);

                    if (bounded.RowsReturned == 0)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        break;
                    }

                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    catch
                    {
                        // Conserva el error original del lote.
                    }

                    throw;
                }

                batches++;
                total += bounded.RowsReturned;
                exhausted = bounded.SourceExhausted;
                progress?.Report(new CopyProgress(total, expectedRowCount, batches));
            }
        }
        catch (Exception exception) when (
            exception is not DataImportException and not OperationCanceledException)
        {
            throw new DataImportException(
                $"Error importando datos en staging de '{schema.Name}'.",
                exception);
        }

        if (!RowCountValidator.Matches(expectedRowCount, total))
        {
            throw new DataImportException(
                $"La validación de filas falló para '{schema.Name}': " +
                $"origen={expectedRowCount}, destino={total}.");
        }

        return new CopyResult(total, batches);
    }
}
