using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessSystemObject
{
    public static bool IsSystemName(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            || name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~", StringComparison.Ordinal);
    }
}

public sealed record AccessLinkClassification(
    AccessLinkKind Kind,
    string? SourcePath,
    bool IsUnc,
    bool IsLocal);

public static class AccessLinkClassifier
{
    public static AccessLinkClassification Classify(
        string? connect,
        string? sourceTableName,
        bool isLinked)
    {
        if (!isLinked)
        {
            return new AccessLinkClassification(AccessLinkKind.Local, null, false, true);
        }

        var text = connect ?? string.Empty;
        var sourcePath = ExtractSourcePath(text);
        var isUnc = ContainsUnc(text) || ContainsUnc(sourcePath);
        var isText = IsTextLink(text, sourceTableName);
        var isInterBase = IsInterBaseLink(text, sourcePath);
        var isOdbc = text.Contains("ODBC;", StringComparison.OrdinalIgnoreCase);
        var isMdb = ContainsMdb(text, sourcePath);

        var kind = isText ? AccessLinkKind.Text
            : isInterBase ? AccessLinkKind.InterBase
            : isOdbc ? AccessLinkKind.Odbc
            : isMdb ? AccessLinkKind.AccessMdb
            : AccessLinkKind.Unknown;

        var isLocal = !isUnc && (LooksLikeLocalPath(sourcePath) || LooksLikeLocalPath(text));
        return new AccessLinkClassification(kind, sourcePath, isUnc, isLocal);
    }

    public static string? ExtractSourcePath(string? connect)
    {
        if (string.IsNullOrWhiteSpace(connect))
        {
            return null;
        }

        foreach (var key in new[] { "DATABASE=", "Database=" })
        {
            var index = connect.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var start = index + key.Length;
            var end = connect.IndexOf(';', start);
            var value = end < 0 ? connect[start..] : connect[start..end];
            value = value.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static bool IsTextLink(string connect, string? sourceTableName)
    {
        return connect.Contains("Text;", StringComparison.OrdinalIgnoreCase)
            || connect.Contains("FMT=", StringComparison.OrdinalIgnoreCase)
            || (sourceTableName?.EndsWith(".TXT", StringComparison.OrdinalIgnoreCase) ?? false)
            || connect.Contains(".TXT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInterBaseLink(string connect, string? sourcePath)
    {
        return ContainsToken(connect, sourcePath, ".gdb")
            || ContainsToken(connect, sourcePath, "InterBase")
            || ContainsToken(connect, sourcePath, "ipvmain");
    }

    private static bool ContainsMdb(string connect, string? sourcePath)
    {
        return ContainsToken(connect, sourcePath, ".mdb");
    }

    private static bool ContainsToken(string connect, string? sourcePath, string token)
    {
        return connect.Contains(token, StringComparison.OrdinalIgnoreCase)
            || (sourcePath?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool ContainsUnc(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && (value.Contains(@"\\", StringComparison.Ordinal)
                || value.Contains("//", StringComparison.Ordinal));
    }

    private static bool LooksLikeLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || ContainsUnc(value))
        {
            return false;
        }

        return value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/');
    }
}

public static class FileIntegrity
{
    public static FileIntegritySnapshot Capture(string path)
    {
        var info = new FileInfo(path);
        return new FileIntegritySnapshot(
            Utilities.FileHashCalculator.CalculateSha256(path),
            info.Length,
            info.LastWriteTimeUtc);
    }

    public static bool EqualsSnapshot(FileIntegritySnapshot left, FileIntegritySnapshot right)
    {
        return string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase)
            && left.Size == right.Size
            && left.LastWriteUtc == right.LastWriteUtc;
    }
}

public static class AccessBooleanParser
{
    public static bool? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value is "True" or "true" or "-1" or "1")
        {
            return true;
        }

        if (value is "False" or "false" or "0")
        {
            return false;
        }

        return null;
    }
}
