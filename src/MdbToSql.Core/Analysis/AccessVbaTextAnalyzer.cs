using System.Text.RegularExpressions;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessEventProcedureNames
{
    public static string Suffix(string eventName)
    {
        if (eventName.StartsWith("On", StringComparison.OrdinalIgnoreCase) && eventName.Length > 2)
        {
            return eventName[2..];
        }

        return eventName;
    }

    public static IReadOnlyList<string> Candidates(
        string objectName,
        AccessObjectKind objectKind,
        string eventName,
        string? eventProcPrefix,
        string formName)
    {
        var suffix = Suffix(eventName);
        var names = new List<string>();
        if (objectKind == AccessObjectKind.Form)
        {
            names.Add("Form_" + suffix);
            AddPrefixed(names, eventProcPrefix, suffix);
            AddPrefixed(names, formName, suffix);
        }
        else
        {
            AddPrefixed(names, objectName, suffix);
            if (!string.IsNullOrWhiteSpace(eventProcPrefix))
            {
                names.Add(eventProcPrefix + "_" + objectName + "_" + suffix);
            }
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static AccessEventProcedureBinding Bind(
        string objectName,
        AccessObjectKind objectKind,
        string eventName,
        string? bindingExpression,
        string? eventProcPrefix,
        string formName,
        IReadOnlyList<AccessVbaProcedure> procedures)
    {
        var candidates = Candidates(objectName, objectKind, eventName, eventProcPrefix, formName);
        var resolved = candidates.FirstOrDefault(candidate =>
            procedures.Any(procedure =>
                string.Equals(procedure.Name, candidate, StringComparison.OrdinalIgnoreCase)));
        return new AccessEventProcedureBinding(
            objectName,
            objectKind.ToString(),
            eventName,
            bindingExpression,
            eventProcPrefix,
            candidates,
            resolved,
            resolved is not null);
    }

    private static void AddPrefixed(List<string> names, string? prefix, string suffix)
    {
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            names.Add(prefix + "_" + suffix);
        }
    }
}

public static class AccessVbaTextAnalyzer
{
    private static readonly Regex ProcedureHeader = new(
        @"^\s*(?:(?:Public|Private|Friend)\s+)?(?:Static\s+)?(Sub|Function|Property\s+Get|Property\s+Let|Property\s+Set)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProcedureEnd = new(
        @"^\s*End\s+(Sub|Function|Property)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CallStatement = new(
        @"^\s*Call\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QueryDefs = new(
        @"\bQueryDefs\s*\(\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly (string Kind, Regex Pattern)[] KnownCalls =
    [
        ("DoCmd.OpenForm", new Regex(@"DoCmd\.OpenForm\b(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("DoCmd.OpenReport", new Regex(@"DoCmd\.OpenReport\b(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("DoCmd.OpenQuery", new Regex(@"DoCmd\.OpenQuery\b(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("DoCmd.OpenTable", new Regex(@"DoCmd\.OpenTable\b(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("DoCmd.RunMacro", new Regex(@"DoCmd\.RunMacro\b(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("DoCmd.RunSQL", new Regex(@"DoCmd\.RunSQL\b(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("CurrentDb.Execute", new Regex(@"CurrentDb\.Execute\b(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("OpenRecordset", new Regex(@"OpenRecordset\s*\((.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    ];

    private static readonly Regex Quoted = new("\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex Unc = new(@"\\\\[^\s""']+", RegexOptions.Compiled);
    private static readonly Regex DrivePath = new(@"[A-Za-z]:\\[^\s""']+", RegexOptions.Compiled);
    private static readonly Regex ExternalHint = new(
        @"ipvmain\.gdb|\.gdb\b|ODBC;|DATABASE=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<AccessVbaProcedure> ParseProcedures(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return [];
        }

        var lines = SplitLines(source);
        var procedures = new List<AccessVbaProcedure>();
        string? kind = null;
        string? name = null;
        var start = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var header = ProcedureHeader.Match(lines[index]);
            if (header.Success && kind is null)
            {
                kind = NormalizeKind(header.Groups[1].Value);
                name = header.Groups[2].Value;
                start = lineNumber;
                continue;
            }

            if (kind is not null && ProcedureEnd.IsMatch(lines[index]))
            {
                procedures.Add(new AccessVbaProcedure(kind, name ?? "(sin nombre)", start, lineNumber));
                kind = null;
                name = null;
            }
        }

        if (kind is not null && name is not null)
        {
            procedures.Add(new AccessVbaProcedure(kind, name, start, lines.Length));
        }

        return procedures;
    }

    public static IReadOnlyList<AccessVbaProcedure> FindByName(string? source, string name)
    {
        return ParseProcedures(source)
            .Where(procedure => string.Equals(procedure.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static string ExtractRange(string? source, int startLine, int endLine)
    {
        if (string.IsNullOrEmpty(source) || startLine < 1 || endLine < startLine)
        {
            return string.Empty;
        }

        var lines = SplitLines(source);
        var from = Math.Min(startLine, lines.Length);
        var to = Math.Min(endLine, lines.Length);
        return string.Join("\n", lines[(from - 1)..to]);
    }

    public static string ExtractDeclarations(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var first = ParseProcedures(source).FirstOrDefault();
        if (first is null)
        {
            return source;
        }

        return first.StartLine <= 1
            ? string.Empty
            : ExtractRange(source, 1, first.StartLine - 1);
    }

    public static string? Signature(string? source, AccessVbaProcedure procedure)
    {
        var text = ExtractRange(source, procedure.StartLine, procedure.StartLine);
        var line = text.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    public static IReadOnlyList<AccessVbaReference> InRange(
        IReadOnlyList<AccessVbaReference> references,
        int startLine,
        int endLine)
    {
        return references
            .Where(reference => reference.Line >= startLine && reference.Line <= endLine)
            .ToList();
    }

    public static IReadOnlyList<AccessVbaReference> ParseFacts(string? source, int startLine, int endLine)
    {
        if (string.IsNullOrEmpty(source) || startLine < 1 || endLine < startLine)
        {
            return [];
        }

        var lines = SplitLines(source);
        var facts = new List<AccessVbaReference>();
        var to = Math.Min(endLine, lines.Length);
        for (var index = startLine - 1; index < to; index++)
        {
            var raw = lines[index];
            var line = StripComment(raw);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var lineNumber = index + 1;
            AddFact(facts, line, raw, lineNumber, @"\bOn\s+Error\b", "ErrorHandler");
            AddFact(facts, line, raw, lineNumber, @"\bInputBox\b", "UserPrompt");
            AddFact(facts, line, raw, lineNumber, @"\bMsgBox\b", "UserMessage");
            AddFact(facts, line, raw, lineNumber, @"\bShell\b", "Shell");
            AddFact(facts, line, raw, lineNumber, @"\bOpenDatabase\b", "Dao");
            AddFact(facts, line, raw, lineNumber, @"\bCurrentDb\b", "Dao");
            AddFact(facts, line, raw, lineNumber, @"\bOpenRecordset\b", "Dao");
            AddFact(facts, line, raw, lineNumber, @"\bD(?:Lookup|Count|Max|Min|Sum|First|Last)\b", "DomainFunction");
            AddFact(facts, line, raw, lineNumber, @"\bOpen\s+[""#]", "FileIo");
            AddFact(facts, line, raw, lineNumber, @"\b(?:Line\s+)?Input\s+#", "FileIo");
            AddFact(facts, line, raw, lineNumber, @"\bPrint\s+#", "FileIo");
            AddFact(facts, line, raw, lineNumber, @"\bFileCopy\b", "FileCopy");
            AddFact(facts, line, raw, lineNumber, @"\bKill\b", "FileDelete");
            AddFact(facts, line, raw, lineNumber, @"\bName\s+.+\s+As\b", "FileMove");
            AddFact(facts, line, raw, lineNumber, @"\bMkDir\b", "MkDir");
            AddFact(facts, line, raw, lineNumber, @"\bDir\s*\(", "Dir");
            AddFact(facts, line, raw, lineNumber, @"\bShell\b", "Shell");
            AddFact(facts, line, raw, lineNumber, @"\bAddNew\b", "RecordAdd");
            AddFact(facts, line, raw, lineNumber, @"\b\.Edit\b|\.Edit\b", "RecordEdit");
            AddFact(facts, line, raw, lineNumber, @"\b\.Update\b", "RecordUpdate");
            AddFact(facts, line, raw, lineNumber, @"\b\.Seek\b", "RecordSeek");

            var operador = Regex.Match(line, @"\bOPERADOR\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (operador.Success)
            {
                var write = Regex.IsMatch(
                    line,
                    @"^\s*(?:Set\s+|Let\s+)?OPERADOR\s*=|(?:Then|Else)\s+(?:Set\s+|Let\s+)?OPERADOR\s*=|(?:Line\s+)?Input\s+#\d+\s*,\s*OPERADOR\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                facts.Add(new AccessVbaReference(
                    write ? "GlobalWrite" : "GlobalRead",
                    "OPERADOR",
                    Dynamic: false,
                    lineNumber,
                    raw.Trim()));
            }
        }

        return facts;
    }

    private static void AddFact(
        List<AccessVbaReference> facts,
        string line,
        string raw,
        int lineNumber,
        string pattern,
        string kind)
    {
        var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            facts.Add(new AccessVbaReference(kind, match.Value, false, lineNumber, raw.Trim()));
        }
    }

    public static IReadOnlyList<AccessVbaReference> ParseReferences(
        string? source,
        IReadOnlyList<AccessVbaProcedure> procedures)
    {
        if (string.IsNullOrEmpty(source))
        {
            return [];
        }

        var lines = SplitLines(source);
        var references = new List<AccessVbaReference>();
        var procedureNames = procedures
            .Select(procedure => procedure.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = StripComment(lines[index]);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var lineNumber = index + 1;
            foreach (var (kind, pattern) in KnownCalls)
            {
                var match = pattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var argument = match.Groups[1].Value.Trim();
                var quoted = Quoted.Match(argument);
                var dynamic = !quoted.Success;
                references.Add(new AccessVbaReference(
                    kind,
                    quoted.Success ? quoted.Groups[1].Value : (string.IsNullOrWhiteSpace(argument) ? null : argument.Trim()),
                    dynamic,
                    lineNumber,
                    lines[index].Trim()));
            }

            var call = CallStatement.Match(line);
            if (call.Success)
            {
                var target = call.Groups[1].Value;
                references.Add(new AccessVbaReference(
                    procedureNames.Contains(target) ? "SameModule" : "Call",
                    target,
                    Dynamic: false,
                    lineNumber,
                    lines[index].Trim()));
            }

            var queryDef = QueryDefs.Match(line);
            if (queryDef.Success)
            {
                references.Add(new AccessVbaReference(
                    "QueryDefs",
                    queryDef.Groups[1].Value,
                    Dynamic: false,
                    lineNumber,
                    lines[index].Trim()));
            }

            foreach (Match path in Unc.Matches(line))
            {
                references.Add(new AccessVbaReference("ExternalPath", path.Value, false, lineNumber, lines[index].Trim()));
            }

            foreach (Match path in DrivePath.Matches(line))
            {
                references.Add(new AccessVbaReference("ExternalPath", path.Value, false, lineNumber, lines[index].Trim()));
            }

            if (ExternalHint.IsMatch(line)
                && !references.Any(item => item.Line == lineNumber && item.Kind == "ExternalPath"))
            {
                references.Add(new AccessVbaReference("ExternalHint", null, true, lineNumber, lines[index].Trim()));
            }

            var current = ProcedureAt(procedures, lineNumber);
            foreach (var procedure in procedureNames)
            {
                if (current is not null
                    && string.Equals(current, procedure, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Regex.IsMatch(line, $@"\b{Regex.Escape(procedure)}\s*\(", RegexOptions.IgnoreCase))
                {
                    references.Add(new AccessVbaReference(
                        "SameModule",
                        procedure,
                        false,
                        lineNumber,
                        lines[index].Trim()));
                }
            }
        }

        return references;
    }

    private static string? ProcedureAt(IReadOnlyList<AccessVbaProcedure> procedures, int line)
    {
        return procedures
            .FirstOrDefault(procedure => line >= procedure.StartLine && line <= procedure.EndLine)
            ?.Name;
    }

    private static string NormalizeKind(string kind)
    {
        var trimmed = Regex.Replace(kind, @"\s+", " ").Trim();
        return trimmed.ToUpperInvariant() switch
        {
            "SUB" => "Sub",
            "FUNCTION" => "Function",
            "PROPERTY GET" => "Property Get",
            "PROPERTY LET" => "Property Let",
            "PROPERTY SET" => "Property Set",
            _ => trimmed
        };
    }

    private static string[] SplitLines(string source)
    {
        return source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    private static string StripComment(string line)
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
