using System.Data.OleDb;
using MdbToSql.Core.Utilities;

namespace MdbToSql.Access;

public sealed class AccessConnectionFactory
{
    public const string JetProvider = "Microsoft.Jet.OLEDB.4.0";

    public OleDbConnection Open(string mdbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mdbPath);
        if (!File.Exists(mdbPath))
        {
            throw new FileNotFoundException($"No existe el archivo MDB '{mdbPath}'.", mdbPath);
        }

        var builder = new OleDbConnectionStringBuilder
        {
            Provider = JetProvider,
            DataSource = mdbPath
        };
        builder["Mode"] = 1;
        builder["Persist Security Info"] = false;

        var connection = new OleDbConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (Exception exception)
        {
            connection.Dispose();
            throw new InvalidOperationException(
                $"No se pudo abrir '{Path.GetFileName(mdbPath)}' con {JetProvider}. " +
                "No se realiza conversión Access 97 → Access 2003 automáticamente. " +
                $"Detalle: {exception.Message}",
                exception);
        }
    }
}

public sealed class AccessDatabaseScanner : Core.Import.IMdbScanner
{
    public IReadOnlyList<Core.Models.MdbFileInfo> Discover(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"No existe el directorio de origen '{sourceDirectory}'.");
        }

        return Directory.GetFiles(sourceDirectory, "*.mdb", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => file.Extension.Equals(".mdb", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new Core.Models.MdbFileInfo(
                file.FullName,
                file.Name,
                file.Length,
                file.LastWriteTimeUtc))
            .ToArray();
    }
}

public sealed class AccessDatabaseFactory : Core.Import.IAccessDatabaseFactory
{
    private readonly AccessConnectionFactory _connections = new();

    public Core.Import.IAccessDatabase Open(string mdbPath)
    {
        return new AccessDatabase(mdbPath, _connections.Open(mdbPath));
    }
}

internal sealed class AccessDatabase : Core.Import.IAccessDatabase
{
    private readonly string _mdbPath;
    private readonly OleDbConnection _connection;
    private readonly AccessSchemaReader _schemaReader;
    private readonly AccessIndexReader _indexReader;
    private readonly AccessTableDataReader _dataReader;

    public AccessDatabase(string mdbPath, OleDbConnection connection)
    {
        _mdbPath = mdbPath;
        _connection = connection;
        _schemaReader = new AccessSchemaReader();
        _indexReader = new AccessIndexReader();
        _dataReader = new AccessTableDataReader();
    }

    public IReadOnlyList<string> ListUserTables()
    {
        return _schemaReader.ListUserTables(_connection);
    }

    public Core.Models.AccessTableSchema ReadTable(string tableName)
    {
        var columns = _schemaReader.ReadColumns(_connection, _mdbPath, tableName);
        var indexes = _indexReader.ReadIndexes(_connection, tableName);
        return new(tableName, columns, indexes);
    }

    public long CountRows(string tableName)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {AccessIdentifier.Quote(tableName)}";
        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    public System.Data.Common.DbDataReader OpenReader(Core.Models.AccessTableSchema schema)
    {
        return _dataReader.Open(_connection, schema);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
