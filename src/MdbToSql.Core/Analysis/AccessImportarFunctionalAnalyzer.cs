using System.Text.RegularExpressions;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessImportarFunctionalAnalyzer
{
    public const string Entry = "Importar_Click";

    public static readonly string[] RequestedProcedures =
    [
        "tiquets_sin_pago",
        "COBRADO",
        "ventas_seccion",
        "CREDITOS",
        "ARQUEO",
        "PAGOS",
        "cobros",
        "MONEDAS",
        "mover_ficheros_antiguos",
        "prv_finales_Click"
    ];

    private static readonly Regex QuotedSql = new(
        @"""((?:INSERT|SELECT|UPDATE|DELETE|CREATE|DROP|UNION)\s[^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SqlAssign = new(
        @"\b(sql_buff|sql_buf2|sql_buf3|sql_buff100)\s*=\s*""([^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ConcatAssign = new(
        @"\b(sql_buff|sql_buf2|sql_buf3)\s*=\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TiendaIf = new(
        @"\bIf\b.*\bTIENDA\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static AccessImportarAnalysis Analyze(
        string source,
        IReadOnlyList<AccessQueryAnalysis> queryCatalog,
        IReadOnlyList<AccessTableReference> tableCatalog)
    {
        var procedures = AccessVbaTextAnalyzer.ParseProcedures(source);
        var references = AccessVbaTextAnalyzer.ParseReferences(source, procedures);
        var (edges, order, graphPending) = AccessCallGraph.Walk(source, Entry, RequestedProcedures);
        var queryRoots = CollectQueryRoots(source, procedures, references, order);
        var queryUses = AccessQueryNesting.Expand(queryRoots, queryCatalog, tableCatalog);
        var docs = new List<AccessProcedureFunctionDoc>();
        var pending = new List<string>(graphPending);

        foreach (var name in order.Concat(RequestedProcedures).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var procedure = procedures.FirstOrDefault(
                item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (procedure is null)
            {
                pending.Add("No está en Form_ventas: " + name);
                continue;
            }

            docs.Add(Document(source, procedure, references, queryCatalog, tableCatalog, pending));
        }

        var tableUses = CollectTables(docs, queryUses, tableCatalog);
        pending.AddRange(CollectPending(docs, queryUses, tableCatalog, queryCatalog));
        return new AccessImportarAnalysis(
            Entry,
            RequestedProcedures,
            edges,
            order,
            docs,
            queryUses,
            tableUses,
            FileProcessing(),
            ProcessFlow(),
            pending.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static AccessProcedureFunctionDoc Document(
        string source,
        AccessVbaProcedure procedure,
        IReadOnlyList<AccessVbaReference> references,
        IReadOnlyList<AccessQueryAnalysis> queryCatalog,
        IReadOnlyList<AccessTableReference> tableCatalog,
        List<string> pending)
    {
        var overlay = AccessImportarProcedureCatalog.For(procedure.Name);
        var facts = AccessVbaTextAnalyzer.ParseFacts(source, procedure.StartLine, procedure.EndLine);
        var localRefs = AccessVbaTextAnalyzer.InRange(references, procedure.StartLine, procedure.EndLine);
        var text = AccessVbaTextAnalyzer.ExtractRange(source, procedure.StartLine, procedure.EndLine);
        var sqls = ExtractSql(text, procedure.StartLine);
        foreach (var sql in sqls.Where(item => item.Dynamic))
        {
            pending.Add(procedure.Name + " L" + sql.Line + " SQL dinámico: " + sql.Text);
        }

        var queryNames = localRefs
            .Where(item => item.Kind is "QueryDefs" or "OpenRecordset" && !item.Dynamic && item.Target is not null)
            .Select(item => item.Target!)
            .Concat(overlay?.Queries ?? [])
            .Concat(sqls.SelectMany(item => AccessSqlIdentifierExtractor.Extract(item.Text)))
            .Where(name => queryCatalog.Any(query => string.Equals(query.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tableNames = sqls
            .SelectMany(item => AccessSqlIdentifierExtractor.Extract(item.Text)
                .Concat(AccessSqlIdentifierExtractor.FindKnown(
                    item.Text,
                    tableCatalog.Select(table => table.Name))))
            .Concat(localRefs.Where(item => item.Kind == "OpenRecordset" && item.Target is not null).Select(item => item.Target!))
            .Where(name => tableCatalog.Any(table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var calls = localRefs
            .Where(item => item.Kind is "Call" or "SameModule" && item.Target is not null)
            .Select(item => item.Target!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = localRefs
            .Where(item => item.Kind is "ExternalPath" or "FileCopy" or "FileDelete" or "FileMove" or "Dir" or "Shell" or "FileIo" or "MkDir")
            .Select(item => "L" + item.Line + " " + item.Kind + ": " + (item.Target ?? item.Evidence))
            .Concat(overlay?.Files ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var conditions = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select((line, index) => (line, number: procedure.StartLine + index))
            .Where(item => TiendaIf.IsMatch(AccessVbaTextAnalyzerStrip(item.line))
                || item.line.Contains("bloqueado", StringComparison.OrdinalIgnoreCase)
                || item.line.Contains("OPERADOR", StringComparison.OrdinalIgnoreCase))
            .Select(item => "L" + item.number + " " + item.line.Trim())
            .Concat(overlay?.Conditions ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var evidence = facts
            .Concat(localRefs.Where(item => item.Kind is "Call" or "SameModule" or "QueryDefs" or "OpenRecordset"
                or "CurrentDb.Execute" or "ExternalPath" or "FileCopy" or "FileDelete" or "FileMove"
                or "Dir" or "Shell" or "FileIo" or "RecordAdd" or "RecordSeek" or "ErrorHandler"))
            .GroupBy(item => item.Line + "|" + item.Kind + "|" + item.Evidence)
            .Select(group => group.First())
            .OrderBy(item => item.Line)
            .Take(40)
            .ToList();
        var transformations = (overlay?.Transformations ?? [])
            .Concat(sqls.Select(item => (item.Dynamic ? "SQL dinámico L" : "SQL L") + item.Line + ": " + Truncate(item.Text, 240)))
            .ToList();
        return new AccessProcedureFunctionDoc(
            procedure.Name,
            procedure.Kind,
            AccessVbaTextAnalyzer.Signature(source, procedure),
            procedure.StartLine,
            procedure.EndLine,
            overlay?.Purpose ?? ("Procedimiento Form_ventas " + procedure.Name + " alcanzado desde Importar_Click."),
            Merge(overlay?.Inputs, InferInputs(text, localRefs)),
            conditions,
            Merge(overlay?.TablesRead, tableNames),
            overlay?.TablesWritten ?? [],
            Merge(overlay?.Queries, queryNames),
            transformations,
            files,
            Merge(overlay?.ErrorHandling, facts.Where(item => item.Kind == "ErrorHandler").Select(item => "L" + item.Line + " " + item.Evidence)),
            calls,
            evidence);
    }

    private static IReadOnlyList<string> CollectQueryRoots(
        string source,
        IReadOnlyList<AccessVbaProcedure> procedures,
        IReadOnlyList<AccessVbaReference> references,
        IReadOnlyList<string> order)
    {
        var names = new List<string>();
        foreach (var name in order)
        {
            var procedure = procedures.First(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            foreach (var reference in AccessVbaTextAnalyzer.InRange(references, procedure.StartLine, procedure.EndLine))
            {
                if (reference.Kind is "QueryDefs" or "OpenRecordset" && !reference.Dynamic && reference.Target is not null)
                {
                    names.Add(reference.Target);
                }
            }

            foreach (var sql in ExtractSql(AccessVbaTextAnalyzer.ExtractRange(source, procedure.StartLine, procedure.EndLine), procedure.StartLine))
            {
                names.AddRange(AccessSqlIdentifierExtractor.Extract(sql.Text));
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<AccessTableUse> CollectTables(
        IReadOnlyList<AccessProcedureFunctionDoc> docs,
        IReadOnlyList<AccessQueryUse> queries,
        IReadOnlyList<AccessTableReference> catalog)
    {
        var origins = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string origin)
        {
            if (!origins.TryGetValue(name, out var list))
            {
                list = [];
                origins[name] = list;
            }

            if (!list.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(origin);
            }
        }

        foreach (var doc in docs)
        {
            foreach (var table in doc.TablesRead.Concat(doc.TablesWritten))
            {
                Add(table, doc.Name);
            }
        }

        foreach (var query in queries)
        {
            foreach (var table in query.TablesInSql)
            {
                Add(table, "QueryDef " + query.Name);
            }
        }

        return origins
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var table = catalog.FirstOrDefault(candidate => string.Equals(candidate.Name, item.Key, StringComparison.OrdinalIgnoreCase));
                return new AccessTableUse(
                    item.Key,
                    table?.IsLinked ?? false,
                    table?.LinkKind ?? AccessLinkKind.Unknown,
                    table?.IsUnc ?? false,
                    table?.SourceTableName,
                    item.Value);
            })
            .ToList();
    }

    private static IReadOnlyList<string> CollectPending(
        IReadOnlyList<AccessProcedureFunctionDoc> docs,
        IReadOnlyList<AccessQueryUse> queries,
        IReadOnlyList<AccessTableReference> tables,
        IReadOnlyList<AccessQueryAnalysis> catalog)
    {
        var pending = new List<string>
        {
            "Form_Open no es llamado por Importar_Click; d_red/h_red/dd_red/OPERADOR se asumen inicializados.",
            "terminal (módulo inicio) no se sigue aquí.",
            "No se ejecutó VBA, SQL, Dir, FileCopy, Shell ni se abrieron UNC/MDB vinculados.",
            "DECLARADO abre Recordset 'arque' sobre zetas.mdb: la QueryDef leída es la de IMPORTAR.mdb; no se comprobó si zetas.mdb define otra."
        };
        foreach (var query in queries)
        {
            foreach (var unknown in query.UnknownIdentifiers)
            {
                pending.Add("QueryDef '" + query.Name + "' identificador no resuelto: " + unknown);
            }
        }

        foreach (var doc in docs)
        {
            foreach (var table in doc.TablesRead.Concat(doc.TablesWritten))
            {
                if (!Regex.IsMatch(table.Trim(), @"^[A-Za-z_][A-Za-z0-9_]*$"))
                {
                    continue;
                }

                if (!tables.Any(item => string.Equals(item.Name, table, StringComparison.OrdinalIgnoreCase))
                    && !catalog.Any(item => string.Equals(item.Name, table, StringComparison.OrdinalIgnoreCase)))
                {
                    pending.Add(doc.Name + ": objeto '" + table + "' no está en TableDefs/QueryDefs de IMPORTAR.mdb (MDB externo o texto).");
                }
            }
        }

        return pending;
    }

    private static AccessFileProcessingDoc FileProcessing() => new(
        "El nombre del fichero 20*_07_*.txt se registra con tabla.AddNew / tabla!fichero=nombre / Update al final de cada iteración del For, después de copiar companions, prefijar TIENDA y (si pasa el filtro) tiquets_sin_pago, COBRADO, ventas_seccion y CREDITOS. Evidencia Importar_Click L401-403. ARQUEO/PAGOS/cobros/Importar_MONEDAS registran sus propios patrones (02, 06, 00, 04) igual, después de su Call interno.",
        [
            "Dir d_red 20*_07_*.txt y filtro Seek en fichero (los ya registrados no se encolan)",
            "Transformación a0.txt (recorte |2018| o FileCopy)",
            "FileCopy companions 05→a1 y 01→a2 (con fallback de fecha)",
            "Reescritura l_tiq.txt / tiq.txt / t_pag.txt con prefijo TIENDA",
            "Si TIENDA <>162 y <>33 y <200: tiquets_sin_pago, COBRADO, ventas_seccion, CREDITOS (escrituras a venta_clientes2, consumos1, zetas, v_seccion, creditos, prv_final LINEAS, monedas tarjeta)"
        ],
        [
            "Siguiente fichero 07 del array",
            "Tras el For: ARQUEO, PAGOS, cobros, MONEDAS (cada uno con su fichero/AddNew)",
            "Close BD zetas",
            "mover_ficheros_antiguos (archiva/borra; no AddNew fichero)",
            "prv_finales_Click (consolida venta_clientes3; no usa fichero)"
        ],
        [
            "Fallo en Dir/FileCopy de companions 05/01: On Error GoTo 20 Resume 40 → no AddNew; el 07 se reintentará",
            "Tras On Error GoTo 0, un error no capturado en el prefijo de archivos o en las Calls abortaría el Sub; 40 no se usa",
            "tiquets_sin_pago usa Resume Next: INSERT fallido no impide AddNew",
            "COBRADO On Error GoTo 20 End Function: deja de acumular zetas pero Importar_Click sigue y registra el fichero",
            "ventas_seccion Resume sigueerror tras MsgBox: puede escribir el ajuste de sección 4 a medias y aun así se registra el 07",
            "CREDITOS no tiene handler: excepción impediría AddNew (el fichero no quedaría marcado)",
            "ARQUEO/PAGOS/cobros/MONEDAS: error de companion → Resume 30 sin AddNew de ese patrón; el 07 ya pudo haberse registrado antes",
            "zetas.bloqueado omite esa caja pero no impide registrar el fichero"
        ],
        [
            "Seek exacto del nombre de archivo en zetas.fichero (índice fichero) al listar 07, 02, 06, 00 y 04",
            "mover_ficheros_antiguos Name/Kill de 07 ya registrados con fecha < Date-3: el archivo deja de estar en d_red para un Dir futuro"
        ],
        [
            "El contenido no se hashea: el mismo nombre no se reimporta aunque el fichero cambie",
            "Si AddNew no llegó a ejecutarse (error 20/40 o aborto), el nombre no está en fichero y se volverá a listar",
            "Companions 05/01/03 no se registran; solo el fichero conductor (07/02/06/00/04)",
            "Tiendas 162, 33 y >=200: el 07 sí se registra aunque no se llamen tiquets_sin_pago/COBRADO/ventas_seccion/CREDITOS",
            "mover no borra 07 no registrados; un 07 viejo sin fila en fichero se reintentaría cada día hasta Date-3+proceso o registro",
            "Tope 1000 en Importar_MONEDAS aborta el resto de 04 de esa ejecución; los no vistos no se marcan",
            "MONEDAS Kill de INBOX ocurre antes de Importar_MONEDAS: si GRABAR_MONEDAS falla tras AddNew, el origen INBOX ya no está"
        ]);

    private static IReadOnlyList<string> ProcessFlow() =>
    [
        "Prerrequisito Form_Open: terminal hasta OPERADOR<>\"\"; dd_red/d_red/h_red UNC; FORMATES recargado desde dossis.",
        "Importar_Click abre zetas.mdb (local si OPERADOR=supervisores) y fichero índice fichero.",
        "Lista d_red 20*_07_*.txt cuyo nombre no está en fichero (máx. 1001 slots sql_buff100).",
        "Por cada 07: a0.txt (recorte |2018| si Date>=30-05-2018); companions 05 y 01 → a1/a2; l_tiq/tiq/t_pag con prefijo TIENDA.",
        "Si TIENDA no es 162 ni 33 y es <200: tiquets_sin_pago (INSERT venta_clientes2/consumos1) → COBRADO (zetas.VENTA) → ventas_seccion (v_seccion, prv_final.LINEAS, tarjeta propia) → CREDITOS (creditos y zetas.CREDITOS).",
        "Registro: AddNew fichero=nombre 07.",
        "ARQUEO 20*_02_*.txt + 03 → DECLARADO (zetas.DECLARADO/contado) → AddNew fichero 02.",
        "PAGOS 20*_06_*.txt → pagos1 (zetas.PAGOS y pagos) → AddNew 06.",
        "cobros 20*_00_*.txt → cobros1 (PAGOS negados) → AddNew 00.",
        "MONEDAS: INBOX *_04_*.* a h_red (IPZ vía cdados.EXE) → Importar_MONEDAS 20*_04_*.txt + 03 → GRABAR_MONEDAS (monedas.mdb) → AddNew 04.",
        "Cierra zetas.mdb. mover_ficheros_antiguos archiva 07 procesados < Date-3 y borra otros patrones; copia MDB a sav.",
        "prv_finales_Click recorre meses Date-9..Date-5, lee venta_clientes3_mes_ano.mdb, escribe prv_final_mes_ano.mdb y pedidos; crea_fic/filtros_1."
    ];

    private static IReadOnlyList<SqlSpan> ExtractSql(string text, int startLine)
    {
        var spans = new List<SqlSpan>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? lastAssign = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = AccessVbaTextAnalyzerStrip(lines[index]);
            var number = startLine + index;
            foreach (Match match in QuotedSql.Matches(line))
            {
                spans.Add(new SqlSpan(number, match.Groups[1].Value, Dynamic: false));
            }

            var assign = SqlAssign.Match(line);
            if (assign.Success)
            {
                lastAssign = assign.Groups[2].Value;
                if (LooksLikeSql(lastAssign))
                {
                    var dynamic = line.Contains('&');
                    spans.Add(new SqlSpan(number, Reconstruct(line, lastAssign), dynamic));
                }
            }
            else
            {
                var concat = ConcatAssign.Match(line);
                if (concat.Success && concat.Groups[2].Value.Contains('&') && lastAssign is not null)
                {
                    spans.Add(new SqlSpan(number, Reconstruct(line, concat.Groups[2].Value), Dynamic: true));
                }
            }
        }

        return spans;
    }

    private static string Reconstruct(string line, string sql)
    {
        if (!line.Contains('&'))
        {
            return sql;
        }

        return Regex.Replace(line, @"\s*&", " &").Trim();
    }

    private static bool LooksLikeSql(string value)
    {
        return Regex.IsMatch(
            value.TrimStart(),
            @"^(INSERT|SELECT|UPDATE|DELETE|CREATE|DROP|UNION)\b",
            RegexOptions.IgnoreCase);
    }

    private static IReadOnlyList<string> InferInputs(string text, IReadOnlyList<AccessVbaReference> references)
    {
        var inputs = new List<string>();
        if (text.Contains("OPERADOR", StringComparison.OrdinalIgnoreCase))
        {
            inputs.Add("OPERADOR");
        }

        foreach (var name in new[] { "d_red", "h_red", "dd_red", "ddd_red", "TIENDA", "CAJA" })
        {
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                inputs.Add(name);
            }
        }

        inputs.AddRange(references
            .Where(item => item.Kind == "OpenDatabase" || item.Kind == "Dao")
            .Select(item => item.Evidence));
        return inputs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> Merge(IReadOnlyList<string>? overlay, IEnumerable<string> extra)
    {
        return (overlay ?? [])
            .Concat(extra)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Truncate(string value, int max)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= max ? compact : compact[..max] + "...";
    }

    private static string AccessVbaTextAnalyzerStrip(string line)
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

    private sealed record SqlSpan(int Line, string Text, bool Dynamic);
}

public sealed record AccessImportarAnalysis(
    string EntryProcedure,
    IReadOnlyList<string> RequestedProcedures,
    IReadOnlyList<AccessCallEdge> CallGraph,
    IReadOnlyList<string> WalkOrder,
    IReadOnlyList<AccessProcedureFunctionDoc> Procedures,
    IReadOnlyList<AccessQueryUse> QueryDefs,
    IReadOnlyList<AccessTableUse> Tables,
    AccessFileProcessingDoc FileProcessing,
    IReadOnlyList<string> ProcessFlow,
    IReadOnlyList<string> Pending);
