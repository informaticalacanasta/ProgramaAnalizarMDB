using System.Text.RegularExpressions;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessSqlIdentifierExtractor
{
    private static readonly Regex Clause = new(
        @"\b(?:FROM|INTO|UPDATE|JOIN)\s+(?<id>\[[^\]]+\]|`[^`]+`|""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<string> Extract(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        var names = new List<string>();
        foreach (Match match in Clause.Matches(sql))
        {
            var raw = match.Groups["id"].Value;
            var name = Unwrap(raw);
            if (string.IsNullOrWhiteSpace(name) || IsKeyword(name))
            {
                continue;
            }

            if (!names.Exists(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)))
            {
                names.Add(name);
            }
        }

        return names;
    }

    public static string IdentifierRegions(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        var matches = Regex.Matches(
            sql,
            @"\b(?:FROM|JOIN|INTO|UPDATE)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0)
        {
            return sql;
        }

        return string.Join("\n", matches.Select(match => sql[match.Index..]));
    }

    public static IReadOnlyList<string> FindKnown(string? sql, IEnumerable<string> knownNames)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        var found = new List<string>();
        foreach (var name in knownNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (Regex.IsMatch(
                    sql,
                    @"\b" + Regex.Escape(name) + @"\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                && !found.Exists(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(name);
            }
        }

        return found;
    }

    public static string Unwrap(string identifier)
    {
        var trimmed = identifier.Trim();
        if (trimmed.Length >= 2
            && ((trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                || (trimmed.StartsWith('`') && trimmed.EndsWith('`'))
                || (trimmed.StartsWith('"') && trimmed.EndsWith('"'))))
        {
            return trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool IsKeyword(string name)
    {
        return name.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            || name.Equals("VALUES", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SET", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record AccessObjectUse(
    string Name,
    string Kind,
    string Role,
    string Evidence,
    int? Line,
    bool Dynamic);

public sealed record AccessTableUse(
    string Name,
    bool IsLinked,
    AccessLinkKind LinkKind,
    bool IsUnc,
    string? SourceTableName,
    IReadOnlyList<string> UsedFrom);

public sealed record AccessQueryUse(
    string Name,
    string? Sql,
    int? Type,
    string TypeName,
    bool? ReturnsRecords,
    string? ConnectPresent,
    IReadOnlyList<AccessQueryParameter> Parameters,
    IReadOnlyList<string> UsedFrom,
    IReadOnlyList<string> TablesInSql,
    IReadOnlyList<string> NestedQueriesPending,
    IReadOnlyList<string> UnknownIdentifiers,
    IReadOnlyList<string> Warnings);

public static class AccessQueryTypeNames
{
    public static string Name(int? type)
    {
        return type switch
        {
            0 => "Select",
            1 => "Crosstab",
            2 => "Delete",
            3 => "Update",
            4 => "Append",
            5 => "MakeTable",
            6 => "DDL",
            7 => "SQLPassThrough",
            8 => "SetOperation",
            9 => "SPTBulk",
            10 => "Compound",
            128 => "Union",
            _ => type?.ToString() ?? "Unknown"
        };
    }

    public static bool MayModifyData(int? type)
    {
        return type is 2 or 3 or 4 or 5 or 6 or 7 or 9;
    }
}

public static class AccessDirectReferenceResolver
{
    public static (
        IReadOnlyList<AccessQueryUse> Queries,
        IReadOnlyList<AccessTableUse> Tables,
        IReadOnlyList<AccessObjectUse> Uses,
        IReadOnlyList<string> Pending) Resolve(
        AccessFormAnalysis form,
        IReadOnlyList<AccessVbaReference> references,
        IReadOnlyList<AccessQueryAnalysis> allQueries,
        IReadOnlyList<AccessTableReference> allTables)
    {
        var queryMap = allQueries.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var tableMap = allTables.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var uses = new List<AccessObjectUse>();
        var queryFrom = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var tableFrom = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>();

        AddNamed(form.RecordSource, form.RecordSourceKind, "RecordSource", form.Name, null);
        foreach (var control in form.Controls)
        {
            if (control.RowSourceKind is AccessRowSourceKind.Table or AccessRowSourceKind.SavedQuery
                or AccessRowSourceKind.SqlText)
            {
                AddNamed(
                    control.RowSource,
                    ToRecordKind(control.RowSourceKind),
                    "RowSource",
                    control.Name,
                    null);
            }

            if (control.ControlType == AccessControlTypeKind.SubForm
                && !string.IsNullOrWhiteSpace(control.SourceObject))
            {
                uses.Add(new AccessObjectUse(
                    control.SourceObject,
                    "Form",
                    "SubForm",
                    control.Name + ".SourceObject",
                    null,
                    false));
                pending.Add("Subform '" + control.SourceObject + "' no abierto.");
            }
        }

        foreach (var reference in references)
        {
            switch (reference.Kind)
            {
                case "DoCmd.OpenQuery":
                case "QueryDefs":
                    AddVbaNamed(reference, "Query", queryMap.ContainsKey);
                    break;
                case "DoCmd.OpenTable":
                    AddVbaNamed(reference, "Table", tableMap.ContainsKey);
                    break;
                case "OpenRecordset":
                    if (reference.Target is not null && AccessRecordSourceClassifier.LooksLikeSql(reference.Target))
                    {
                        AddSqlText(reference.Target, reference.Kind, "VBA L" + reference.Line, reference.Line, reference.Dynamic);
                    }
                    else
                    {
                        AddVbaNamed(reference, "Recordset", name => queryMap.ContainsKey(name) || tableMap.ContainsKey(name));
                    }

                    break;
                case "DoCmd.RunSQL":
                case "CurrentDb.Execute":
                    AddSqlText(reference.Target, reference.Kind, "VBA L" + reference.Line, reference.Line, reference.Dynamic);
                    break;
                case "DoCmd.OpenForm":
                    if (reference.Dynamic || string.IsNullOrWhiteSpace(reference.Target))
                    {
                        pending.Add("L" + reference.Line + " OpenForm dinámico: " + reference.Evidence);
                    }
                    else if (!reference.Target.Equals(form.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        uses.Add(new AccessObjectUse(reference.Target, "Form", reference.Kind, reference.Evidence, reference.Line, false));
                        pending.Add("L" + reference.Line + " formulario '" + reference.Target + "' no analizado.");
                    }

                    break;
                case "DoCmd.OpenReport":
                    if (reference.Dynamic || string.IsNullOrWhiteSpace(reference.Target))
                    {
                        pending.Add("L" + reference.Line + " OpenReport dinámico: " + reference.Evidence);
                    }
                    else
                    {
                        uses.Add(new AccessObjectUse(reference.Target, "Report", reference.Kind, reference.Evidence, reference.Line, false));
                        pending.Add("L" + reference.Line + " informe '" + reference.Target + "' no analizado.");
                    }

                    break;
                case "Call":
                    pending.Add("L" + reference.Line + " procedimiento '" + reference.Target + "' fuera de este módulo; no seguido.");
                    break;
                case "ExternalPath":
                case "ExternalHint":
                case "Shell":
                case "FileIo":
                    pending.Add("L" + reference.Line + " " + reference.Kind + " " + (reference.Target ?? "") + " no abierto ni ejecutado.");
                    break;
            }
        }

        var queryUses = new List<AccessQueryUse>();
        foreach (var (name, origins) in queryFrom.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!queryMap.TryGetValue(name, out var query))
            {
                pending.Add("QueryDef '" + name + "' referenciada y no encontrada en DAO.");
                continue;
            }

            var identifiers = AccessSqlIdentifierExtractor.Extract(query.Sql);
            var tablesInSql = new List<string>();
            var nested = new List<string>();
            var unknown = new List<string>();
            foreach (var identifier in identifiers)
            {
                if (tableMap.ContainsKey(identifier))
                {
                    tablesInSql.Add(identifier);
                    AddOrigin(tableFrom, identifier, "QueryDef " + name);
                }
                else if (queryMap.ContainsKey(identifier)
                    && !identifier.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    nested.Add(identifier);
                    pending.Add("QueryDef anidada '" + identifier + "' en '" + name + "'; no seguida.");
                }
                else
                {
                    unknown.Add(identifier);
                }
            }

            if (!string.IsNullOrWhiteSpace(query.Connect))
            {
                pending.Add("QueryDef '" + name + "' tiene Connect; no se abre la conexión.");
            }

            queryUses.Add(new AccessQueryUse(
                query.Name,
                query.Sql,
                query.Type,
                AccessQueryTypeNames.Name(query.Type),
                query.ReturnsRecords,
                string.IsNullOrWhiteSpace(query.Connect) ? null : "(presente; no se abre)",
                query.Parameters,
                origins,
                tablesInSql,
                nested,
                unknown,
                query.Warnings));
        }

        var tableUses = tableFrom
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                tableMap.TryGetValue(item.Key, out var table);
                return new AccessTableUse(
                    item.Key,
                    table?.IsLinked ?? false,
                    table?.LinkKind ?? AccessLinkKind.Unknown,
                    table?.IsUnc ?? false,
                    table?.SourceTableName,
                    item.Value);
            })
            .ToList();

        return (
            queryUses,
            tableUses,
            uses,
            pending.Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        void AddNamed(string? name, AccessRecordSourceKind kind, string role, string source, int? line)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (kind == AccessRecordSourceKind.SqlText)
            {
                AddSqlText(name, role, source, line, dynamic: false);
                return;
            }

            if (kind == AccessRecordSourceKind.SavedQuery)
            {
                uses.Add(new AccessObjectUse(name.Trim(), "Query", role, source, line, false));
                AddOrigin(queryFrom, name.Trim(), role + " " + source);
                return;
            }

            if (kind == AccessRecordSourceKind.Table)
            {
                uses.Add(new AccessObjectUse(name.Trim(), "Table", role, source, line, false));
                AddOrigin(tableFrom, name.Trim(), role + " " + source);
                return;
            }

            if (kind == AccessRecordSourceKind.Unknown)
            {
                pending.Add(role + " '" + name + "' no resuelto como tabla ni QueryDef.");
            }
        }

        void AddVbaNamed(AccessVbaReference reference, string fallbackKind, Func<string, bool> known)
        {
            if (reference.Dynamic || string.IsNullOrWhiteSpace(reference.Target))
            {
                pending.Add("L" + reference.Line + " " + reference.Kind + " dinámico: " + reference.Evidence);
                return;
            }

            var name = reference.Target.Trim();
            var kind = queryMap.ContainsKey(name) ? "Query" : tableMap.ContainsKey(name) ? "Table" : fallbackKind;
            uses.Add(new AccessObjectUse(name, kind, reference.Kind, reference.Evidence, reference.Line, false));
            if (queryMap.ContainsKey(name))
            {
                AddOrigin(queryFrom, name, "VBA L" + reference.Line + " " + reference.Kind);
            }
            else if (tableMap.ContainsKey(name))
            {
                AddOrigin(tableFrom, name, "VBA L" + reference.Line + " " + reference.Kind);
            }
            else if (!known(name))
            {
                pending.Add("L" + reference.Line + " '" + name + "' no está en tablas ni QueryDefs.");
            }
        }

        void AddSqlText(string? sql, string role, string source, int? line, bool dynamic)
        {
            if (dynamic || string.IsNullOrWhiteSpace(sql))
            {
                pending.Add(source + " SQL dinámico; no se ejecuta.");
                uses.Add(new AccessObjectUse(sql ?? "(dinámico)", "Sql", role, source, line, true));
                return;
            }

            uses.Add(new AccessObjectUse(sql, "Sql", role, source, line, false));
            foreach (var identifier in AccessSqlIdentifierExtractor.Extract(sql))
            {
                if (tableMap.ContainsKey(identifier))
                {
                    AddOrigin(tableFrom, identifier, role + " " + source);
                }
                else if (queryMap.ContainsKey(identifier))
                {
                    AddOrigin(queryFrom, identifier, role + " " + source);
                }
                else
                {
                    pending.Add(source + " identificador SQL '" + identifier + "' no resuelto.");
                }
            }
        }

        static AccessRecordSourceKind ToRecordKind(AccessRowSourceKind? kind)
        {
            return kind switch
            {
                AccessRowSourceKind.Table => AccessRecordSourceKind.Table,
                AccessRowSourceKind.SavedQuery => AccessRecordSourceKind.SavedQuery,
                AccessRowSourceKind.SqlText => AccessRecordSourceKind.SqlText,
                _ => AccessRecordSourceKind.Unknown
            };
        }

        static void AddOrigin(Dictionary<string, List<string>> map, string name, string origin)
        {
            if (!map.TryGetValue(name, out var list))
            {
                list = [];
                map[name] = list;
            }

            if (!list.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(origin);
            }
        }
    }
}
