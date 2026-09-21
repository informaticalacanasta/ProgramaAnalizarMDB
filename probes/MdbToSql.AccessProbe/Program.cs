using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MdbToSql.AccessProbe;

internal static class ProbeConstants
{
    public const string AccessProgId = "Access.Application.11";
    public const string DaoProgId = "DAO.DBEngine.36";
    public const int MsoAutomationSecurityLow = 1;
    public const int MsoAutomationSecurityByUI = 2;
    public const int MsoAutomationSecurityForceDisable = 3;
    public const int AcForm = 2;
    public const int AcReport = 3;
    public const int AcModule = 5;
    public const int AcSaveNo = 2;
    public const int AcSaveYes = 1;
    public const int AcQuitSaveNone = 2;
    public const int AcDesign = 1;
    public const int AcHidden = 1;
    public const int AcFileFormatAccess2000 = 9;
    public const int AcFileFormatAccess2002 = 10;
    public const int DbBoolean = 1;
    public const int DbText = 10;
    public const int DbAttachedTable = 1073741824;
    public const int DbAttachedOdbc = 536870912;
}

internal sealed class LinkedTableInfo
{
    public string Name { get; set; } = string.Empty;
    public string? SourceTableName { get; set; }
    public string? Connect { get; set; }
    public string Kind { get; set; } = "Unknown";
    public bool IsMdb { get; set; }
    public bool IsUnc { get; set; }
    public bool IsOdbc { get; set; }
    public bool IsInterBase { get; set; }
    public bool IsText { get; set; }
    public bool IsLocalPath { get; set; }
}

internal sealed class ProbeReport
{
    public string MdbPath { get; set; } = string.Empty;
    public string? SourceCopyPath { get; set; }
    public string? ConvertedPath { get; set; }
    public string AccessProgId { get; set; } = ProbeConstants.AccessProgId;
    public string? AccessVersion { get; set; }
    public string? AccessProcessPath { get; set; }
    public string? JetVersion { get; set; }
    public string? ConvertedJetVersion { get; set; }
    public string? DestinationFormatName { get; set; }
    public int? DestinationFormatValue { get; set; }
    public bool ConvertSucceeded { get; set; }
    public string? ConvertMessage { get; set; }
    public long OriginalSizeBefore { get; set; }
    public long OriginalSizeAfter { get; set; }
    public long SourceCopySize { get; set; }
    public long ConvertedSize { get; set; }
    public bool OpenedConverted { get; set; }
    public string? OpenMessage { get; set; }
    public bool AccessHidden { get; set; }
    public bool DialogDetected { get; set; }
    public string? DialogTitle { get; set; }
    public int LocalTables { get; set; }
    public int LinkedTables { get; set; }
    public int QueryDefs { get; set; }
    public int Relations { get; set; }
    public int Forms { get; set; }
    public int Reports { get; set; }
    public int Macros { get; set; }
    public int Modules { get; set; }
    public int? CurrentProjectForms { get; set; }
    public int? CurrentProjectReports { get; set; }
    public int? CurrentProjectMacros { get; set; }
    public int? CurrentProjectModules { get; set; }
    public int? CurrentDataTables { get; set; }
    public int? CurrentDataQueries { get; set; }
    public string? CurrentProjectFileFormat { get; set; }
    public bool VbaAccessible { get; set; }
    public string? VbaMessage { get; set; }
    public bool SaveAsTextForm { get; set; }
    public bool SaveAsTextReport { get; set; }
    public bool SaveAsTextModule { get; set; }
    public string? SaveAsTextMessage { get; set; }
    public string HashBefore { get; set; } = string.Empty;
    public string HashAfter { get; set; } = string.Empty;
    public DateTime LastWriteBeforeUtc { get; set; }
    public DateTime LastWriteAfterUtc { get; set; }
    public int PreExistingAccessProcesses { get; set; }
    public int? StartedAccessPid { get; set; }
    public bool OrphanAccessProcess { get; set; }
    public bool TempDeleted { get; set; }
    public List<LinkedTableInfo> LinkedTablesList { get; } = [];
    public List<string> SampleQueries { get; } = [];
    public List<string> FormNames { get; } = [];
    public List<string> ReportNames { get; } = [];
    public List<string> ModuleNames { get; } = [];
    public List<string> FormProbeLines { get; } = [];
    public List<string> ReportProbeLines { get; } = [];
    public List<string> VbaLines { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];
    public bool HashUnchanged =>
        string.Equals(HashBefore, HashAfter, StringComparison.OrdinalIgnoreCase);
    public bool LastWriteUnchanged => LastWriteBeforeUtc == LastWriteAfterUtc;
    public bool SizeUnchanged => OriginalSizeBefore == OriginalSizeAfter;
    public bool OriginalImmutable => HashUnchanged && LastWriteUnchanged && SizeUnchanged;
    public bool Passed => OriginalImmutable && !OrphanAccessProcess && Errors.Count == 0;
}

