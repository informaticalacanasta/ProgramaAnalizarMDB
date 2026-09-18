using System.Globalization;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Mapping;
using MdbToSql.Core.Models;
using MdbToSql.Core.Planning;
using MdbToSql.Core.Reporting;
using MdbToSql.Core.Utilities;

namespace MdbToSql.Core.Import;

public sealed class ImportCoordinator
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-ES");

    private readonly IMdbScanner _scanner;
    private readonly IAccessDatabaseFactory _accessFactory;
    private readonly AccessToSqlTypeMapper _typeMapper;
    private readonly ISqlDatabaseInstaller? _installer;
    private readonly ISqlSchemaPort? _sql;
    private readonly IDataCopyPort? _copy;
    private readonly IImportHistoryPort? _history;
    private readonly ImportSettings _settings;
    private readonly IImportInteraction _interaction;

    public ImportCoordinator(
        IMdbScanner scanner,
        IAccessDatabaseFactory accessFactory,
        AccessToSqlTypeMapper typeMapper,
        ImportSettings settings,
        IImportInteraction? interaction = null,
        ISqlDatabaseInstaller? installer = null,
        ISqlSchemaPort? sql = null,
        IDataCopyPort? copy = null,
        IImportHistoryPort? history = null)
    {
        _scanner = scanner;
        _accessFactory = accessFactory;
        _typeMapper = typeMapper;
        _settings = settings;
        _interaction = interaction ?? NullImportInteraction.Instance;
        _installer = installer;
        _sql = sql;
        _copy = copy;
        _history = history;
    }

    public async Task<PipelineResult> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        var files = Discover();
        var analyses = new List<MdbAnalysis>();
        var warnings = new List<string>();
        var errors = new List<string>();

        _interaction.Inform($"MDB encontrados: {files.Count}");
        foreach (var file in files)
        {
            analyses.Add(await AnalyzeMdbAsync(file, errors, cancellationToken));
        }

        var collisions = CollisionDetector.FindTableNameCollisions(
            analyses.SelectMany(mdb => mdb.Tables
                .Where(table => table.Error is null)
                .Select(table => (mdb.File.Name, table.TableName))));
        if (collisions.Count > 0)
        {
            warnings.Add(SummaryPrinter.FormatCollisions(collisions).Trim());
            _interaction.Inform(SummaryPrinter.FormatCollisions(collisions).Trim());
        }

        await ProbeExistingSqlTablesAsync(analyses, warnings, cancellationToken);

        return new(
            ImportMode.Analyze,
            files,
            analyses,
            collisions,
            [],
            warnings,
            errors);
    }

    public async Task<PipelineResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        if (_installer is null || _sql is null || _copy is null || _history is null)
        {
            throw new InvalidOperationException(
                "Los servicios SQL no están configurados para importar.");
        }

        await _installer.EnsureDatabaseExistsAsync(cancellationToken);
        await _installer.EnsureInfrastructureAsync(cancellationToken);

        var files = Discover();
        var analyses = new List<MdbAnalysis>();
        var results = new List<TableImportResult>();
        var warnings = new List<string>();
        var errors = new List<string>();

        _interaction.Inform($"MDB encontrados: {files.Count}");
        foreach (var file in files)
        {
            analyses.Add(await AnalyzeMdbAsync(file, errors, cancellationToken));
        }

        var collisions = CollisionDetector.FindTableNameCollisions(
            analyses.SelectMany(mdb => mdb.Tables
                .Where(table => table.Error is null)
                .Select(table => (mdb.File.Name, table.TableName))));
        var collidingNames = collisions
            .Select(item => item.TableName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (collisions.Count > 0)
        {
            var message = SummaryPrinter.FormatCollisions(collisions).Trim();
            warnings.Add(message);
            _interaction.Inform(message);
        }

        foreach (var analysis in analyses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (analysis.Error is not null)
            {
                continue;
            }

            try
            {
                results.AddRange(await ImportMdbAsync(
                    analysis,
                    collidingNames,
                    collisions,
                    errors,
                    cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var message = $"Error procesando {analysis.File.Name}: {RootMessage(exception)}";
                errors.Add(message);
                _interaction.Inform($"ERROR {analysis.File.Name}: {RootMessage(exception)}");
            }
        }

        return new(
            ImportMode.Import,
            files,
            analyses,
            collisions,
            results,
            warnings,
            errors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private IReadOnlyList<MdbFileInfo> Discover()
    {
        if (!Directory.Exists(_settings.SourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"No existe el directorio de origen '{_settings.SourceDirectory}'.");
        }

        return _scanner.Discover(_settings.SourceDirectory);
    }

    private async Task<MdbAnalysis> AnalyzeMdbAsync(
        MdbFileInfo file,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        _interaction.Inform(string.Empty);
        _interaction.Inform($"Procesando: {file.Name}");
        string hash;
        try
        {
            hash = await FileHashCalculator.CalculateSha256Async(file.FullPath, cancellationToken);
        }
        catch (Exception exception)
        {
            var message = $"No se pudo calcular el hash de '{file.Name}': {RootMessage(exception)}";
            errors.Add(message);
            return new(file, string.Empty, [], RootMessage(exception));
        }

        try
        {
            using var database = _accessFactory.Open(file.FullPath);
            var tables = new List<TableAnalysis>();
            foreach (var tableName in database.ListUserTables())
            {
                tables.Add(AnalyzeTable(file, database, tableName, errors));
            }

            return new(file, hash, tables, null);
        }
        catch (Exception exception)
        {
            var message = $"No se pudo abrir '{file.Name}': {RootMessage(exception)}";
            errors.Add(message);
            _interaction.Inform($"  ERROR {RootMessage(exception)}");
            return new(file, hash, [], RootMessage(exception));
        }
    }

    private TableAnalysis AnalyzeTable(
        MdbFileInfo file,
        IAccessDatabase database,
        string tableName,
        ICollection<string> errors)
    {
        _interaction.Inform($"[{tableName}]");
        try
        {
            _interaction.Inform("  Leyendo estructura...");
            var schema = database.ReadTable(tableName);
            foreach (var column in schema.Columns)
            {
                _typeMapper.Map(column);
            }

            var rowCount = database.CountRows(tableName);
            _interaction.Inform($"  {schema.Columns.Count} columnas.");
            _interaction.Inform($"  {schema.Indexes.Count} índices.");
            _interaction.Inform($"  Registros origen: {rowCount.ToString("N0", DisplayCulture)}");

            var columns = schema.Columns
                .OrderBy(column => column.Ordinal)
                .Select(column => new ColumnAnalysis(
                    column.Name,
                    column.SourceTypeName,
                    _typeMapper.Map(column).ToSql() + (column.IsAutoIncrement ? " IDENTITY" : string.Empty),
                    column.IsNullable,
                    column.IsAutoIncrement))
                .ToArray();
            var indexes = schema.Indexes
                .Select(index => new IndexAnalysis(
                    index.Name,
                    index.IsPrimaryKey,
                    index.IsUnique,
                    index.Columns.OrderBy(column => column.Ordinal).Select(column => column.Name).ToArray()))
                .ToArray();
            foreach (var column in columns)
            {
                _interaction.Inform($"    {column.Name}: {column.SourceTypeName} → {column.SqlType}");
            }

            foreach (var index in indexes)
            {
                var kind = index.IsPrimaryKey ? "PK" : index.IsUnique ? "UNIQUE" : "INDEX";
                _interaction.Inform($"    {kind} {index.Name} ({string.Join(", ", index.Columns)})");
            }

            return new(
                file.Name,
                file.FullPath,
                tableName,
                schema.Columns.Count,
                schema.Indexes.Count,
                rowCount,
                columns,
                indexes,
                null);
        }
        catch (Exception exception)
        {
            var message = RootMessage(exception);
            errors.Add($"{file.Name} [{tableName}]: {message}");
            _interaction.Inform("  ERROR");
            _interaction.Inform($"  {message}");
            return new(
                file.Name,
                file.FullPath,
                tableName,
                0,
                0,
                0,
                [],
                [],
                message);
        }
    }

    private async Task<IReadOnlyList<TableImportResult>> ImportMdbAsync(
        MdbAnalysis analysis,
        IReadOnlySet<string> collidingNames,
        IReadOnlyList<TableCollision> collisions,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var results = new List<TableImportResult>();
        var runId = await _history!.RecordRunStartAsync(analysis.File, analysis.Hash, cancellationToken);
        var failed = false;
        try
        {
            using var database = _accessFactory.Open(analysis.File.FullPath);
            foreach (var table in analysis.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TableImportResult result;
                try
                {
                    result = await ImportTableAsync(
                        analysis,
                        table,
                        database,
                        collidingNames,
                        collisions,
                        runId,
                        cancellationToken);
                }
                catch (OperationCanceledException exception)
                {
                    result = await RecordAsync(
                        runId,
                        Draft(analysis, table, SchemaStatus.Failed, DataStatus.Failed, ImportStatus.Cancelled, 0, exception.Message),
                        CancellationToken.None);
                    results.Add(result);
                    throw;
                }
                catch (Exception exception)
                {
                    failed = true;
                    var message = RootMessage(exception);
                    errors.Add($"{analysis.File.Name} [{table.TableName}]: {message}");
                    _interaction.Inform("  ERROR");
                    _interaction.Inform($"  {message}");
                    result = await RecordAsync(
                        runId,
                        Draft(analysis, table, SchemaStatus.Failed, DataStatus.Failed, ImportStatus.Failed, 0, message),
                        CancellationToken.None);
                }

                results.Add(result);
                if (result.Status is ImportStatus.Failed or ImportStatus.Collision or ImportStatus.Protected)
                {
                    failed = true;
                    if (result.Error is not null)
                    {
                        errors.Add($"{analysis.File.Name} [{table.TableName}]: {result.Error}");
                    }
                }
            }

            await _history.RecordRunFinishAsync(
                runId,
                failed ? ImportStatus.Failed : ImportStatus.Success,
                error: null,
                cancellationToken);
            return results;
        }
        catch (Exception exception)
        {
            await _history.RecordRunFinishAsync(
                runId,
                exception is OperationCanceledException ? ImportStatus.Cancelled : ImportStatus.Failed,
                RootMessage(exception),
                CancellationToken.None);
            throw;
        }
    }

    private async Task<TableImportResult> ImportTableAsync(
        MdbAnalysis analysis,
        TableAnalysis table,
        IAccessDatabase database,
        IReadOnlySet<string> collidingNames,
        IReadOnlyList<TableCollision> collisions,
        long runId,
        CancellationToken cancellationToken)
    {
        _interaction.Inform($"[{table.TableName}]");
        if (table.Error is not null)
        {
            _interaction.Inform("  ERROR");
            _interaction.Inform($"  {table.Error}");
            return await RecordAsync(
                runId,
                Draft(analysis, table, SchemaStatus.Failed, DataStatus.Failed, ImportStatus.Failed, 0, table.Error),
                cancellationToken);
        }

        if (ProtectedInfrastructureTables.Contains(table.TableName))
        {
            var error = new ProtectedTableException(table.TableName).Message;
            _interaction.Inform("  ERROR");
            _interaction.Inform($"  {error}");
            return await RecordAsync(
                runId,
                Draft(analysis, table, SchemaStatus.Protected, DataStatus.NotProcessed, ImportStatus.Protected, 0, error),
                cancellationToken);
        }

        if (collidingNames.Contains(table.TableName))
        {
            var collision = collisions.First(item =>
                string.Equals(item.TableName, table.TableName, StringComparison.OrdinalIgnoreCase));
            var error = new TableNameCollisionException(table.TableName, collision.MdbNames).Message;
            _interaction.Inform("  COLISIÓN");
            _interaction.Inform($"  {error}");
            return await RecordAsync(
                runId,
                Draft(analysis, table, SchemaStatus.Collision, DataStatus.NotProcessed, ImportStatus.Collision, 0, error),
                cancellationToken);
        }

        var schema = database.ReadTable(table.TableName);
        foreach (var column in schema.Columns)
        {
            _typeMapper.Map(column);
        }

        var rowCount = database.CountRows(table.TableName);
        var schemaWithRows = schema with { RowCount = rowCount };
        _interaction.Inform("  Leyendo estructura...");
        _interaction.Inform($"  {schema.Columns.Count} columnas.");
        _interaction.Inform($"  {schema.Indexes.Count} índices.");
        _interaction.Inform($"  Registros origen: {rowCount.ToString("N0", DisplayCulture)}");

        var exists = await _sql!.TableExistsAsync(table.TableName, cancellationToken);
        var previous = exists
            ? await _history!.GetLastSuccessfulImportAsync(table.TableName, cancellationToken)
            : null;
        var alreadyImported = await _history!.WasTableImportedAsync(
            table.TableName,
            analysis.Hash,
            cancellationToken);
        var decision = ExistingTablePolicy.Decide(
            exists,
            analysis.File.FullPath,
            analysis.Hash,
            previous,
            _settings.ForceImport);

        if (decision == ExistingTableDecision.SkipAlreadyImported ||
            ImportDeduplicationPolicy.ShouldSkipAlreadyImported(alreadyImported, _settings.ForceImport))
        {
            _interaction.Inform("  Omitida: mismo hash MDB + tabla ya importada correctamente.");
            return await RecordAsync(
                runId,
                Draft(analysis, table, SchemaStatus.AlreadyExists, DataStatus.AlreadyImported, ImportStatus.SkippedAlreadyImported, 0, null),
                cancellationToken);
        }

        if (decision == ExistingTableDecision.CollisionDifferentSource)
        {
            var error =
                $"La tabla '{table.TableName}' ya existe en SQL Server porque se importó " +
                $"desde '{previous!.SourceMdbName}'. No se sobrescribe con '{analysis.File.Name}'.";
            _interaction.Inform("  COLISIÓN");
            _interaction.Inform($"  {error}");
            return await RecordAsync(
                runId,
                Draft(analysis, table, SchemaStatus.Collision, DataStatus.NotProcessed, ImportStatus.Collision, 0, error),
                cancellationToken);
        }

        if (decision == ExistingTableDecision.CollisionUnknownOrigin)
        {
            var error =
                $"La tabla '{table.TableName}' ya existe en SQL Server y no hay un histórico " +
                "de importación satisfactorio de este importador. No se sobrescribe.";
            _interaction.Inform("  COLISIÓN");
            _interaction.Inform($"  {error}");
            return await RecordAsync(
                runId,
                Draft(analysis, table, SchemaStatus.Collision, DataStatus.NotProcessed, ImportStatus.Collision, 0, error),
                cancellationToken);
        }

        return await ImportViaStagingAsync(
            analysis,
            table,
            database,
            schemaWithRows,
            exists,
            runId,
            cancellationToken);
    }

    private async Task<TableImportResult> ImportViaStagingAsync(
        MdbAnalysis analysis,
        TableAnalysis table,
        IAccessDatabase database,
        AccessTableSchema schema,
        bool destinationExisted,
        long runId,
        CancellationToken cancellationToken)
    {
        var token = StagingNames.NewToken();
        var stagingName = StagingNames.StagingTable(schema.Name, token);
        var backupName = StagingNames.BackupTable(schema.Name, token);
        var operations = new SqlReplacementOperations(
            _sql!,
            _copy!,
            database,
            schema,
            token,
            _interaction);

        try
        {
            var imported = await new SafeReplacementWorkflow().ReplaceOrCreateAsync(
                operations,
                schema,
                schema.Name,
                stagingName,
                backupName,
                cancellationToken);
            _interaction.Inform("  OK");
            return await RecordAsync(
                runId,
                Draft(
                    analysis,
                    table,
                    destinationExisted ? SchemaStatus.Replaced : SchemaStatus.Created,
                    DataStatus.Imported,
                    ImportStatus.Success,
                    imported,
                    null),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = RootMessage(exception);
            _interaction.Inform("  ERROR");
            _interaction.Inform($"  {message}");
            return await RecordAsync(
                runId,
                Draft(
                    analysis,
                    table,
                    destinationExisted ? SchemaStatus.AlreadyExists : SchemaStatus.Failed,
                    DataStatus.Failed,
                    ImportStatus.Failed,
                    0,
                    message),
                CancellationToken.None);
        }
    }

    private async Task ProbeExistingSqlTablesAsync(
        IReadOnlyList<MdbAnalysis> analyses,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        if (_installer is null || _sql is null)
        {
            return;
        }

        try
        {
            if (!await _installer.DestinationExistsAsync(cancellationToken))
            {
                warnings.Add(
                    $"La base de datos '{_settings.DestinationDatabase}' aún no existe. " +
                    "El modo analyze no la crea.");
                return;
            }

            var existing = await _sql.ListUserTablesAsync(cancellationToken);
            var business = existing
                .Where(name => !ProtectedInfrastructureTables.Contains(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var table in analyses.SelectMany(item => item.Tables).Where(item => item.Error is null))
            {
                if (business.Contains(table.TableName))
                {
                    warnings.Add(
                        $"La tabla '{table.TableName}' ({table.MdbName}) ya existe en " +
                        $"{_settings.DestinationDatabase}.");
                }
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"No se pudo consultar SQL Server en modo analyze: {RootMessage(exception)}");
        }
    }

    private async Task<TableImportResult> RecordAsync(
        long runId,
        TableImportResult result,
        CancellationToken cancellationToken)
    {
        if (_history is null)
        {
            return result;
        }

        var id = await _history.RecordTableStartAsync(runId, result, cancellationToken);
        await _history.RecordTableFinishAsync(id, result, cancellationToken);
        return result;
    }

    private static TableImportResult Draft(
        MdbAnalysis analysis,
        TableAnalysis table,
        SchemaStatus schemaStatus,
        DataStatus dataStatus,
        ImportStatus status,
        long imported,
        string? error)
    {
        return new(
            analysis.File.FullPath,
            analysis.File.Name,
            analysis.Hash,
            analysis.File.Size,
            analysis.File.LastWriteTimeUtc,
            table.TableName,
            schemaStatus,
            dataStatus,
            status,
            table.RowCount,
            imported,
            error);
    }

    private static string RootMessage(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }

    private sealed class SqlReplacementOperations : IReplacementOperations
    {
        private readonly ISqlSchemaPort _sql;
        private readonly IDataCopyPort _copy;
        private readonly IAccessDatabase _database;
        private readonly AccessTableSchema _schema;
        private readonly string _indexSuffix;
        private readonly IImportInteraction _interaction;

        public SqlReplacementOperations(
            ISqlSchemaPort sql,
            IDataCopyPort copy,
            IAccessDatabase database,
            AccessTableSchema schema,
            string indexSuffix,
            IImportInteraction interaction)
        {
            _sql = sql;
            _copy = copy;
            _database = database;
            _schema = schema;
            _indexSuffix = indexSuffix;
            _interaction = interaction;
        }

        public Task CreateEmptyTableAsync(
            string tableName,
            AccessTableSchema schema,
            CancellationToken cancellationToken)
        {
            return _sql.CreateTableAsync(schema, tableName, cancellationToken);
        }

        public async Task<long> CopyDataAsync(string tableName, CancellationToken cancellationToken)
        {
            _interaction.Inform("  Importando...");
            var progress = new Progress<CopyProgress>(update =>
                _interaction.Inform(
                    $"  {update.RowsCopied.ToString("N0", DisplayCulture)} / " +
                    $"{update.ExpectedRows.ToString("N0", DisplayCulture)}"));
            using var reader = _database.OpenReader(_schema);
            var copied = await _copy.CopyAsync(
                _schema,
                reader,
                tableName,
                _schema.RowCount,
                progress,
                cancellationToken);
            return copied.RowsCopied;
        }

        public async Task CreateIndexesAsync(
            string tableName,
            AccessTableSchema schema,
            CancellationToken cancellationToken)
        {
            _interaction.Inform("  Validando...");
            await _sql.CreateIndexesAsync(
                schema,
                tableName,
                _indexSuffix,
                cancellationToken);
        }

        public Task<long> CountAsync(string tableName, CancellationToken cancellationToken)
        {
            return _sql.CountRowsAsync(tableName, cancellationToken);
        }

        public Task<bool> ExistsAsync(string tableName, CancellationToken cancellationToken)
        {
            return _sql.TableExistsAsync(tableName, cancellationToken);
        }

        public Task SwapAtomicAsync(
            string destinationTableName,
            string stagingTableName,
            string? backupTableName,
            CancellationToken cancellationToken)
        {
            return _sql.SwapAtomicAsync(
                destinationTableName,
                stagingTableName,
                backupTableName,
                _schema.Indexes,
                _schema.Name,
                _indexSuffix,
                cancellationToken);
        }

        public Task DropIfExistsAsync(string tableName, CancellationToken cancellationToken)
        {
            return _sql.DropTableAsync(tableName, cancellationToken);
        }
    }

    private sealed class NullImportInteraction : IImportInteraction
    {
        public static readonly NullImportInteraction Instance = new();

        public void Inform(string message)
        {
        }
    }
}
