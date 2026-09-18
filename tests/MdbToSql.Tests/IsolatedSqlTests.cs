using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MdbToSql.Core.Import;
using MdbToSql.Core.Mapping;
using MdbToSql.Core.Models;
using MdbToSql.SqlServer;

namespace MdbToSql.Tests;

public sealed class IsolatedSqlTests
{
    [SqlFact]
    public async Task NewDatabase_CreatesOnlyTechnicalInfrastructure()
    {
        await using var database = await IsolatedSqlDatabase.CreateAsync();
        var tables = await database.ListUserTablesAsync();

        Assert.NotEqual("TPVONEMDB", database.DatabaseName);
        Assert.StartsWith("TPVONEMDB_TESTS_", database.DatabaseName, StringComparison.Ordinal);
        foreach (var technical in ProtectedInfrastructureTables.Names.Where(name =>
                     name is "SchemaMigrations" or "MdbImportRun" or "MdbImportTableHistory"))
        {
            Assert.Contains(technical, tables, StringComparer.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("articulos", tables, StringComparer.OrdinalIgnoreCase);
    }

    [SqlFact]
    public async Task Import_CreatesTableViaStagingAndLeavesNoStaging()
    {
        await using var database = await IsolatedSqlDatabase.CreateAsync();
        using var source = new TempDir();
        var file = WriteMdb(source.Path, "tienda.mdb", "contenido-a");
        var factory = new FakeAccessDatabaseFactory();
        factory.Add(file.FullPath, Articulos(1, "Jamón"));
        var result = await ImportAsync(database, source.Path, factory, file);

        Assert.Equal(ImportStatus.Success, result.Tables[0].Status);
        Assert.Equal(SchemaStatus.Created, result.Tables[0].SchemaStatus);
        Assert.Equal(1, result.Tables[0].ImportedRowCount);
        Assert.Equal(1, await database.CountRowsAsync("articulos"));
        Assert.Equal("Jamón", await database.ReadNVarCharAsync("articulos", "nombre", "id", 1));
        Assert.DoesNotContain(
            await database.ListUserTablesAsync(),
            name => name.Contains("__s", StringComparison.OrdinalIgnoreCase)
                || name.Contains("__b", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("PK_articulos", await database.ListIndexNamesAsync("articulos"));
    }

    [SqlFact]
    public async Task IdenticalSecondImport_SameHash_IsSkipped()
    {
        await using var database = await IsolatedSqlDatabase.CreateAsync();
        using var source = new TempDir();
        var file = WriteMdb(source.Path, "tienda.mdb", "contenido-a");
        var factory = new FakeAccessDatabaseFactory();
        factory.Add(file.FullPath, Articulos(1, "Jamón"));

        var first = await ImportAsync(database, source.Path, factory, file);
        var second = await ImportAsync(database, source.Path, factory, file);

        Assert.Equal(ImportStatus.Success, first.Tables[0].Status);
        Assert.Equal(ImportStatus.SkippedAlreadyImported, second.Tables[0].Status);
        Assert.Equal(1, await database.CountRowsAsync("articulos"));
    }

    [SqlFact]
    public async Task FailedValidation_LeavesOriginalTable()
    {
        await using var database = await IsolatedSqlDatabase.CreateAsync();
        using var source = new TempDir();
        var file = WriteMdb(source.Path, "tienda.mdb", "contenido-a");
        var factory = new FakeAccessDatabaseFactory();
        factory.Add(file.FullPath, Articulos(1, "Jamón"));
        await ImportAsync(database, source.Path, factory, file);

        File.WriteAllText(file.FullPath, "contenido-b-distinto");
        var updated = new FileInfo(file.FullPath);
        var updatedInfo = new MdbFileInfo(
            updated.FullName,
            updated.Name,
            updated.Length,
            updated.LastWriteTimeUtc);
        var broken = new FakeAccessDatabase { CountOverride = 2 };
        var schema = new AccessTableSchema(
            "articulos",
            [
                TypeMapperTests.Column("id", AccessTypeKind.Long, 3, 4, false, true),
                TypeMapperTests.Column("nombre", AccessTypeKind.Text, 202, 50)
            ],
            [new("PrimaryKey", true, true, [new("id", 1, false)])],
            2);
        broken.AddTable(schema, [2, "nuevo"]);
        var brokenFactory = new FakeAccessDatabaseFactory();
        brokenFactory.Add(file.FullPath, broken);

        var failed = await ImportAsync(database, source.Path, brokenFactory, updatedInfo);
        Assert.Equal(ImportStatus.Failed, failed.Tables[0].Status);
        Assert.Equal(1, await database.CountRowsAsync("articulos"));
        Assert.Equal("Jamón", await database.ReadNVarCharAsync("articulos", "nombre", "id", 1));
    }

    [SqlFact]
    public async Task DifferentMdb_DoesNotOverwriteExistingTable()
    {
        await using var database = await IsolatedSqlDatabase.CreateAsync();
        using var source = new TempDir();
        var firstFile = WriteMdb(source.Path, "tienda01.mdb", "uno");
        var secondFile = WriteMdb(source.Path, "tienda02.mdb", "dos");
        var firstFactory = new FakeAccessDatabaseFactory();
        firstFactory.Add(firstFile.FullPath, Articulos(1, "Jamón"));
        await ImportAsync(database, source.Path, firstFactory, firstFile);

        var secondFactory = new FakeAccessDatabaseFactory();
        secondFactory.Add(secondFile.FullPath, Articulos(9, "Queso"));
        var second = await ImportAsync(
            database,
            source.Path,
            secondFactory,
            secondFile,
            new FakeMdbScanner([secondFile]));

        Assert.Equal(ImportStatus.Collision, second.Tables[0].Status);
        Assert.Equal("Jamón", await database.ReadNVarCharAsync("articulos", "nombre", "id", 1));
        Assert.Equal(1, await database.CountRowsAsync("articulos"));
    }

    [SqlFact]
    public async Task BooleanNullAndBinary_ArePreserved()
    {
        await using var database = await IsolatedSqlDatabase.CreateAsync();
        using var source = new TempDir();
        var file = WriteMdb(source.Path, "tienda.mdb", "bin");
        var foto = new byte[] { 0x42, 0x4D, 0x01, 0x00 };
        var schema = new AccessTableSchema(
            "datos",
            [
                TypeMapperTests.Column("id", AccessTypeKind.Long, 3, 4, false, true),
                TypeMapperTests.Column("activo", AccessTypeKind.Boolean, 11),
                TypeMapperTests.Column("foto", AccessTypeKind.LongBinary, 205),
                TypeMapperTests.Column("nota", AccessTypeKind.Memo, 203)
            ],
            [new("PrimaryKey", true, true, [new("id", 1, false)])]);
        var fake = new FakeAccessDatabase();
        fake.AddTable(
            schema,
            [1, true, foto, "jamón y paté"],
            [2, null, null, null]);
        var factory = new FakeAccessDatabaseFactory();
        factory.Add(file.FullPath, fake);

        var result = await ImportAsync(database, source.Path, factory, file);
        Assert.Equal(ImportStatus.Success, result.Tables[0].Status);
        Assert.Equal(foto, await database.ReadBinaryAsync("datos", "foto", "id", 1));
        Assert.Null(await database.ReadBinaryAsync("datos", "foto", "id", 2));
        Assert.Equal("jamón y paté", await database.ReadNVarCharAsync("datos", "nota", "id", 1));
        Assert.True(await database.ReadBitAsync("datos", "activo", "id", 1));
        Assert.Null(await database.ReadBitAsync("datos", "activo", "id", 2));
    }

    private static async Task<PipelineResult> ImportAsync(
        IsolatedSqlDatabase database,
        string sourceDirectory,
        FakeAccessDatabaseFactory factory,
        MdbFileInfo file,
        IMdbScanner? scanner = null)
    {
        var settings = new ImportSettings
        {
            SourceDirectory = sourceDirectory,
            DestinationDatabase = database.DatabaseName,
            SqlServerConnectionString = database.MasterConnectionString,
            Mode = ImportMode.Import,
            BatchSize = 5000,
            CommandTimeoutSeconds = 60
        };
        var mapper = new AccessToSqlTypeMapper();
        var schema = new SqlSchemaService(database.ConnectionString, mapper, 60);
        var coordinator = new ImportCoordinator(
            scanner ?? new FakeMdbScanner([file]),
            factory,
            mapper,
            settings,
            installer: new SqlDatabaseInstaller(
                database.MasterConnectionString,
                database.DatabaseName,
                60,
                NullLogger<SqlDatabaseInstaller>.Instance),
            sql: schema,
            copy: new SqlBulkImporter(database.ConnectionString, schema, settings),
            history: new ImportHistoryService(database.ConnectionString, 60));
        return await coordinator.ImportAsync();
    }

    private static FakeAccessDatabase Articulos(int id, string name)
    {
        var database = new FakeAccessDatabase();
        database.AddTable(
            new AccessTableSchema(
                "articulos",
                [
                    TypeMapperTests.Column("id", AccessTypeKind.Long, 3, 4, false, true),
                    TypeMapperTests.Column("nombre", AccessTypeKind.Text, 202, 50)
                ],
                [new("PrimaryKey", true, true, [new("id", 1, false)])]),
            [id, name]);
        return database;
    }

    private static MdbFileInfo WriteMdb(string directory, string name, string contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        var info = new FileInfo(path);
        return new MdbFileInfo(info.FullName, info.Name, info.Length, info.LastWriteTimeUtc);
    }
}

internal sealed class IsolatedSqlDatabase : IAsyncDisposable
{
    public const string MasterConnectionStringValue =
        "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true";

    public static bool IsAvailable { get; } = Probe();

    private IsolatedSqlDatabase(string databaseName, string connectionString)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public string MasterConnectionString => MasterConnectionStringValue;

    public static async Task<IsolatedSqlDatabase> CreateAsync()
    {
        var databaseName = "TPVONEMDB_TESTS_" + Guid.NewGuid().ToString("N");
        Assert.NotEqual("TPVONEMDB", databaseName);

        await using (var master = new SqlConnection(MasterConnectionStringValue))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{databaseName}];", master)
            {
                CommandTimeout = 60
            };
            await create.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(MasterConnectionStringValue)
        {
            InitialCatalog = databaseName
        };
        var database = new IsolatedSqlDatabase(databaseName, builder.ConnectionString);
        try
        {
            var installer = new SqlDatabaseInstaller(
                MasterConnectionStringValue,
                databaseName,
                60,
                NullLogger<SqlDatabaseInstaller>.Instance);
            await installer.EnsureInfrastructureAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> ListUserTablesAsync()
    {
        const string sql = """
            SELECT name
            FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo')
            ORDER BY name;
            """;
        var tables = new List<string>();
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public async Task<IReadOnlyList<string>> ListIndexNamesAsync(string tableName)
    {
        const string sql = """
            SELECT i.name
            FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U')
              AND i.name IS NOT NULL
            ORDER BY i.name;
            """;
        var names = new List<string>();
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@TableName", tableName);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public async Task<long> CountRowsAsync(string tableName)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT COUNT_BIG(*) FROM dbo.[{tableName.Replace("]", "]]", StringComparison.Ordinal)}];",
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task<byte[]?> ReadBinaryAsync(
        string tableName,
        string columnName,
        string idColumn,
        int id)
    {
        var value = await ReadScalarAsync(tableName, columnName, idColumn, id);
        return value is null or DBNull ? null : (byte[])value;
    }

    public async Task<string?> ReadNVarCharAsync(
        string tableName,
        string columnName,
        string idColumn,
        int id)
    {
        var value = await ReadScalarAsync(tableName, columnName, idColumn, id);
        return value is null or DBNull ? null : (string)value;
    }

    public async Task<bool?> ReadBitAsync(
        string tableName,
        string columnName,
        string idColumn,
        int id)
    {
        var value = await ReadScalarAsync(tableName, columnName, idColumn, id);
        return value is null or DBNull ? null : Convert.ToBoolean(value);
    }

    public async ValueTask DisposeAsync()
    {
        await using var master = new SqlConnection(MasterConnectionStringValue);
        await master.OpenAsync();
        var sql = $"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END
            """;
        await using var command = new SqlCommand(sql, master) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ReadScalarAsync(
        string tableName,
        string columnName,
        string idColumn,
        int id)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var quotedTable = tableName.Replace("]", "]]", StringComparison.Ordinal);
        var quotedColumn = columnName.Replace("]", "]]", StringComparison.Ordinal);
        var quotedId = idColumn.Replace("]", "]]", StringComparison.Ordinal);
        await using var command = new SqlCommand(
            $"SELECT [{quotedColumn}] FROM dbo.[{quotedTable}] WHERE [{quotedId}] = @Id;",
            connection);
        command.Parameters.AddWithValue("@Id", id);
        return await command.ExecuteScalarAsync();
    }

    private static bool Probe()
    {
        try
        {
            using var connection = new SqlConnection(MasterConnectionStringValue);
            connection.Open();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (!IsolatedSqlDatabase.IsAvailable)
        {
            Skip = "SQL Server no está disponible en localhost.";
        }
    }
}