internal static class Program
{
    private static readonly string DefaultMdb = Path.Combine(
        @"C:\Users\Usuario\Desktop\LaCanasta\ORIGENMDB",
        "IMPORTAR.mdb");

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length > 0
                && string.Equals(args[0], "--ventas-functional", StringComparison.OrdinalIgnoreCase))
            {
                var mdb = args.Length > 1 ? args[1] : DefaultMdb;
                return PhaseVentasFunctional.Run(mdb);
            }

            if (args.Length > 0
                && string.Equals(args[0], "--ventas", StringComparison.OrdinalIgnoreCase))
            {
                var mdb = args.Length > 1 ? args[1] : DefaultMdb;
                return PhaseVentas.Run(mdb);
            }

            if (args.Length > 0
                && string.Equals(args[0], "--terminal-vba", StringComparison.OrdinalIgnoreCase))
            {
                var mdb = args.Length > 1 ? args[1] : DefaultMdb;
                return PhaseTerminalVba.Run(mdb);
            }

            if (args.Length > 0
                && string.Equals(args[0], "--menu-vba", StringComparison.OrdinalIgnoreCase))
            {
                var mdb = args.Length > 1 ? args[1] : DefaultMdb;
                return PhaseMenuVba.Run(mdb);
            }

            if (args.Length > 0
                && string.Equals(args[0], "--phase4a", StringComparison.OrdinalIgnoreCase))
            {
                var mdb = args.Length > 1 ? args[1] : DefaultMdb;
                return Phase4A.Run(mdb);
            }

            if (args.Length > 0
                && string.Equals(args[0], "--phase3", StringComparison.OrdinalIgnoreCase))
            {
                var mdb = args.Length > 1 ? args[1] : DefaultMdb;
                return Phase3.Run(mdb);
            }

            if (args.Length > 0
                && string.Equals(args[0], "--phase2", StringComparison.OrdinalIgnoreCase))
            {
                var mdb = args.Length > 1 ? args[1] : DefaultMdb;
                return Phase2.Run(mdb);
            }

            if (args.Length > 0
                && string.Equals(args[0], "--phase1c", StringComparison.OrdinalIgnoreCase))
            {
                return Phase1C.Run();
            }

            if (args.Length == 0
                || string.Equals(args[0], "--phase1b", StringComparison.OrdinalIgnoreCase))
            {
                return Phase1B.Run();
            }

            var mdbPath = string.Equals(args[0], "--convert", StringComparison.OrdinalIgnoreCase)
                ? args.Length > 1 ? args[1] : DefaultMdb
                : args[0];
        var report = new ProbeReport { MdbPath = mdbPath };
        var runId = Guid.NewGuid().ToString("N");
        var tempDir = Path.Combine(Path.GetTempPath(), "MdbToSql", runId);

        try
        {
            RunProbe(report, tempDir);
        }
        catch (Exception exception)
        {
            report.Errors.Add(exception.Message);
            Console.Error.WriteLine(exception);
        }
        finally
        {
            report.TempDeleted = TryDeleteDirectory(tempDir, report);
        }

        PrintReport(report);
        return report.Passed ? 0 : 2;
    }

    private static void RunProbe(ProbeReport report, string tempDir)
    {
        if (!File.Exists(report.MdbPath))
        {
            throw new FileNotFoundException($"No existe el MDB '{report.MdbPath}'.");
        }

        if (IntPtr.Size != 4)
        {
            throw new InvalidOperationException(
                $"El probe debe ejecutarse como x86. IntPtr.Size={IntPtr.Size}.");
        }

        EnsureSafeLocalMdbPath(report.MdbPath, "MDB de origen");
        var originalInfo = new FileInfo(report.MdbPath);
        report.OriginalSizeBefore = originalInfo.Length;
        report.LastWriteBeforeUtc = originalInfo.LastWriteTimeUtc;
        report.HashBefore = ComputeSha256(report.MdbPath);

        var beforePids = CurrentAccessPids();
        report.PreExistingAccessProcesses = beforePids.Count;

        Directory.CreateDirectory(tempDir);
        var sourceCopy = Path.Combine(tempDir, "source-jet3.mdb");
        var convertedPath = Path.Combine(tempDir, "converted.mdb");
        var exportDir = Path.Combine(tempDir, "Export");
        Directory.CreateDirectory(exportDir);

        File.Copy(report.MdbPath, sourceCopy, overwrite: true);
        PrepareLocalWorkingCopy(sourceCopy);
        EnsureSafeLocalMdbPath(sourceCopy, "copia Jet 3 temporal");
        EnsureSafeLocalMdbPath(convertedPath, "MDB convertido temporal");
        report.SourceCopyPath = sourceCopy;
        report.ConvertedPath = convertedPath;
        report.SourceCopySize = new FileInfo(sourceCopy).Length;
        report.Warnings.Add(
            "Fase 1 ampliada: ConvertAccessProject sobre copia en %TEMP%. " +
            "No se usa CompactDatabase. No se abre ni convierte el MDB original. " +
            "No se siguen Connect/UNC.");

        try
        {
            InventoryWithDao(report, sourceCopy);
        }
        catch (Exception exception)
        {
            if (!report.Errors.Exists(item => item.StartsWith("DAO.", StringComparison.Ordinal)))
            {
                report.Errors.Add($"DAO.DBEngine.36: {RootMessage(exception)}");
            }
        }

        if (!WaitUntilFileUnlocked(sourceCopy))
        {
            report.Errors.Add("La copia Jet 3 sigue bloqueada por DAO; no se llama a ConvertAccessProject.");
        }
        else
        {
            try
            {
                ConvertCopyWithAccess(report, sourceCopy, convertedPath, beforePids);
            }
            catch (Exception exception)
            {
                if (!report.Errors.Exists(item => item.StartsWith("Access.", StringComparison.Ordinal)))
                {
                    report.Errors.Add($"Access.Application.11 conversión: {FormatCom(exception)}");
                }
            }

            WaitForAccessExit(report, beforePids);

            if (report.ConvertSucceeded && File.Exists(convertedPath))
            {
                try
                {
                    InspectConvertedWithDao(report, convertedPath);
                }
                catch (Exception exception)
                {
                    report.Warnings.Add($"DAO no pudo inspeccionar converted.mdb: {RootMessage(exception)}");
                }

                if (!WaitUntilFileUnlocked(convertedPath))
                {
                    report.Errors.Add("converted.mdb sigue bloqueada; no se llama a OpenCurrentDatabase.");
                }
                else if (!report.DialogDetected)
                {
                    try
                    {
                        AnalyzeConvertedWithAccess(report, convertedPath, exportDir, beforePids);
                    }
                    catch (Exception exception)
                    {
                        if (!report.Errors.Exists(item => item.StartsWith("Access.", StringComparison.Ordinal)))
                        {
                            report.Errors.Add($"Access.Application.11 apertura: {FormatCom(exception)}");
                        }
                    }
                }
            }
        }

        WaitForAccessExit(report, beforePids);

        var afterInfo = new FileInfo(report.MdbPath);
        report.OriginalSizeAfter = afterInfo.Length;
        report.LastWriteAfterUtc = afterInfo.LastWriteTimeUtc;
        report.HashAfter = ComputeSha256(report.MdbPath);
        report.OrphanAccessProcess = CurrentAccessPids().Except(beforePids).Any();

        if (!report.HashUnchanged)
        {
            report.Errors.Add("ERROR CRÍTICO: el SHA-256 del MDB original cambió.");
        }

        if (!report.LastWriteUnchanged)
        {
            report.Errors.Add("ERROR CRÍTICO: LastWriteTimeUtc del MDB original cambió.");
        }

        if (!report.SizeUnchanged)
        {
            report.Errors.Add("ERROR CRÍTICO: el tamaño del MDB original cambió.");
        }

        if (report.OrphanAccessProcess)
        {
            report.Errors.Add("ERROR CRÍTICO: quedó un MSACCESS.EXE iniciado por el probe.");
        }
    }

    private static void InventoryWithDao(ProbeReport report, string copyPath)
    {
        using var lifetime = new ComLifetime();
        object? engine = null;
        object? database = null;
        try
        {
            engine = lifetime.Track(Com.Create(ProbeConstants.DaoProgId));
            database = lifetime.Track(Com.Call(engine, "OpenDatabase", copyPath, false, true)!);
            report.JetVersion = Com.GetString(database, "Version");
            InventoryTableDefs(report, lifetime, database);
            InventoryQueryDefs(report, lifetime, database);
            InventoryRelations(report, lifetime, database);
            report.Forms = CountDocuments(lifetime, database, "Forms", report.FormNames, report);
            report.Reports = CountDocuments(lifetime, database, "Reports", report.ReportNames, report);
            report.Macros = CountDocuments(lifetime, database, "Scripts", samples: null, report);
            report.Modules = CountDocuments(lifetime, database, "Modules", report.ModuleNames, report);
        }
        catch (Exception exception)
        {
            report.Errors.Add($"DAO.DBEngine.36: {RootMessage(exception)}");
            throw;
        }
        finally
        {
            TryCall(database, "Close");
            TryCall(engine, "Idle", 8);
        }
    }

    private static void InventoryTableDefs(ProbeReport report, ComLifetime lifetime, object database)
    {
        object tableDefs;
        int tableCount;
        try
        {
            tableDefs = lifetime.Track(Com.Get(database, "TableDefs"));
            tableCount = Com.Count(tableDefs);
        }
        catch (Exception exception)
        {
            report.Warnings.Add($"TableDefs no enumerables: {RootMessage(exception)}");
            return;
        }

        for (var index = 0; index < tableCount; index++)
        {
            try
            {
                var table = lifetime.Track(Com.Item(tableDefs, index));
                var name = Com.GetString(table, "Name") ?? string.Empty;
                if (IsSystemObjectName(name))
                {
                    continue;
                }

                var attributesObject = Com.TryGet(table, "Attributes");
                var attributes = attributesObject is null
                    ? 0
                    : Convert.ToInt32(attributesObject, CultureInfo.InvariantCulture);
                var attachedByAttribute =
                    (attributes & ProbeConstants.DbAttachedTable) != 0
                    || (attributes & ProbeConstants.DbAttachedOdbc) != 0;

                string? connect = null;
                string? sourceTable = null;
                try
                {
                    connect = Com.GetString(table, "Connect");
                    sourceTable = Com.GetString(table, "SourceTableName");
                }
                catch (Exception exception)
                {
                    report.Warnings.Add(
                        $"Tabla '{name}': no se leyó Connect/SourceTableName ({RootMessage(exception)}).");
                }

                var attached = attachedByAttribute || !string.IsNullOrWhiteSpace(connect);
                if (!attached)
                {
                    report.LocalTables++;
                    continue;
                }

                report.LinkedTables++;
                var info = ClassifyLinkedTable(name, sourceTable, connect);
                report.LinkedTablesList.Add(info);
                if (info.IsUnc || info.IsOdbc || info.IsText)
                {
                    report.Warnings.Add(
                        $"Tabla '{name}' ({info.Kind}): solo se registra Connect; no se abre.");
                }
            }
            catch (Exception exception)
            {
                report.Warnings.Add($"TableDef[{index}]: {RootMessage(exception)}");
            }
        }
    }

    private static LinkedTableInfo ClassifyLinkedTable(string name, string? sourceTable, string? connect)
    {
        var text = connect ?? string.Empty;
        var info = new LinkedTableInfo
        {
            Name = name,
            SourceTableName = sourceTable,
            Connect = text
        };

        info.IsText = text.Contains("Text;", StringComparison.OrdinalIgnoreCase)
            || text.Contains("FMT=", StringComparison.OrdinalIgnoreCase)
            || (sourceTable?.EndsWith(".TXT", StringComparison.OrdinalIgnoreCase) ?? false)
            || text.Contains(".TXT", StringComparison.OrdinalIgnoreCase);
        info.IsOdbc = text.Contains("ODBC;", StringComparison.OrdinalIgnoreCase);
        info.IsInterBase = text.Contains(".gdb", StringComparison.OrdinalIgnoreCase)
            || text.Contains("InterBase", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ipvmain", StringComparison.OrdinalIgnoreCase);
        info.IsUnc = text.Contains(@"\\", StringComparison.Ordinal) || text.Contains("//", StringComparison.Ordinal);
        info.IsMdb = text.Contains(".mdb", StringComparison.OrdinalIgnoreCase);
        info.IsLocalPath = !info.IsUnc
            && (text.Contains(@"DATABASE=C:\", StringComparison.OrdinalIgnoreCase)
                || text.Contains(@"DATABASE=D:\", StringComparison.OrdinalIgnoreCase)
                || text.Contains(@":\", StringComparison.Ordinal));

        info.Kind = info.IsText ? "Text"
            : info.IsInterBase ? "InterBase"
            : info.IsOdbc ? "ODBC"
            : info.IsUnc && info.IsMdb ? "Access MDB (UNC)"
            : info.IsMdb && info.IsLocalPath ? "Access MDB (local)"
            : info.IsUnc ? "UNC path"
            : info.IsLocalPath ? "Local path"
            : "Unknown";
        return info;
    }

    private static void InventoryQueryDefs(ProbeReport report, ComLifetime lifetime, object database)
    {
        object queryDefs;
        int queryCount;
        try
        {
            queryDefs = lifetime.Track(Com.Get(database, "QueryDefs"));
            queryCount = Com.Count(queryDefs);
        }
        catch (Exception exception)
        {
            report.Warnings.Add($"QueryDefs no enumerables: {RootMessage(exception)}");
            return;
        }

        for (var index = 0; index < queryCount; index++)
        {
            try
            {
                var query = lifetime.Track(Com.Item(queryDefs, index));
                var name = Com.GetString(query, "Name") ?? string.Empty;
                if (IsSystemObjectName(name))
                {
                    continue;
                }

                report.QueryDefs++;
                if (report.SampleQueries.Count >= 8)
                {
                    continue;
                }

                string sql;
                try
                {
                    sql = Trim(Com.GetString(query, "SQL"), 160) ?? "(SQL vacío)";
                }
                catch (Exception exception)
                {
                    sql = $"(SQL no disponible: {RootMessage(exception)})";
                }

                report.SampleQueries.Add($"{name}: {sql}");
            }
            catch (Exception exception)
            {
                report.Warnings.Add($"QueryDef[{index}]: {RootMessage(exception)}");
            }
        }
    }

    private static void InventoryRelations(ProbeReport report, ComLifetime lifetime, object database)
    {
        object relations;
        int relationCount;
        try
        {
            relations = lifetime.Track(Com.Get(database, "Relations"));
            relationCount = Com.Count(relations);
        }
        catch (Exception exception)
        {
            report.Warnings.Add($"Relations no enumerables: {RootMessage(exception)}");
            return;
        }

        for (var index = 0; index < relationCount; index++)
        {
            try
            {
                var relation = lifetime.Track(Com.Item(relations, index));
                var name = Com.GetString(relation, "Name") ?? string.Empty;
                if (IsSystemObjectName(name))
                {
                    continue;
                }

                report.Relations++;
            }
            catch (Exception exception)
            {
                report.Warnings.Add($"Relation[{index}]: {RootMessage(exception)}");
            }
        }
    }

    private static int CountDocuments(
        ComLifetime lifetime,
        object database,
        string containerName,
        List<string>? samples,
        ProbeReport report)
    {
        try
        {
            var containers = lifetime.Track(Com.Get(database, "Containers"));
            var container = lifetime.Track(Com.Item(containers, containerName));
            var documents = lifetime.Track(Com.Get(container, "Documents"));
            var count = Com.Count(documents);
            var visible = 0;
            for (var index = 0; index < count; index++)
            {
                try
                {
                    var document = lifetime.Track(Com.Item(documents, index));
                    var name = Com.GetString(document, "Name") ?? string.Empty;
                    if (IsSystemObjectName(name))
                    {
                        continue;
                    }

                    visible++;
                    samples?.Add(name);
                }
                catch (Exception exception)
                {
                    report.Warnings.Add($"Container '{containerName}'[{index}]: {RootMessage(exception)}");
                }
            }

            return visible;
        }
        catch (Exception exception)
        {
            report.Warnings.Add($"Container '{containerName}': {RootMessage(exception)}");
            return 0;
        }
    }

    private static void ConvertCopyWithAccess(
        ProbeReport report,
        string sourceCopy,
        string convertedPath,
        IReadOnlySet<int> beforePids)
    {
        using var lifetime = new ComLifetime();
        object? app = null;
        try
        {
            app = lifetime.Track(Com.Create(ProbeConstants.AccessProgId));
            var started = CurrentAccessPids().Except(beforePids).ToArray();
            report.StartedAccessPid = started.Length == 0 ? null : started[0];
            report.AccessProcessPath = TryGetProcessPath(report.StartedAccessPid);
            report.AccessVersion = Com.GetString(app, "Version");

            if (!IsAccess2003(report.AccessVersion, report.AccessProcessPath))
            {
                report.Errors.Add(
                    $"Se automatizó Access '{report.AccessVersion}' en '{report.AccessProcessPath}'. " +
                    "Se esperaba Access 2003 (11.0 / OFFICE11). Abortado.");
                return;
            }

            ApplyAccessSafetySettings(app, report);
            using (new DialogGuard(report.StartedAccessPid ?? 0, report))
            {
                ConvertCopy(app, report, sourceCopy, convertedPath);
                TryCall(app, "CloseCurrentDatabase");
            }

            if (report.DialogDetected)
            {
                report.Errors.Add(
                    "Abortado: diálogo modal durante la conversión. No se pulsó ningún botón.");
            }
        }
        finally
        {
            TryCall(app, "Quit", ProbeConstants.AcQuitSaveNone);
        }
    }

    private static void InspectConvertedWithDao(ProbeReport report, string convertedPath)
    {
        using var lifetime = new ComLifetime();
        object? engine = null;
        object? database = null;
        try
        {
            engine = lifetime.Track(Com.Create(ProbeConstants.DaoProgId));
            database = lifetime.Track(Com.Call(engine, "OpenDatabase", convertedPath, false, true)!);
            report.ConvertedJetVersion = Com.GetString(database, "Version");
            report.Warnings.Add(
                $"DAO abrió converted.mdb en solo lectura. Jet={report.ConvertedJetVersion}.");
        }
        finally
        {
            TryCall(database, "Close");
            TryCall(engine, "Idle", 8);
        }
    }

    private static void AnalyzeConvertedWithAccess(
        ProbeReport report,
        string convertedPath,
        string exportDir,
        IReadOnlySet<int> beforePids)
    {
        using var lifetime = new ComLifetime();
        object? app = null;
        var databaseOpened = false;
        try
        {
            app = lifetime.Track(Com.Create(ProbeConstants.AccessProgId));
            var started = CurrentAccessPids().Except(beforePids).ToArray();
            report.StartedAccessPid = started.Length == 0 ? null : started[0];
            report.AccessProcessPath = TryGetProcessPath(report.StartedAccessPid);
            report.AccessVersion = Com.GetString(app, "Version");

            if (!IsAccess2003(report.AccessVersion, report.AccessProcessPath))
            {
                report.Errors.Add(
                    $"La segunda instancia de Access no es 2003: '{report.AccessVersion}' " +
                    $"'{report.AccessProcessPath}'. Abortado.");
                return;
            }

            ApplyAccessSafetySettings(app, report);
            using (new DialogGuard(report.StartedAccessPid ?? 0, report))
            {
                Com.Call(app, "OpenCurrentDatabase", convertedPath);
                databaseOpened = true;
                if (report.DialogDetected)
                {
                    report.OpenedConverted = false;
                    report.OpenMessage = "Diálogo al abrir la copia convertida. Abortado sin pulsar.";
                    report.Errors.Add(report.OpenMessage);
                    return;
                }

                report.OpenedConverted = true;
                report.OpenMessage = "OpenCurrentDatabase abrió converted.mdb en una instancia Access nueva.";
                InventoryOpenedProject(app, lifetime, report);
                ProbeOneForm(app, lifetime, report);
                ProbeOneReport(app, lifetime, report);
                ProbeVba(app, lifetime, report);
                ProbeSaveAsText(app, report, exportDir);
            }
        }
        catch (Exception exception)
        {
            report.Errors.Add($"Access.Application.11 apertura: {FormatCom(exception)}");
            throw;
        }
        finally
        {
            if (databaseOpened)
            {
                TryCall(app, "CloseCurrentDatabase");
            }

            TryCall(app, "Quit", ProbeConstants.AcQuitSaveNone);
        }
    }

    private static void ApplyAccessSafetySettings(object app, ProbeReport report)
    {
        try
        {
            Com.Set(app, "Visible", false);
            report.AccessHidden = true;
        }
        catch (Exception exception)
        {
            report.AccessHidden = false;
            report.Warnings.Add($"Visible=false no se pudo aplicar: {RootMessage(exception)}");
        }

        try
        {
            Com.Set(app, "UserControl", false);
        }
        catch (Exception exception)
        {
            report.Warnings.Add($"UserControl=false no se pudo aplicar: {RootMessage(exception)}");
        }

        try
        {
            Com.Set(app, "AutomationSecurity", ProbeConstants.MsoAutomationSecurityForceDisable);
        }
        catch (Exception exception)
        {
            report.Warnings.Add(
                $"AutomationSecurity=ForceDisable no se pudo aplicar: {RootMessage(exception)}");
        }
    }

    private static void ConvertCopy(object app, ProbeReport report, string sourceCopy, string convertedPath)
    {
        if (File.Exists(convertedPath))
        {
            File.SetAttributes(convertedPath, FileAttributes.Normal);
            File.Delete(convertedPath);
        }

        var attempts = new (int Format, string Name)[]
        {
            (ProbeConstants.AcFileFormatAccess2000, "acFileFormatAccess2000"),
            (ProbeConstants.AcFileFormatAccess2002, "acFileFormatAccess2002")
        };

        foreach (var attempt in attempts)
        {
            try
            {
                Com.Call(app, "ConvertAccessProject", sourceCopy, convertedPath, attempt.Format);
                if (!File.Exists(convertedPath) || new FileInfo(convertedPath).Length == 0)
                {
                    report.ConvertMessage =
                        $"ConvertAccessProject({attempt.Name}) no generó converted.mdb.";
                    continue;
                }

                report.ConvertSucceeded = true;
                report.DestinationFormatValue = attempt.Format;
                report.DestinationFormatName = attempt.Name;
                report.ConvertedSize = new FileInfo(convertedPath).Length;
                report.ConvertMessage =
                    $"ConvertAccessProject OK ({attempt.Name}). " +
                    $"Origen copia={report.SourceCopySize} bytes, destino={report.ConvertedSize} bytes.";
                return;
            }
            catch (Exception exception)
            {
                report.ConvertMessage =
                    $"ConvertAccessProject({attempt.Name}) falló: {FormatCom(exception)}";
                if (File.Exists(convertedPath))
                {
                    try
                    {
                        File.Delete(convertedPath);
                    }
                    catch
                    {
                        // Siguiente intento.
                    }
                }

                if (report.DialogDetected)
                {
                    return;
                }
            }
        }

        report.ConvertSucceeded = false;
        report.Errors.Add(report.ConvertMessage ?? "ConvertAccessProject no convirtió la copia.");
    }

    private static void InventoryOpenedProject(object app, ComLifetime lifetime, ProbeReport report)
    {
        try
        {
            var project = lifetime.Track(Com.Get(app, "CurrentProject"));
            report.CurrentProjectFileFormat = Convert.ToString(Com.TryGet(project, "FileFormat"), CultureInfo.InvariantCulture);
            report.CurrentProjectForms = CountAccessObjects(project, "AllForms", lifetime, report);
            report.CurrentProjectReports = CountAccessObjects(project, "AllReports", lifetime, report);
            report.CurrentProjectMacros = CountAccessObjects(project, "AllMacros", lifetime, report);
            report.CurrentProjectModules = CountAccessObjects(project, "AllModules", lifetime, report);
            if (report.CurrentProjectForms is > 0 && report.FormNames.Count == 0)
            {
                FillObjectNames(project, "AllForms", lifetime, report.FormNames);
            }

            if (report.CurrentProjectReports is > 0 && report.ReportNames.Count == 0)
            {
                FillObjectNames(project, "AllReports", lifetime, report.ReportNames);
            }

            if (report.CurrentProjectModules is > 0 && report.ModuleNames.Count == 0)
            {
                FillObjectNames(project, "AllModules", lifetime, report.ModuleNames);
            }
        }
        catch (Exception exception)
        {
            report.Warnings.Add($"CurrentProject: {RootMessage(exception)}");
        }

        try
        {
            var data = lifetime.Track(Com.Get(app, "CurrentData"));
            report.CurrentDataTables = CountAccessObjects(data, "AllTables", lifetime, report);
            report.CurrentDataQueries = CountAccessObjects(data, "AllQueries", lifetime, report);
        }
        catch (Exception exception)
        {
            report.Warnings.Add($"CurrentData: {RootMessage(exception)}");
        }
    }

    private static int CountAccessObjects(object parent, string collectionName, ComLifetime lifetime, ProbeReport report)
    {
        try
        {
            var collection = lifetime.Track(Com.Get(parent, collectionName));
            var count = Com.Count(collection);
            var visible = 0;
            for (var index = 0; index < count; index++)
            {
                var item = lifetime.Track(Com.Item(collection, index));
                var name = Com.GetString(item, "Name") ?? string.Empty;
                if (IsSystemObjectName(name))
                {
                    continue;
                }

                visible++;
            }

            return visible;
        }
        catch (Exception exception)
        {
            report.Warnings.Add($"{collectionName}: {RootMessage(exception)}");
            return 0;
        }
    }

    private static void FillObjectNames(object parent, string collectionName, ComLifetime lifetime, List<string> names)
    {
        var collection = lifetime.Track(Com.Get(parent, collectionName));
        var count = Com.Count(collection);
        for (var index = 0; index < count; index++)
        {
            var item = lifetime.Track(Com.Item(collection, index));
            var name = Com.GetString(item, "Name") ?? string.Empty;
            if (!IsSystemObjectName(name))
            {
                names.Add(name);
            }
        }
    }

    private static void ProbeOneForm(object app, ComLifetime lifetime, ProbeReport report)
    {
        var formName = ChooseObject(report.FormNames, "Formulario1");
        if (formName is null)
        {
            report.FormProbeLines.Add("No hay formularios de usuario para abrir en Design View.");
            return;
        }

        var doCmd = lifetime.Track(Com.Get(app, "DoCmd"));
        var opened = false;
        try
        {
            Com.Call(
                doCmd,
                "OpenForm",
                formName,
                ProbeConstants.AcDesign,
                Type.Missing,
                Type.Missing,
                Type.Missing,
                ProbeConstants.AcHidden);
            opened = true;
            if (report.DialogDetected)
            {
                report.FormProbeLines.Add(
                    $"Abortado al abrir '{formName}' por diálogo modal. No se pulsó ningún botón.");
                return;
            }

            var forms = lifetime.Track(Com.Get(app, "Forms"));
            var form = lifetime.Track(Com.Item(forms, formName));
            report.FormProbeLines.Add($"Formulario: {formName}");
            report.FormProbeLines.Add($"RecordSource: {Com.GetString(form, "RecordSource") ?? "(vacío)"}");
            report.FormProbeLines.Add($"Caption: {Com.GetString(form, "Caption") ?? "(vacío)"}");
            report.FormProbeLines.Add($"HasModule: {Com.TryGet(form, "HasModule")}");
            report.FormProbeLines.Add($"DefaultView: {Com.TryGet(form, "DefaultView")}");
            var controls = lifetime.Track(Com.Get(form, "Controls"));
            var count = Com.Count(controls);
            report.FormProbeLines.Add($"Controles: {count}");
            var listed = 0;
            for (var index = 0; index < count; index++)
            {
                var control = lifetime.Track(Com.Item(controls, index));
                var name = Com.GetString(control, "Name") ?? $"[{index}]";
                var type = Com.TryGet(control, "ControlType");
                var source = Com.GetString(control, "ControlSource");
                var rowSource = Com.GetString(control, "RowSource");
                var caption = Com.GetString(control, "Caption");
                var onClick = Com.GetString(control, "OnClick");
                var before = Com.GetString(control, "BeforeUpdate");
                var after = Com.GetString(control, "AfterUpdate");
                report.FormProbeLines.Add(
                    $"  - {name} | Type={type} | ControlSource={Trim(source, 80)} | " +
                    $"RowSource={Trim(rowSource, 60)} | Caption={Trim(caption, 40)} | " +
                    $"OnClick={Trim(onClick, 40)} | BeforeUpdate={Trim(before, 40)} | AfterUpdate={Trim(after, 40)}");
                listed++;
                if (listed >= 40)
                {
                    report.FormProbeLines.Add($"  ... {count - listed} controles más omitidos en consola.");
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            report.FormProbeLines.Add($"No se pudo inspeccionar '{formName}': {FormatCom(exception)}");
        }
        finally
        {
            if (opened)
            {
                TryCall(doCmd, "Close", ProbeConstants.AcForm, formName, ProbeConstants.AcSaveNo);
            }
        }
    }

    private static void ProbeOneReport(object app, ComLifetime lifetime, ProbeReport report)
    {
        var reportName = ChooseObject(report.ReportNames, "cierres");
        if (reportName is null)
        {
            report.ReportProbeLines.Add("No hay informes de usuario para abrir en Design View.");
            return;
        }

        var doCmd = lifetime.Track(Com.Get(app, "DoCmd"));
        var opened = false;
        try
        {
            Com.Call(
                doCmd,
                "OpenReport",
                reportName,
                ProbeConstants.AcDesign,
                Type.Missing,
                Type.Missing,
                ProbeConstants.AcHidden);
            opened = true;
            if (report.DialogDetected)
            {
                report.ReportProbeLines.Add(
                    $"Abortado al abrir '{reportName}' por diálogo modal. No se pulsó ningún botón.");
                return;
            }

            var reports = lifetime.Track(Com.Get(app, "Reports"));
            var accessReport = lifetime.Track(Com.Item(reports, reportName));
            report.ReportProbeLines.Add($"Informe: {reportName}");
            report.ReportProbeLines.Add($"RecordSource: {Com.GetString(accessReport, "RecordSource") ?? "(vacío)"}");
            report.ReportProbeLines.Add($"HasModule: {Com.TryGet(accessReport, "HasModule")}");
            var controls = lifetime.Track(Com.Get(accessReport, "Controls"));
            var count = Com.Count(controls);
            report.ReportProbeLines.Add($"Controles: {count}");
            var listed = 0;
            for (var index = 0; index < count; index++)
            {
                var control = lifetime.Track(Com.Item(controls, index));
                var name = Com.GetString(control, "Name") ?? $"[{index}]";
                var type = Com.TryGet(control, "ControlType");
                var source = Com.GetString(control, "ControlSource");
                report.ReportProbeLines.Add($"  - {name} | Type={type} | ControlSource={Trim(source, 80)}");
                listed++;
                if (listed >= 40)
                {
                    report.ReportProbeLines.Add($"  ... {count - listed} controles más omitidos en consola.");
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            report.ReportProbeLines.Add($"No se pudo inspeccionar '{reportName}': {FormatCom(exception)}");
        }
        finally
        {
            if (opened)
            {
                TryCall(doCmd, "Close", ProbeConstants.AcReport, reportName, ProbeConstants.AcSaveNo);
            }
        }
    }

    private static string? ChooseObject(List<string> names, string preferred)
    {
        if (names.Count == 0)
        {
            return null;
        }

        return names.Find(name => string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
            ?? names[0];
    }

    private static void ProbeVba(object app, ComLifetime lifetime, ProbeReport report)
    {
        try
        {
            var vbe = lifetime.Track(Com.Get(app, "VBE"));
            var projects = lifetime.Track(Com.Get(vbe, "VBProjects"));
            var projectCount = Com.Count(projects);
            var readable = 0;
            var blocked = 0;
            for (var projectIndex = 1; projectIndex <= projectCount; projectIndex++)
            {
                var project = lifetime.Track(Com.Item(projects, projectIndex));
                var components = lifetime.Track(Com.Get(project, "VBComponents"));
                var componentCount = Com.Count(components);
                for (var componentIndex = 1; componentIndex <= componentCount; componentIndex++)
                {
                    var component = lifetime.Track(Com.Item(components, componentIndex));
                    var name = Com.GetString(component, "Name") ?? $"[{componentIndex}]";
                    var type = Com.TryGet(component, "Type");
                    try
                    {
                        var module = lifetime.Track(Com.Get(component, "CodeModule"));
                        var lines = Convert.ToInt32(Com.Get(module, "CountOfLines"), CultureInfo.InvariantCulture);
                        readable++;
                        report.VbaLines.Add($"{name} | Type={type} | líneas={lines} | código accesible=sí");
                    }
                    catch (Exception exception)
                    {
                        blocked++;
                        report.VbaLines.Add(
                            $"{name} | Type={type} | código accesible=no ({RootMessage(exception)})");
                    }
                }
            }

            report.VbaAccessible = readable > 0 && blocked == 0;
            report.VbaMessage = report.VbaAccessible
                ? $"VBE accesible. Proyectos={projectCount}. Componentes con código={readable}."
                : blocked > 0
                    ? "VBA encontrado, pero VBE/Trust bloqueó parte o todo el código. No se cambia la seguridad de Office."
                    : "No se encontraron componentes VBA.";
            if (blocked > 0)
            {
                report.Warnings.Add(report.VbaMessage);
            }
        }
        catch (Exception exception)
        {
            report.VbaAccessible = false;
            report.VbaMessage =
                "VBA no se pudo inventariar por configuración de seguridad. " + RootMessage(exception);
            report.Warnings.Add(report.VbaMessage);
        }
    }

    private static void ProbeSaveAsText(object app, ProbeReport report, string exportDir)
    {
        Directory.CreateDirectory(exportDir);
        var messages = new List<string>();
        report.SaveAsTextForm = TrySaveAsText(
            app,
            ProbeConstants.AcForm,
            ChooseObject(report.FormNames, "Formulario1"),
            Path.Combine(exportDir, "form.txt"),
            messages);
        report.SaveAsTextReport = TrySaveAsText(
            app,
            ProbeConstants.AcReport,
            ChooseObject(report.ReportNames, "cierres"),
            Path.Combine(exportDir, "report.txt"),
            messages);
        report.SaveAsTextModule = TrySaveAsText(
            app,
            ProbeConstants.AcModule,
            ChooseObject(report.ModuleNames, report.ModuleNames.FirstOrDefault() ?? string.Empty),
            Path.Combine(exportDir, "module.txt"),
            messages);
        report.SaveAsTextMessage = string.Join(" ", messages);
    }

    private static bool TrySaveAsText(
        object app,
        int objectType,
        string? objectName,
        string output,
        List<string> messages)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            messages.Add($"{LabelFor(objectType)}: no hay objeto.");
            return false;
        }

        try
        {
            Com.Call(app, "SaveAsText", objectType, objectName, output);
            var exists = File.Exists(output);
            var length = exists ? new FileInfo(output).Length : 0;
            var ok = exists && length > 0;
            messages.Add(
                ok
                    ? $"{LabelFor(objectType)} '{objectName}' OK ({length} bytes)."
                    : $"{LabelFor(objectType)} '{objectName}' no generó archivo.");
            return ok;
        }
        catch (Exception exception)
        {
            messages.Add($"{LabelFor(objectType)} '{objectName}' falló: {RootMessage(exception)}");
            return false;
        }
    }

    private static string LabelFor(int objectType) => objectType switch
    {
        ProbeConstants.AcForm => "Form",
        ProbeConstants.AcReport => "Report",
        ProbeConstants.AcModule => "Module",
        _ => "Objeto"
    };

    private static void WaitForAccessExit(ProbeReport report, IReadOnlySet<int> beforePids)
    {
        if (report.StartedAccessPid is null or 0)
        {
            return;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!CurrentAccessPids().Except(beforePids).Any())
            {
                report.OrphanAccessProcess = false;
                return;
            }

            Thread.Sleep(500);
        }
    }

    private static HashSet<int> CurrentAccessPids()
    {
        return Process.GetProcessesByName("MSACCESS")
            .Select(process => process.Id)
            .ToHashSet();
    }

    private static string? TryGetProcessPath(int? pid)
    {
        if (pid is null or 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsAccess2003(string? version, string? path)
    {
        var versionOk = version is not null && version.StartsWith("11.", StringComparison.Ordinal);
        var pathOk = path is null
            || path.Contains("OFFICE11", StringComparison.OrdinalIgnoreCase);
        var isOffice15 = path is not null && path.Contains("Office15", StringComparison.OrdinalIgnoreCase);
        return versionOk && pathOk && !isOffice15;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void PrepareLocalWorkingCopy(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch
        {
            // El ADS puede no existir.
        }
    }

    private static void EnsureSafeLocalMdbPath(string path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Ruta vacía para {purpose}.");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Rechazado {purpose}: no se abren rutas UNC ({path}).");
        }

        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Rechazado {purpose}: la ruta resuelta es UNC ({full}).");
        }
    }

    private static bool WaitUntilFileUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
        }

        return false;
    }

    private static bool IsSystemObjectName(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            || name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~", StringComparison.Ordinal);
    }

    private static void TryCall(object? target, string method, params object[] args)
    {
        if (target is null)
        {
            return;
        }

        try
        {
            Com.Call(target, method, args);
        }
        catch
        {
            // Cierre/Quit best-effort.
        }
    }

    private static bool TryDeleteDirectory(string path, ProbeReport report)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch (Exception exception)
        {
            report.Warnings.Add($"No se pudo eliminar el temporal '{path}': {exception.Message}");
            return false;
        }
    }

    private static string FormatCom(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        if (current is COMException com)
        {
            return $"HRESULT=0x{com.ErrorCode:X8} {com.Message}";
        }

        return current.Message;
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var compact = value.Replace('\r', ' ').Replace('\n', ' ');
        return compact.Length <= max ? compact : compact[..max] + "...";
    }

    private static string RootMessage(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }

    private static void PrintReport(ProbeReport report)
    {
        Console.WriteLine("====================================================");
        Console.WriteLine("FASE 1 — CONVERSIÓN TEMPORAL ConvertAccessProject");
        Console.WriteLine("====================================================");
        Console.WriteLine($"MDB original: {report.MdbPath}");
        Console.WriteLine($"Copia origen: {report.SourceCopyPath ?? "(no creada)"}");
        Console.WriteLine($"Copia convertida: {report.ConvertedPath ?? "(no creada)"}");
        Console.WriteLine($"ProgID: {report.AccessProgId}");
        Console.WriteLine($"Access Version: {report.AccessVersion ?? "(desconocida)"}");
        Console.WriteLine($"Access Path: {report.AccessProcessPath ?? "(desconocida)"}");
        Console.WriteLine($"Proceso x86: IntPtr.Size={IntPtr.Size}");
        Console.WriteLine();

        Console.WriteLine("--- Conversión ---");
        Console.WriteLine($"ConvertAccessProject: {(report.ConvertSucceeded ? "sí" : "no")}");
        Console.WriteLine($"Formato origen (Jet): {report.JetVersion ?? "(desconocido)"}");
        Console.WriteLine($"Jet copia convertida: {report.ConvertedJetVersion ?? "(no leído)"}");
        Console.WriteLine($"Formato destino: {report.DestinationFormatName ?? "(ninguno)"} ({report.DestinationFormatValue?.ToString() ?? "-"})");
        Console.WriteLine($"Tamaño original: {report.OriginalSizeBefore} → {report.OriginalSizeAfter}");
        Console.WriteLine($"Tamaño copia Jet3: {report.SourceCopySize}");
        Console.WriteLine($"Tamaño convertido: {report.ConvertedSize}");
        Console.WriteLine($"Ruta temporal: {report.ConvertedPath}");
        Console.WriteLine(report.ConvertMessage);
        Console.WriteLine();

        Console.WriteLine("--- Apertura ---");
        Console.WriteLine($"OpenCurrentDatabase(converted): {(report.OpenedConverted ? "sí" : "no")}");
        Console.WriteLine($"Diálogos: {(report.DialogDetected ? "sí — " + report.DialogTitle : "no")}");
        Console.WriteLine($"Access oculto: {(report.AccessHidden ? "sí" : "no")}");
        Console.WriteLine($"FileFormat CurrentProject: {report.CurrentProjectFileFormat ?? "(n/d)"}");
        Console.WriteLine(report.OpenMessage);
        Console.WriteLine();

        Console.WriteLine("--- Objetos (DAO copia Jet 3) ---");
        Console.WriteLine($"Tablas locales:     {report.LocalTables}");
        Console.WriteLine($"Tablas vinculadas:  {report.LinkedTables}");
        Console.WriteLine($"QueryDefs:          {report.QueryDefs}");
        Console.WriteLine($"Relations:          {report.Relations}");
        Console.WriteLine($"Forms:              {report.Forms}");
        Console.WriteLine($"Reports:            {report.Reports}");
        Console.WriteLine($"Macros:             {report.Macros}");
        Console.WriteLine($"Modules:            {report.Modules}");
        if (report.OpenedConverted)
        {
            Console.WriteLine("--- CurrentProject / CurrentData (copia convertida) ---");
            Console.WriteLine($"AllForms:    {report.CurrentProjectForms}");
            Console.WriteLine($"AllReports:  {report.CurrentProjectReports}");
            Console.WriteLine($"AllMacros:   {report.CurrentProjectMacros}");
            Console.WriteLine($"AllModules:  {report.CurrentProjectModules}");
            Console.WriteLine($"AllTables:   {report.CurrentDataTables}");
            Console.WriteLine($"AllQueries:  {report.CurrentDataQueries}");
        }

        Console.WriteLine();
        Console.WriteLine("--- Probe formulario ---");
        foreach (var line in report.FormProbeLines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine("--- Probe informe ---");
        foreach (var line in report.ReportProbeLines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine("--- VBA ---");
        Console.WriteLine($"Código accesible: {(report.VbaAccessible ? "sí" : "no")}");
        Console.WriteLine(report.VbaMessage);
        foreach (var line in report.VbaLines)
        {
            Console.WriteLine($"  - {line}");
        }

        Console.WriteLine();
        Console.WriteLine("--- SaveAsText ---");
        Console.WriteLine($"Form:   {(report.SaveAsTextForm ? "sí" : "no")}");
        Console.WriteLine($"Report: {(report.SaveAsTextReport ? "sí" : "no")}");
        Console.WriteLine($"Module: {(report.SaveAsTextModule ? "sí" : "no")}");
        Console.WriteLine(report.SaveAsTextMessage);
        Console.WriteLine();

        Console.WriteLine("--- Seguridad original ---");
        Console.WriteLine($"Hash antes:  {report.HashBefore}");
        Console.WriteLine($"Hash después:{report.HashAfter}");
        Console.WriteLine($"Hash igual:  {report.HashUnchanged}");
        Console.WriteLine($"LastWrite antes:  {report.LastWriteBeforeUtc:O}");
        Console.WriteLine($"LastWrite después:{report.LastWriteAfterUtc:O}");
        Console.WriteLine($"LastWrite igual:  {report.LastWriteUnchanged}");
        Console.WriteLine($"Tamaño igual:     {report.SizeUnchanged}");
        Console.WriteLine($"MSACCESS previos: {report.PreExistingAccessProcesses}");
        Console.WriteLine($"PID iniciado:     {report.StartedAccessPid}");
        Console.WriteLine($"Huérfano:         {report.OrphanAccessProcess}");
        Console.WriteLine($"Temporales borrados: {(report.TempDeleted ? "sí" : "no")}");
        Console.WriteLine();

        Console.WriteLine("--- Arquitectura vinculadas ---");
        PrintLinkedGroup("MDB", report.LinkedTablesList.Where(item => item.IsMdb));
        PrintLinkedGroup("UNC", report.LinkedTablesList.Where(item => item.IsUnc));
        PrintLinkedGroup("ODBC/InterBase", report.LinkedTablesList.Where(item => item.IsOdbc || item.IsInterBase));
        PrintLinkedGroup("Texto", report.LinkedTablesList.Where(item => item.IsText));
        PrintLinkedGroup(
            "Otras",
            report.LinkedTablesList.Where(item => !item.IsMdb && !item.IsUnc && !item.IsOdbc && !item.IsInterBase && !item.IsText));

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine($"WARNING: {warning}");
        }

        foreach (var error in report.Errors)
        {
            Console.WriteLine($"ERROR: {error}");
        }

        Console.WriteLine("====================================================");
        Console.WriteLine(report.Passed ? "RESULTADO: OK" : "RESULTADO: FALLIDO");
        Console.WriteLine("====================================================");
    }

    private static void PrintLinkedGroup(string title, IEnumerable<LinkedTableInfo> items)
    {
        var list = items.ToList();
        Console.WriteLine($"{title}: {list.Count}");
        foreach (var item in list)
        {
            Console.WriteLine(
                $"  - {item.Name} | Source={item.SourceTableName} | Kind={item.Kind} | Connect={Trim(item.Connect, 140)}");
        }
    }
}
