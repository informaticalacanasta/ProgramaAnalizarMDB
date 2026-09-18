using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Planning;

public interface IReplacementOperations
{
    Task CreateEmptyTableAsync(
        string tableName,
        AccessTableSchema schema,
        CancellationToken cancellationToken);

    Task<long> CopyDataAsync(string tableName, CancellationToken cancellationToken);

    Task CreateIndexesAsync(
        string tableName,
        AccessTableSchema schema,
        CancellationToken cancellationToken);

    Task<long> CountAsync(string tableName, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string tableName, CancellationToken cancellationToken);

    Task SwapAtomicAsync(
        string destinationTableName,
        string stagingTableName,
        string? backupTableName,
        CancellationToken cancellationToken);

    Task DropIfExistsAsync(string tableName, CancellationToken cancellationToken);
}

public sealed class SafeReplacementWorkflow
{
    public async Task<long> ReplaceOrCreateAsync(
        IReplacementOperations operations,
        AccessTableSchema schema,
        string destinationTableName,
        string stagingTableName,
        string backupTableName,
        CancellationToken cancellationToken = default)
    {
        var destinationExisted = await operations.ExistsAsync(
            destinationTableName,
            cancellationToken);

        try
        {
            await operations.CreateEmptyTableAsync(
                stagingTableName,
                schema,
                cancellationToken);
            var imported = await operations.CopyDataAsync(
                stagingTableName,
                cancellationToken);
            if (!RowCountValidator.Matches(schema.RowCount, imported))
            {
                throw new DataImportException(
                    $"La validación de filas falló para '{schema.Name}': " +
                    $"origen={schema.RowCount}, staging importado={imported}.");
            }

            await operations.CreateIndexesAsync(
                stagingTableName,
                schema,
                cancellationToken);

            var counted = await operations.CountAsync(
                stagingTableName,
                cancellationToken);
            if (!RowCountValidator.Matches(schema.RowCount, counted))
            {
                throw new DataImportException(
                    $"La validación de filas falló para '{schema.Name}': " +
                    $"origen={schema.RowCount}, staging={counted}.");
            }

            await operations.SwapAtomicAsync(
                destinationTableName,
                stagingTableName,
                destinationExisted ? backupTableName : null,
                cancellationToken);

            if (destinationExisted)
            {
                try
                {
                    await operations.DropIfExistsAsync(backupTableName, cancellationToken);
                }
                catch
                {
                    // El swap ya confirmó la tabla definitiva; un backup huérfano no revierte el éxito.
                }
            }

            return counted;
        }
        catch (Exception exception)
        {
            Exception? cleanup = null;
            try
            {
                await operations.DropIfExistsAsync(stagingTableName, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                cleanup = cleanupException;
            }

            if (cleanup is not null)
            {
                throw new DataImportException(
                    $"{exception.Message} Además no se pudo eliminar la tabla staging '{stagingTableName}'.",
                    new AggregateException(exception, cleanup));
            }

            throw;
        }
    }
}

public static class CollisionDetector
{
    public static IReadOnlyList<TableCollision> FindTableNameCollisions(
        IEnumerable<(string MdbName, string TableName)> tables)
    {
        return tables
            .GroupBy(item => item.TableName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.MdbName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
            .Select(group => new TableCollision(
                group.Key,
                group.Select(item => item.MdbName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderBy(item => item.TableName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public static class ImportDeduplicationPolicy
{
    public static bool ShouldSkipAlreadyImported(
        bool previousSuccessfulImportSameHashAndTable,
        bool forceImport = false)
    {
        return previousSuccessfulImportSameHashAndTable && !forceImport;
    }
}

public static class RowCountValidator
{
    public static bool Matches(long sourceRows, long destinationRows)
    {
        return sourceRows == destinationRows;
    }
}

public enum ExistingTableDecision
{
    CreateNew,
    SkipAlreadyImported,
    ReplaceFromSameSource,
    CollisionDifferentSource,
    CollisionUnknownOrigin
}

public static class ExistingTablePolicy
{
    public static ExistingTableDecision Decide(
        bool tableExists,
        string currentMdbPath,
        string currentMdbHash,
        PreviousSuccessfulImport? previousSuccess,
        bool forceImport)
    {
        if (!tableExists)
        {
            return ExistingTableDecision.CreateNew;
        }

        if (previousSuccess is null)
        {
            return ExistingTableDecision.CollisionUnknownOrigin;
        }

        var sameHash = string.Equals(
            previousSuccess.SourceMdbHash,
            currentMdbHash,
            StringComparison.OrdinalIgnoreCase);
        if (sameHash)
        {
            return ImportDeduplicationPolicy.ShouldSkipAlreadyImported(true, forceImport)
                ? ExistingTableDecision.SkipAlreadyImported
                : ExistingTableDecision.ReplaceFromSameSource;
        }

        var sameSource = string.Equals(
            previousSuccess.SourceMdb,
            currentMdbPath,
            StringComparison.OrdinalIgnoreCase);
        return sameSource
            ? ExistingTableDecision.ReplaceFromSameSource
            : ExistingTableDecision.CollisionDifferentSource;
    }
}
