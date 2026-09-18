using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MdbToSql.Core.Import;
using MdbToSql.Core.Utilities;

namespace MdbToSql.SqlServer;

public sealed class SqlDatabaseInstaller : ISqlDatabaseInstaller
{
    private readonly string _serverConnectionString;
    private readonly string _databaseName;
    private readonly int _commandTimeout;
    private readonly ILogger<SqlDatabaseInstaller> _logger;

    public SqlDatabaseInstaller(
        string serverConnectionString,
        string databaseName,
        int commandTimeout,
        ILogger<SqlDatabaseInstaller> logger)
    {
        _serverConnectionString = serverConnectionString;
        _databaseName = databaseName;
        _commandTimeout = commandTimeout;
        _logger = logger;
    }

    public async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken = default)
    {
        DatabaseNameValidator.EnsureValid(_databaseName);

        var masterBuilder = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = "master"
        };

        _logger.LogInformation(
            "Comprobando existencia de base de datos {DatabaseName}...",
            _databaseName);

        await using var connection = new SqlConnection(masterBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "SELECT DB_ID(@DatabaseName);";
        existsCommand.Parameters.AddWithValue("@DatabaseName", _databaseName);
        var databaseId = await existsCommand.ExecuteScalarAsync(cancellationToken);
        if (databaseId is not null && databaseId is not DBNull)
        {
            _logger.LogInformation("La base de datos {DatabaseName} ya existe.", _databaseName);
            return;
        }

        _logger.LogInformation("Creando {DatabaseName}...", _databaseName);
        var escaped = _databaseName.Replace("]", "]]", StringComparison.Ordinal);
        await using var createCommand = connection.CreateCommand();
        createCommand.CommandText = $"CREATE DATABASE [{escaped}];";
        createCommand.CommandTimeout = Math.Max(60, _commandTimeout);
        await createCommand.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Base de datos {DatabaseName} creada correctamente.", _databaseName);
    }

    public async Task<bool> DestinationExistsAsync(CancellationToken cancellationToken = default)
    {
        DatabaseNameValidator.EnsureValid(_databaseName);
        var masterBuilder = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = "master"
        };
        await using var connection = new SqlConnection(masterBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_ID(@DatabaseName);";
        command.Parameters.AddWithValue("@DatabaseName", _databaseName);
        var databaseId = await command.ExecuteScalarAsync(cancellationToken);
        return databaseId is not null && databaseId is not DBNull;
    }

    public async Task EnsureInfrastructureAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(GetDestinationConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using (var createHistory = new SqlCommand(
                         """
                         IF OBJECT_ID(N'dbo.SchemaMigrations', N'U') IS NULL
                         BEGIN
                             CREATE TABLE dbo.SchemaMigrations
                             (
                                 MigrationId nvarchar(255) NOT NULL PRIMARY KEY,
                                 AppliedAt datetime2 NOT NULL DEFAULT SYSDATETIME()
                             );
                         END
                         """,
                         connection)
                     {
                         CommandTimeout = _commandTimeout
                     })
        {
            await createHistory.ExecuteNonQueryAsync(cancellationToken);
        }

        var applied = await LoadAppliedAsync(connection, cancellationToken);
        foreach (var (id, sql) in LoadEmbeddedMigrations())
        {
            if (applied.Contains(id))
            {
                continue;
            }

            foreach (var batch in SplitBatches(sql))
            {
                await using var command = new SqlCommand(batch, connection)
                {
                    CommandTimeout = _commandTimeout
                };
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var insert = new SqlCommand(
                "INSERT INTO dbo.SchemaMigrations (MigrationId) VALUES (@Id);",
                connection)
            {
                CommandTimeout = _commandTimeout
            };
            insert.Parameters.AddWithValue("@Id", id);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public string GetDestinationConnectionString()
    {
        DatabaseNameValidator.EnsureValid(_databaseName);
        var builder = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = _databaseName
        };
        return builder.ConnectionString;
    }

    private async Task<HashSet<string>> LoadAppliedAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(
            "SELECT MigrationId FROM dbo.SchemaMigrations;",
            connection)
        {
            CommandTimeout = _commandTimeout
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    private static IReadOnlyList<(string Id, string Sql)> LoadEmbeddedMigrations()
    {
        var assembly = typeof(SqlDatabaseInstaller).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"No se encontró el recurso '{name}'.");
                using var reader = new StreamReader(stream);
                return (MigrationIdFromResource(name), reader.ReadToEnd());
            })
            .ToArray();
    }

    private static string MigrationIdFromResource(string resourceName)
    {
        const string marker = ".Migrations.";
        var index = resourceName.LastIndexOf(marker, StringComparison.Ordinal);
        var file = index >= 0
            ? resourceName[(index + marker.Length)..]
            : resourceName;
        return Path.GetFileNameWithoutExtension(file);
    }

    private static IEnumerable<string> SplitBatches(string sql)
    {
        var batch = new System.Text.StringBuilder();
        using var reader = new StringReader(sql);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                var text = batch.ToString().Trim();
                if (text.Length > 0)
                {
                    yield return text;
                }

                batch.Clear();
                continue;
            }

            batch.AppendLine(line);
        }

        var final = batch.ToString().Trim();
        if (final.Length > 0)
        {
            yield return final;
        }
    }
}
