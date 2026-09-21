namespace MdbToSql.Core.Exceptions;

public class MdbToSqlException : Exception
{
    public MdbToSqlException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class UnsupportedAccessTypeException : MdbToSqlException
{
    public UnsupportedAccessTypeException(
        string mdbPath,
        string tableName,
        string columnName,
        string foundType)
        : base(
            $"Tipo Access no soportado.{Environment.NewLine}" +
            $"  MDB: {mdbPath}{Environment.NewLine}" +
            $"  Tabla: {tableName}{Environment.NewLine}" +
            $"  Columna: {columnName}{Environment.NewLine}" +
            $"  Tipo encontrado: {foundType}")
    {
        MdbPath = mdbPath;
        TableName = tableName;
        ColumnName = columnName;
        FoundType = foundType;
    }

    public string MdbPath { get; }

    public string TableName { get; }

    public string ColumnName { get; }

    public string FoundType { get; }
}

public sealed class DataImportException : MdbToSqlException
{
    public DataImportException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class SchemaReadException : MdbToSqlException
{
    public SchemaReadException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class ProtectedTableException : MdbToSqlException
{
    public ProtectedTableException(string tableName)
        : base(
            $"La tabla '{tableName}' es de infraestructura interna del importador " +
            "y no puede crearse ni sobrescribirse desde un MDB.")
    {
        TableName = tableName;
    }

    public string TableName { get; }
}

public sealed class TableNameCollisionException : MdbToSqlException
{
    public TableNameCollisionException(string tableName, IReadOnlyList<string> mdbNames)
        : base(
            $"La tabla '{tableName}' aparece en más de un MDB " +
            $"({string.Join(", ", mdbNames)}). " +
            "No se mezclan filas ni se sobrescribe automáticamente.")
    {
        TableName = tableName;
        MdbNames = mdbNames;
    }

    public string TableName { get; }

    public IReadOnlyList<string> MdbNames { get; }
}

public sealed class UnsafeAccessStartupException : MdbToSqlException
{
    public UnsafeAccessStartupException(string message)
        : base(message)
    {
    }

    public UnsafeAccessStartupException(
        IReadOnlyList<string> loadedForms,
        IReadOnlyList<string> loadedReports)
        : base(
            "La copia de análisis abrió objetos de arranque. Análisis abortado." +
            Environment.NewLine +
            $"  Forms cargados: {(loadedForms.Count == 0 ? "(ninguno)" : string.Join(", ", loadedForms))}" +
            Environment.NewLine +
            $"  Reports cargados: {(loadedReports.Count == 0 ? "(ninguno)" : string.Join(", ", loadedReports))}")
    {
        LoadedForms = loadedForms;
        LoadedReports = loadedReports;
    }

    public IReadOnlyList<string> LoadedForms { get; } = [];

    public IReadOnlyList<string> LoadedReports { get; } = [];
}

public sealed class AccessApplicationAnalysisException : MdbToSqlException
{
    public AccessApplicationAnalysisException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class AccessSessionUnsafeException : MdbToSqlException
{
    public AccessSessionUnsafeException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
