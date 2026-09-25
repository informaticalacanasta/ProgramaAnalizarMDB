using System.Text.RegularExpressions;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessVbaDataUseExtractor
{
    private static readonly Regex OpenDatabase = new(
        @"^\s*Set\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*OpenDatabase\s*\(\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex OpenRecordset = new(
        @"^\s*Set\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([A-Za-z_][A-Za-z0-9_]*)\.OpenRecordset\s*\((.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CurrentDbRecordset = new(
        @"^\s*Set\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*CurrentDb\.OpenRecordset\s*\((.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex IndexAssign = new(
        @"^\s*([A-Za-z_][A-Za-z0-9_]*)\.Index\s*=\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SeekCall = new(
        @"^\s*([A-Za-z_][A-Za-z0-9_]*)\.Seek\s+(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Bang = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)!([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FieldsOrdinal = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\.Fields\s*\(\s*(\d+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Execute = new(
        @"\b(?:CurrentDb|[A-Za-z_][A-Za-z0-9_]*)\.Execute\s+(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Quoted = new("\"([^\"]+)\"", RegexOptions.Compiled);

    public sealed record RecordsetOrigin(
        string Procedure,
        string Variable,
        string ObjectName,
        string Database,
        bool Dynamic);

    public static (
        IReadOnlyList<AccessTableOperation> Operations,
        IReadOnlyList<AccessVbaFieldMention> Fields,
        IReadOnlyList<AccessSeekUse> Searches,
        IReadOnlyDictionary<string, RecordsetOrigin> LastOrigins)
        Extract(string procedureName, string procedureSource, int startLine)
    {
        var operations = new List<AccessTableOperation>();
        var fields = new List<AccessVbaFieldMention>();
        var searches = new List<AccessSeekUse>();
        var databases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var origins = new Dictionary<string, RecordsetOrigin>(StringComparer.OrdinalIgnoreCase);
        var indexes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = procedureSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var line = Strip(raw);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var number = startLine + index;
            var openDb = OpenDatabase.Match(line);
            if (openDb.Success)
            {
                databases[openDb.Groups[1].Value] = openDb.Groups[2].Value;
            }

            var currentRs = CurrentDbRecordset.Match(line);
            if (currentRs.Success)
            {
                BindRecordset(
                    origins,
                    operations,
                    procedureName,
                    number,
                    currentRs.Groups[1].Value,
                    "CurrentDb",
                    currentRs.Groups[2].Value,
                    raw);
            }

            var rs = OpenRecordset.Match(line);
            if (rs.Success
                && !rs.Groups[2].Value.Equals("CurrentDb", StringComparison.OrdinalIgnoreCase))
            {
                databases.TryGetValue(rs.Groups[2].Value, out var database);
                BindRecordset(
                    origins,
                    operations,
                    procedureName,
                    number,
                    rs.Groups[1].Value,
                    database ?? rs.Groups[2].Value,
                    rs.Groups[3].Value,
                    raw);
            }

            var indexMatch = IndexAssign.Match(line);
            if (indexMatch.Success)
            {
                indexes[indexMatch.Groups[1].Value] = indexMatch.Groups[2].Value;
            }

            var seek = SeekCall.Match(line);
            if (seek.Success)
            {
                origins.TryGetValue(seek.Groups[1].Value, out var origin);
                indexes.TryGetValue(seek.Groups[1].Value, out var indexName);
                searches.Add(new AccessSeekUse(
                    procedureName,
                    number,
                    seek.Groups[1].Value,
                    origin?.ObjectName,
                    indexName,
                    SplitSeekKeys(seek.Groups[2].Value),
                    raw.Trim()));
                operations.Add(new AccessTableOperation(procedureName, number, "Seek", origin?.ObjectName, raw.Trim()));
            }

            AddRecordOp(operations, origins, procedureName, number, line, raw, @"([A-Za-z_][A-Za-z0-9_]*)\.AddNew\b", "Insert");
            AddRecordOp(operations, origins, procedureName, number, line, raw, @"([A-Za-z_][A-Za-z0-9_]*)\.Edit\b", "Update");
            AddRecordOp(operations, origins, procedureName, number, line, raw, @"([A-Za-z_][A-Za-z0-9_]*)\.Delete\b", "Delete");

            var execute = Execute.Match(line);
            if (execute.Success)
            {
                var sql = Quoted.Match(execute.Groups[1].Value);
                var sqlText = sql.Success ? sql.Groups[1].Value : execute.Groups[1].Value;
                var kind = KindFromSql(sqlText);
                var targets = AccessSqlIdentifierExtractor.Extract(sqlText);
                if (targets.Count == 0)
                {
                    operations.Add(new AccessTableOperation(procedureName, number, kind, null, raw.Trim()));
                }
                else
                {
                    foreach (var target in targets)
                    {
                        operations.Add(new AccessTableOperation(procedureName, number, kind, target, raw.Trim()));
                    }
                }
            }

            foreach (Match bang in Bang.Matches(line))
            {
                var variable = bang.Groups[1].Value;
                origins.TryGetValue(variable, out var origin);
                var role = line.Contains(variable + "!" + bang.Groups[2].Value + " =")
                    || Regex.IsMatch(line, variable + @"!" + bang.Groups[2].Value + @"\s*=")
                    ? "Write"
                    : "Read";
                fields.Add(new AccessVbaFieldMention(
                    procedureName,
                    number,
                    variable,
                    origin?.ObjectName,
                    bang.Groups[2].Value,
                    null,
                    role,
                    raw.Trim(),
                    NameVerified: false));
            }

            foreach (Match ordinal in FieldsOrdinal.Matches(line))
            {
                var variable = ordinal.Groups[1].Value;
                origins.TryGetValue(variable, out var origin);
                var role = line.Contains(".Fields(" + ordinal.Groups[2].Value + ") =")
                    || Regex.IsMatch(line, @"Fields\s*\(\s*" + ordinal.Groups[2].Value + @"\s*\)\s*=")
                    ? "Write"
                    : "Read";
                fields.Add(new AccessVbaFieldMention(
                    procedureName,
                    number,
                    variable,
                    origin?.ObjectName,
                    null,
                    int.Parse(ordinal.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                    role,
                    raw.Trim(),
                    NameVerified: false));
            }
        }

        return (operations, fields, searches, origins);
    }

    private static void BindRecordset(
        Dictionary<string, RecordsetOrigin> origins,
        List<AccessTableOperation> operations,
        string procedure,
        int line,
        string variable,
        string database,
        string argument,
        string raw)
    {
        var quoted = Quoted.Match(argument);
        var dynamic = !quoted.Success;
        var name = quoted.Success ? quoted.Groups[1].Value : argument.Trim().TrimEnd(')');
        if (name.StartsWith("select ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("insert ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("delete ", StringComparison.OrdinalIgnoreCase))
        {
            dynamic = true;
        }

        origins[variable] = new RecordsetOrigin(procedure, variable, name, database, dynamic);
        operations.Add(new AccessTableOperation(
            procedure,
            line,
            dynamic ? "ReadDynamic" : "Read",
            dynamic ? null : name,
            raw.Trim()));
    }

    private static void AddRecordOp(
        List<AccessTableOperation> operations,
        Dictionary<string, RecordsetOrigin> origins,
        string procedure,
        int line,
        string code,
        string raw,
        string pattern,
        string kind)
    {
        var match = Regex.Match(code, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return;
        }

        origins.TryGetValue(match.Groups[1].Value, out var origin);
        operations.Add(new AccessTableOperation(procedure, line, kind, origin?.ObjectName, raw.Trim()));
    }

    private static IReadOnlyList<string> SplitSeekKeys(string argument)
    {
        var text = argument.Trim().TrimStart('=').Trim();
        if (text.StartsWith("\"=\"", StringComparison.Ordinal) || text.StartsWith("\"=\"", StringComparison.OrdinalIgnoreCase))
        {
            text = Regex.Replace(text, @"^""="",\s*", string.Empty);
        }

        return text.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
    }

    private static string KindFromSql(string sql)
    {
        if (Regex.IsMatch(sql, @"^\s*INSERT\b", RegexOptions.IgnoreCase))
        {
            return "Insert";
        }

        if (Regex.IsMatch(sql, @"^\s*DELETE\b", RegexOptions.IgnoreCase))
        {
            return "Delete";
        }

        if (Regex.IsMatch(sql, @"^\s*UPDATE\b", RegexOptions.IgnoreCase))
        {
            return "Update";
        }

        return "Execute";
    }

    private static string Strip(string line)
    {
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (line[index] == '\'' && !inQuotes)
            {
                return line[..index];
            }
        }

        return line;
    }
}
