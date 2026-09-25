using System.Text.RegularExpressions;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessRecepcionContractAnalyzer
{
    public static AccessRecepcionAnalysis Analyze(
        string source,
        IReadOnlyList<AccessProcedureFunctionDoc> procedures,
        IReadOnlyList<AccessQueryUse> queryDefs,
        IReadOnlyList<AccessTableReference> importarTables,
        IReadOnlyList<AccessOrigenMdbInventory> origenMdbs)
    {
        var operations = new List<AccessTableOperation>();
        var fields = new List<AccessVbaFieldMention>();
        var searches = new List<AccessSeekUse>();
        var origins = new List<AccessVbaDataUseExtractor.RecordsetOrigin>();
        foreach (var procedure in procedures)
        {
            var range = AccessVbaTextAnalyzer.ExtractRange(source, procedure.StartLine, procedure.EndLine);
            var extracted = AccessVbaDataUseExtractor.Extract(procedure.Name, range, procedure.StartLine);
            operations.AddRange(extracted.Operations);
            fields.AddRange(extracted.Fields);
            searches.AddRange(extracted.Searches);
            origins.AddRange(extracted.LastOrigins.Values);
        }

        var importarMap = importarTables.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var queryMap = queryDefs.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in origins)
        {
            if (!origin.Dynamic && IsName(origin.ObjectName))
            {
                names.Add(origin.ObjectName);
            }
        }

        foreach (var query in queryDefs)
        {
            names.Add(query.Name);
            foreach (var table in query.TablesInSql)
            {
                names.Add(table);
            }
        }

        foreach (var procedure in procedures)
        {
            foreach (var name in procedure.TablesRead.Concat(procedure.TablesWritten).Concat(procedure.Queries))
            {
                if (IsName(name))
                {
                    names.Add(name);
                }
            }
        }

        var tables = names
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Select(name => BuildTable(name, procedures, operations, fields, searches, origins, importarMap, queryMap, origenMdbs))
            .ToList();
        return new AccessRecepcionAnalysis(
            tables,
            AccessTpvFormatCatalog.Document(),
            Legacy(),
            Defects(),
            Decisions(),
            Missing(tables, origenMdbs));
    }

    private static AccessRecepcionTableContract BuildTable(
        string name,
        IReadOnlyList<AccessProcedureFunctionDoc> procedures,
        IReadOnlyList<AccessTableOperation> operations,
        IReadOnlyList<AccessVbaFieldMention> fields,
        IReadOnlyList<AccessSeekUse> searches,
        IReadOnlyList<AccessVbaDataUseExtractor.RecordsetOrigin> origins,
        IReadOnlyDictionary<string, AccessTableReference> importar,
        IReadOnlyDictionary<string, AccessQueryUse> queries,
        IReadOnlyList<AccessOrigenMdbInventory> origenMdbs)
    {
        var usedOrigins = origins
            .Where(item => !item.Dynamic && string.Equals(item.ObjectName, name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Database)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var kind = queries.ContainsKey(name) ? "Query" : "Table";
        if (queries.ContainsKey(name)
            && usedOrigins.Any(item => item.Contains("prv_final", StringComparison.OrdinalIgnoreCase)))
        {
            kind = "Query (IMPORTAR lineas) / Table (prv_final.LINEAS homónimo)";
        }
        importar.TryGetValue(name, out var importarTable);
        var originPath = usedOrigins.FirstOrDefault(item => item.Contains(".mdb", StringComparison.OrdinalIgnoreCase))
            ?? importarTable?.SourcePath;
        var originDatabase = ClassifyOrigin(usedOrigins, originPath, importarTable);
        var (verified, schemaMdb, uncertain, verifiedFields) = ResolveSchema(name, originDatabase, origenMdbs, importarTable);
        var mentioned = fields
            .Where(item => string.Equals(item.ObjectName, name, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => (item.FieldName ?? ("#" + item.Ordinal)) + "|" + item.Line + "|" + item.Role)
            .Select(group => group.First())
            .ToList();
        var tableOps = operations
            .Where(item => string.Equals(item.ObjectName, name, StringComparison.OrdinalIgnoreCase)
                || (item.ObjectName is null && mentions(item, name)))
            .ToList();
        var tableSearches = searches
            .Where(item => string.Equals(item.ObjectName, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var usedBy = origins
            .Where(item => string.Equals(item.ObjectName, name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Procedure)
            .Concat(mentioned.Select(item => item.Procedure))
            .Concat(tableOps.Select(item => item.Procedure))
            .Concat(InferUsedBy(name, procedures, queries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pending = new List<string>();
        if (!verified)
        {
            pending.Add("Esquema no leído: tabla vinculada, QueryDef o MDB externo no presente en ORIGENMDB.");
        }

        if (uncertain)
        {
            pending.Add("El MDB local de ORIGENMDB no demuestra ser la misma versión que la ruta UNC/local de runtime.");
        }

        if (mentioned.Any(item => item.Ordinal is not null && item.FieldName is null))
        {
            pending.Add("Hay Fields(n) con nombre de columna desconocido.");
        }

        if (importarTable?.IsUnc == true || (originPath?.StartsWith(@"\\", StringComparison.Ordinal) ?? false))
        {
            pending.Add("Origen UNC no abierto.");
        }

        if (usedOrigins.Count > 1)
        {
            pending.Add("Abierto desde más de una base: " + string.Join("; ", usedOrigins));
        }

        return new AccessRecepcionTableContract(
            name,
            kind,
            originDatabase,
            originPath,
            importarTable?.LinkKind ?? (kind == "Query" ? AccessLinkKind.Local : AccessLinkKind.Unknown),
            importarTable?.IsLinked ?? kind != "Query",
            importarTable?.IsUnc ?? originPath?.StartsWith(@"\\", StringComparison.Ordinal) == true,
            importarTable?.SourceTableName,
            verified,
            schemaMdb,
            uncertain,
            usedBy,
            tableOps.Take(40).ToList(),
            mentioned,
            verifiedFields,
            tableSearches,
            pending);
    }

    private static bool mentions(AccessTableOperation operation, string name)
    {
        return operation.Evidence.Contains("\"" + name + "\"", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(operation.Evidence, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase);
    }

    private static bool ContainsName(AccessProcedureFunctionDoc procedure, string name)
    {
        return procedure.TablesRead.Concat(procedure.TablesWritten).Concat(procedure.Queries)
            .Any(item => item.Equals(name, StringComparison.OrdinalIgnoreCase)
                || item.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> InferUsedBy(
        string name,
        IReadOnlyList<AccessProcedureFunctionDoc> procedures,
        IReadOnlyDictionary<string, AccessQueryUse> queries)
    {
        var used = procedures
            .Where(item => ContainsName(item, name))
            .Select(item => item.Name)
            .ToList();
        if (queries.TryGetValue(name, out var query))
        {
            used.AddRange(query.UsedFrom);
        }

        return used.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ClassifyOrigin(
        IReadOnlyList<string> databases,
        string? path,
        AccessTableReference? importar)
    {
        var blob = string.Join(" ", databases.Append(path ?? string.Empty));
        if (blob.Contains("monedas.mdb", StringComparison.OrdinalIgnoreCase))
        {
            return "monedas.mdb (ruta de runtime; copia ORIGENMDB incierta)";
        }

        if (blob.Contains("zetas.mdb", StringComparison.OrdinalIgnoreCase))
        {
            return "zetas.mdb (ruta de runtime; copia ORIGENMDB incierta)";
        }

        if (blob.Contains("prv_final", StringComparison.OrdinalIgnoreCase))
        {
            return "prv_final_{mes}_{ano}.mdb (creado/abierto en UNC pedidos; no está en ORIGENMDB)";
        }

        if (blob.Contains("venta_clientes3", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("transmisiones", StringComparison.OrdinalIgnoreCase))
        {
            return "transmisiones / venta_clientes3 (UNC pedidos; no está en ORIGENMDB)";
        }

        if (blob.Contains("temporal.mdb", StringComparison.OrdinalIgnoreCase))
        {
            return "C:\\programas\\estadisticas\\temporal.mdb (no está en ORIGENMDB)";
        }

        if (blob.Contains("consumos.mdb", StringComparison.OrdinalIgnoreCase))
        {
            return "consumos.mdb (UNC pedidos; no está en ORIGENMDB)";
        }

        if (databases.Any(item => item.Equals("CurrentDb", StringComparison.OrdinalIgnoreCase)))
        {
            return importar is null
                ? "IMPORTAR.mdb CurrentDb (objeto no catalogado o QueryDef)"
                : importar.IsLinked
                    ? "IMPORTAR.mdb TableDef vinculada → " + (importar.SourcePath ?? importar.LinkKind.ToString())
                    : "IMPORTAR.mdb tabla local";
        }

        return importar is null ? "origen no determinado" : "IMPORTAR.mdb TableDef";
    }

    private static (bool Verified, string? SchemaMdb, bool Uncertain, IReadOnlyList<AccessLocalFieldSchema> Fields) ResolveSchema(
        string name,
        string originDatabase,
        IReadOnlyList<AccessOrigenMdbInventory> origenMdbs,
        AccessTableReference? importar)
    {
        AccessLocalTableSchema? Match(string fileName)
        {
            var mdb = origenMdbs.FirstOrDefault(item =>
                item.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            return mdb?.LocalSchemas.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        if (importar is { IsLinked: false }
            && !originDatabase.Contains("zetas.mdb", StringComparison.OrdinalIgnoreCase)
            && !originDatabase.Contains("monedas.mdb", StringComparison.OrdinalIgnoreCase)
            && !originDatabase.Contains("prv_final", StringComparison.OrdinalIgnoreCase)
            && !originDatabase.Contains("transmisiones", StringComparison.OrdinalIgnoreCase)
            && !originDatabase.Contains("temporal.mdb", StringComparison.OrdinalIgnoreCase)
            && !originDatabase.Contains("consumos.mdb", StringComparison.OrdinalIgnoreCase))
        {
            var local = Match("IMPORTAR.mdb");
            return local is null
                ? (false, "IMPORTAR.mdb", false, [])
                : (true, "IMPORTAR.mdb", false, local.Fields);
        }

        if (originDatabase.StartsWith("IMPORTAR.mdb tabla local", StringComparison.Ordinal)
            || (importar is { IsLinked: false } && originDatabase.Contains("CurrentDb", StringComparison.OrdinalIgnoreCase)))
        {
            var local = Match("IMPORTAR.mdb");
            return local is null
                ? (false, "IMPORTAR.mdb", false, [])
                : (true, "IMPORTAR.mdb", false, local.Fields);
        }

        if (originDatabase.Contains("zetas.mdb", StringComparison.OrdinalIgnoreCase))
        {
            var local = Match("zetas.mdb");
            return local is null
                ? (false, "zetas.mdb", true, [])
                : (true, "ORIGENMDB\\zetas.mdb", true, local.Fields);
        }

        if (originDatabase.Contains("monedas.mdb", StringComparison.OrdinalIgnoreCase))
        {
            var local = Match("monedas.mdb");
            return local is null
                ? (false, "monedas.mdb", true, [])
                : (true, "ORIGENMDB\\monedas.mdb", true, local.Fields);
        }

        return (false, null, false, []);
    }

    private static bool IsName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name)
            && Regex.IsMatch(name.Trim(), @"^[A-Za-z_][A-Za-z0-9_]*$");
    }

    private static IReadOnlyList<string> Legacy() =>
    [
        "El ledger de procesado es zetas.fichero por nombre exacto de fichero conductor (07/02/06/00/04), no por hash ni por companions.",
        "OPERADOR elige zetas/monedas local c:\\programas\\... frente a \\\\supervisores\\c\\programas\\...",
        "Filtro de importación de tiquets: TIENDA<>162 y <>33 y <200; el 07 puede registrarse igual.",
        "Remapeo de CAJA repetido en COBRADO, ventas_seccion, CREDITOS, DECLARADO, pagos1, cobros1, GRABAR_MONEDAS y GRABAR_TARJETA_PROPIA.",
        "zetas.bloqueado omite la caja en cobrado/secciones/pagos/cobros/monedas; CREDITOS escribe igual y solo avisa.",
        "tiquets_sin_pago inserta SELECT * de QueryDefs hacia venta_clientes2 y consumos1 con Resume Next.",
        "Textos TPV se materializan en h_red (l_tiq/tiq/t_pag/a2/a3/MONEDAS.txt) y las QueryDefs leen TableDefs de texto vinculadas.",
        "mover_ficheros_antiguos archiva 07 ya registrados con fecha < Date-3 y borra otros patrones; no usa fichero para 03/log/etc.",
        "prv_finales_Click es consolidación mensual distinta del ledger de ficheros TPV."
    ];

    private static IReadOnlyList<string> Defects() =>
    [
        "Registro de 07 pese a errores parciales: tiquets_sin_pago Resume Next y COBRADO End Function no impiden AddNew. CREDITOS sin handler sí podría abortar antes del registro.",
        "Deduplicación solo por nombre: un fichero cambiado con el mismo nombre no se reimporta; uno fallido sin AddNew se reintenta.",
        "Tiendas 162, 33 y >=200 quedan registradas en fichero sin pasar por tiquets_sin_pago/COBRADO/ventas_seccion/CREDITOS.",
        "Remapeos de caja copiados en muchos procedimientos (tienda 92 duplicada en el If); riesgo de divergencia si se cambia uno solo.",
        "ventas_seccion L755/L218 (Form_ventas): venta_seccion = tabla.Fields(0) usa el recordset de lineas_tiquets, no el SUM de tabla3; el tramo L931 sí usa tabla3.Fields(0). El nombre de Fields(0) no está verificado.",
        "prv_finales_Click usa tabla3.Fields(0) sobre contador y tabla.Fields(0) sobre un SELECT max(cobrado) from encargo: ordinal 0, nombre desconocido.",
        "CREDITOS.aceptado se asigna False si la caja está cerrada y no se usa para saltar la escritura.",
        "GRABAR_MONEDAS GoTo 30 si bloqueado sale del While completo, no solo de la fila.",
        "PAGOS/cobros declaran On Error GoTo 20 Resume 30 sin activar el handler en el cuerpo.",
        "Declarado abre QueryDef/tabla 'arque' en zetas.mdb; la SQL leída es la de IMPORTAR.mdb."
    ];

    private static IReadOnlyList<string> Decisions() =>
    [
        "No reproducir en .NET el registro de fichero si fallan escrituras intermedias; decidir un criterio transaccional nuevo.",
        "No copiar la deduplicación solo por nombre si se requiere reproceso por contenido.",
        "Decidir qué hacer con tiendas 162/33/>=200: omitir del ledger, importar igual, o registrar sin importar (legado).",
        "Centralizar remapeo de caja en una tabla de configuración; no duplicar la cascata If.",
        "Fields(0): no asumir el nombre; comprobar el esquema o usar alias explícitos (SUM).",
        "Confirmar si ORIGENMDB\\zetas.mdb y monedas.mdb son la versión de runtime o copias distintas antes de usar su esquema como contrato.",
        "Confirmar si i_divisas / i_divisas_MONEDAS locales de IMPORTAR se usan o si el runtime es siempre monedas.mdb.",
        "No abrir UNC ni ejecutar cdados.EXE en la reconstrucción hasta tener muestras TPV y política de archivos."
    ];

    private static IReadOnlyList<string> Missing(
        IReadOnlyList<AccessRecepcionTableContract> tables,
        IReadOnlyList<AccessOrigenMdbInventory> origen)
    {
        return
        [
            "Ficheros de ejemplo 00-07 (ninguno en ORIGENMDB; solo IMPORTAR.mdb, zetas.mdb, monedas.mdb).",
            "MDB de runtime no presentes: transmisiones.mdb, venta_clientes3_*.mdb, prv_final_*.mdb, consumos.mdb, temporal.mdb, pagos.mdb.",
            "Esquema de TableDefs de texto (TIQ, t_pag, t_lin, a2, A3, paguitos1/2, moneditas0): no se leyeron Fields por ser vínculos Text.",
            "SQL de QueryDef arque en zetas.mdb (solo se leyó la de IMPORTAR).",
            "Layout interior pipe-delimited de cada tipo TPV.",
            "Contrato de cdados.EXE para IPZ.",
            tables.Count(item => !item.SchemaVerified) + " objetos sin esquema local verificado.",
            "Copia ORIGENMDB zetas/monedas: integridad leída, correspondencia con \\\\supervisores o c:\\programas no demostrada."
        ];
    }
}

public sealed record AccessRecepcionAnalysis(
    IReadOnlyList<AccessRecepcionTableContract> Tables,
    IReadOnlyList<AccessTpvFormatDoc> TpvFormats,
    IReadOnlyList<string> LegacyBehavior,
    IReadOnlyList<string> PossibleDefects,
    IReadOnlyList<string> PendingDecisions,
    IReadOnlyList<string> MissingForReconstruction);
