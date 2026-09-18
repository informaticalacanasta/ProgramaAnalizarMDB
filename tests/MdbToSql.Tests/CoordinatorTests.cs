using MdbToSql.Access;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Import;
using MdbToSql.Core.Mapping;
using MdbToSql.Core.Models;

namespace MdbToSql.Tests;

public sealed class CoordinatorAndScannerTests
{
    [Fact]
    public void Scanner_FindsOnlyMdbSorted()
    {
        using var directory = new TempDir();
        File.WriteAllText(Path.Combine(directory.Path, "b.mdb"), "b");
        File.WriteAllText(Path.Combine(directory.Path, "a.mdb"), "a");
        File.WriteAllText(Path.Combine(directory.Path, "c.accdb"), "no");
        File.WriteAllText(Path.Combine(directory.Path, "notes.txt"), "no");

        var files = new AccessDatabaseScanner().Discover(directory.Path);

        Assert.Equal(["a.mdb", "b.mdb"], files.Select(file => file.Name).ToArray());
    }

    [Fact]
    public async Task Analyze_DetectsCollisionsWithoutSql()
    {
        using var directory = new TempDir();
        var first = CreateMdbFile(directory.Path, "tienda01.mdb", "uno");
        var second = CreateMdbFile(directory.Path, "tienda02.mdb", "dos");
        var factory = new FakeAccessDatabaseFactory();
        factory.Add(first.FullPath, ArticulosMdb(2));
        factory.Add(second.FullPath, ArticulosMdb(3));

        var coordinator = new ImportCoordinator(
            new FakeMdbScanner([first, second]),
            factory,
            new AccessToSqlTypeMapper(),
            new ImportSettings { SourceDirectory = directory.Path, Mode = ImportMode.Analyze });

        var result = await coordinator.AnalyzeAsync();

        Assert.Equal(2, result.MdbFound);
        var collision = Assert.Single(result.Collisions);
        Assert.Equal("articulos", collision.TableName);
        Assert.Contains("tienda01.mdb", collision.MdbNames);
        Assert.Contains("tienda02.mdb", collision.MdbNames);
    }

