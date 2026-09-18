using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MdbToSql.Core.Utilities;

public static class SqlIdentifier
{
    public const int MaxLength = 128;

    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    public static string Fit(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return identifier.Length <= MaxLength
            ? identifier
            : identifier[..MaxLength];
    }
}

public static class AccessIdentifier
{
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }
}

public static partial class DatabaseNameValidator
{
    public static void EnsureValid(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        if (!NameRegex().IsMatch(databaseName))
        {
            throw new InvalidOperationException(
                $"El nombre de base de datos '{databaseName}' no es válido. " +
                "Solo se permiten letras, números y guion bajo.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}

public static class StagingNames
{
    public static string NewToken()
    {
        return Guid.NewGuid().ToString("N");
    }

    public static string StagingTable(string tableName, string token)
    {
        return SqlIdentifier.Fit($"{tableName}__s{token}");
    }

    public static string BackupTable(string tableName, string token)
    {
        return SqlIdentifier.Fit($"{tableName}__b{token}");
    }

    public static string CanonicalIndexName(
        string tableName,
        string indexName,
        bool isPrimaryKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        return isPrimaryKey
            ? SqlIdentifier.Fit($"PK_{tableName}")
            : SqlIdentifier.Fit($"{tableName}_{indexName}");
    }

    public static string SuffixedIndex(string canonicalIndexName, string? suffix)
    {
        if (string.IsNullOrEmpty(suffix))
        {
            return canonicalIndexName;
        }

        return SqlIdentifier.Fit($"{canonicalIndexName}__{suffix}");
    }
}

public static class FileHashCalculator
{
    public static async Task<string> CalculateSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
