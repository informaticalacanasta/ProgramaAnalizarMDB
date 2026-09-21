using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessCallGraph
{
    public static (IReadOnlyList<AccessCallEdge> Edges, IReadOnlyList<string> Order, IReadOnlyList<string> Pending) Walk(
        string? source,
        string entry,
        IReadOnlyList<string> extraRoots)
    {
        var procedures = AccessVbaTextAnalyzer.ParseProcedures(source);
        var byName = procedures
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var references = AccessVbaTextAnalyzer.ParseReferences(source, procedures);
        var edges = new List<AccessCallEdge>();
        var order = new List<string>();
        var pending = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string name)
        {
            if (!byName.TryGetValue(name, out var procedure))
            {
                pending.Add("Procedimiento '" + name + "' no está en este módulo.");
                return;
            }

            if (visiting.Contains(name))
            {
                edges.Add(new AccessCallEdge(name, name, procedure.StartLine, Cycle: true));
                pending.Add("Ciclo detectado en '" + name + "'.");
                return;
            }

            if (!visited.Add(name))
            {
                return;
            }

            visiting.Add(name);
            order.Add(procedure.Name);
            var calls = AccessVbaTextAnalyzer.InRange(references, procedure.StartLine, procedure.EndLine)
                .Where(item => item.Kind is "Call" or "SameModule" && !string.IsNullOrWhiteSpace(item.Target))
                .ToList();
            foreach (var call in calls)
            {
                var target = call.Target!;
                var cycle = visiting.Contains(target);
                edges.Add(new AccessCallEdge(procedure.Name, target, call.Line, cycle));
                if (cycle)
                {
                    pending.Add("Ciclo " + procedure.Name + " → " + target + " L" + call.Line + ".");
                    continue;
                }

                if (byName.ContainsKey(target))
                {
                    Visit(target);
                }
                else
                {
                    pending.Add("L" + call.Line + " " + procedure.Name + " llama a '" + target + "' fuera de Form_ventas; no seguido.");
                }
            }

            visiting.Remove(name);
        }

        Visit(entry);
        foreach (var root in extraRoots)
        {
            Visit(root);
        }

        var distinctEdges = edges
            .GroupBy(item => item.From + "|" + item.To + "|" + item.Line + "|" + item.Cycle, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        return (distinctEdges, order, pending.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}

public static class AccessQueryNesting
{
    public static IReadOnlyList<AccessQueryUse> Expand(
        IReadOnlyList<string> roots,
        IReadOnlyList<AccessQueryAnalysis> allQueries,
        IReadOnlyList<AccessTableReference> allTables)
    {
        var queryMap = allQueries.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var tableMap = allTables.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var known = queryMap.Keys.Concat(tableMap.Keys).ToList();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collected = new Dictionary<string, AccessQueryUse>(StringComparer.OrdinalIgnoreCase);

        void Visit(string name, string origin)
        {
            if (!queryMap.TryGetValue(name, out var query))
            {
                return;
            }

            if (visiting.Contains(name))
            {
                return;
            }

            if (collected.ContainsKey(name))
            {
                var existing = collected[name];
                if (!existing.UsedFrom.Contains(origin, StringComparer.OrdinalIgnoreCase))
                {
                    collected[name] = existing with { UsedFrom = existing.UsedFrom.Append(origin).ToList() };
                }

                return;
            }

            visiting.Add(name);
            var identifiers = AccessSqlIdentifierExtractor.Extract(query.Sql)
                .Concat(AccessSqlIdentifierExtractor.FindKnown(
                    AccessSqlIdentifierExtractor.IdentifierRegions(query.Sql),
                    known))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var tablesInSql = identifiers.Where(tableMap.ContainsKey).ToList();
            var nested = identifiers
                .Where(item => queryMap.ContainsKey(item) && !item.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var unknown = identifiers
                .Where(item => !tableMap.ContainsKey(item) && !queryMap.ContainsKey(item))
                .ToList();
            collected[name] = new AccessQueryUse(
                query.Name,
                query.Sql,
                query.Type,
                AccessQueryTypeNames.Name(query.Type),
                query.ReturnsRecords,
                string.IsNullOrWhiteSpace(query.Connect) ? null : "(presente; no se abre)",
                query.Parameters,
                [origin],
                tablesInSql,
                nested,
                unknown,
                query.Warnings);
            foreach (var child in nested)
            {
                Visit(child, "QueryDef " + name);
            }

            visiting.Remove(name);
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Visit(root, "Form_ventas");
        }

        return collected.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