    [Fact]
    public async Task Analyze_ReportsUnsupportedTypePerTable()
    {
        using var directory = new TempDir();
        var file = CreateMdbFile(directory.Path, "tienda.mdb", "x");
        var database = new FakeAccessDatabase
        {
            ReadException = new UnsupportedAccessTypeException(
                file.FullPath,
                "fotos",
                "FOTO",
                "OleDbType=999")
        };
        database.AddTable(
            new AccessTableSchema(
                "fotos",
                [new("FOTO", AccessTypeKind.LongBinary, "Unknown", 205, null, null, null, 0, true, false)],
                []));
        var factory = new FakeAccessDatabaseFactory();
        factory.Add(file.FullPath, database);

        var coordinator = new ImportCoordinator(
            new FakeMdbScanner([file]),
            factory,
            new AccessToSqlTypeMapper(),
            new ImportSettings { SourceDirectory = directory.Path });

        var result = await coordinator.AnalyzeAsync();
        var table = Assert.Single(result.Analyses[0].Tables);
        Assert.NotNull(table.Error);
        Assert.Contains("FOTO", table.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_RejectsProtectedTableNames()
    {
        using var directory = new TempDir();
        var file = CreateMdbFile(directory.Path, "tienda.mdb", "x");
        var database = new FakeAccessDatabase();
        database.AddTable(
            new AccessTableSchema(
                "MdbImportRun",
                [TypeMapperTests.Column("Id", AccessTypeKind.Long, 3, 4, false)],
                []),
            [1]);
        var factory = new FakeAccessDatabaseFactory();
        factory.Add(file.FullPath, database);
        var sql = new InMemorySqlPorts();

        var coordinator = new ImportCoordinator(
            new FakeMdbScanner([file]),
            factory,
            new AccessToSqlTypeMapper(),
            new ImportSettings { SourceDirectory = directory.Path, Mode = ImportMode.Import },
            installer: sql,
            sql: sql,
            copy: sql,
            history: sql);

        var result = await coordinator.ImportAsync();
        var table = Assert.Single(result.Tables);
        Assert.Equal(ImportStatus.Protected, table.Status);
        Assert.DoesNotContain("MdbImportRun", sql.Tables.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_DoesNotMixCollidingTables()
    {
        using var directory = new TempDir();
        var first = CreateMdbFile(directory.Path, "tienda01.mdb", "uno");
        var second = CreateMdbFile(directory.Path, "tienda02.mdb", "dos");
        var factory = new FakeAccessDatabaseFactory();
        factory.Add(first.FullPath, ArticulosMdb(1));
        factory.Add(second.FullPath, ArticulosMdb(2));
        var sql = new InMemorySqlPorts();

        var coordinator = new ImportCoordinator(
            new FakeMdbScanner([first, second]),
            factory,
            new AccessToSqlTypeMapper(),
            new ImportSettings { SourceDirectory = directory.Path, Mode = ImportMode.Import },
            installer: sql,
            sql: sql,
            copy: sql,
            history: sql);

        var result = await coordinator.ImportAsync();
        Assert.Equal(2, result.Tables.Count);
        Assert.All(result.Tables, table => Assert.Equal(ImportStatus.Collision, table.Status));
        Assert.Empty(sql.Tables);
    }

    private static FakeAccessDatabase ArticulosMdb(int id)
    {
        var database = new FakeAccessDatabase();
        database.AddTable(
            new AccessTableSchema(
                "articulos",
                [TypeMapperTests.Column("id", AccessTypeKind.Long, 3, 4, false, true)],
                [new("PrimaryKey", true, true, [new("id", 1, false)])]),
            [id]);
        return database;
    }

    private static MdbFileInfo CreateMdbFile(string directory, string name, string contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        var info = new FileInfo(path);
        return new MdbFileInfo(info.FullName, info.Name, info.Length, info.LastWriteTimeUtc);
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = Directory.CreateTempSubdirectory().FullName;
    }

    public string Path { get; }

    public void Dispose()
    {
        Directory.Delete(Path, true);
    }
}

internal sealed class InMemorySqlPorts :
    ISqlDatabaseInstaller,
    ISqlSchemaPort,
    IDataCopyPort,
    IImportHistoryPort
{
    public Dictionary<string, long> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<TableImportResult> _history = [];

    public Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnsureInfrastructureAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> DestinationExistsAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public string GetDestinationConnectionString() => "memory";

    public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        return Task.FromResult(Tables.ContainsKey(tableName));
    }

    public Task DropTableAsync(string tableName, CancellationToken cancellationToken)
    {
        Tables.Remove(tableName);
        return Task.CompletedTask;
    }

    public Task CreateTableAsync(
        AccessTableSchema table,
        string destinationTableName,
        CancellationToken cancellationToken)
    {
        Tables[destinationTableName] = 0;
        return Task.CompletedTask;
    }

    public Task CreateIndexesAsync(
        AccessTableSchema table,
        string destinationTableName,
        string? uniqueNameSuffix,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task SwapAtomicAsync(
        string destinationTableName,
        string stagingTableName,
        string? backupTableName,
        IReadOnlyList<AccessIndexSchema> indexes,
        string tableLogicalName,
        string? uniqueNameSuffix,
        CancellationToken cancellationToken)
    {
        if (backupTableName is not null)
        {
            Tables.Remove(destinationTableName);
        }

        Tables[destinationTableName] = Tables[stagingTableName];
        Tables.Remove(stagingTableName);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListUserTablesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<string>>(Tables.Keys.ToArray());
    }

    public Task<long> CountRowsAsync(string tableName, CancellationToken cancellationToken)
    {
        return Task.FromResult(Tables[tableName]);
    }

    public Task<CopyResult> CopyAsync(
        AccessTableSchema schema,
        System.Data.Common.DbDataReader reader,
        string destinationTableName,
        long expectedRowCount,
        IProgress<CopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        long count = 0;
        while (reader.Read())
        {
            count++;
        }

        Tables[destinationTableName] = count;
        progress?.Report(new CopyProgress(count, expectedRowCount, 1));
        return Task.FromResult(new CopyResult(count, 1));
    }

    public Task<long> RecordRunStartAsync(
        MdbFileInfo file,
        string hash,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(1L);
    }

    public Task RecordRunFinishAsync(
        long runId,
        ImportStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<bool> WasTableImportedAsync(
        string tableName,
        string mdbHash,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(_history.Any(item =>
            item.TableName == tableName &&
            item.MdbHash == mdbHash &&
            item.Status == ImportStatus.Success));
    }

    public Task<PreviousSuccessfulImport?> GetLastSuccessfulImportAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        var last = _history.LastOrDefault(item =>
            item.TableName == tableName && item.Status == ImportStatus.Success);
        return Task.FromResult(last is null
            ? null
            : new PreviousSuccessfulImport(last.MdbPath, last.MdbName, last.MdbHash));
    }

    public Task<long> RecordTableStartAsync(
        long runId,
        TableImportResult draft,
        CancellationToken cancellationToken)
    {
        return Task.FromResult((long)_history.Count + 1);
    }

    public Task RecordTableFinishAsync(
        long id,
        TableImportResult result,
        CancellationToken cancellationToken)
    {
        _history.Add(result);
        return Task.CompletedTask;
    }
}
