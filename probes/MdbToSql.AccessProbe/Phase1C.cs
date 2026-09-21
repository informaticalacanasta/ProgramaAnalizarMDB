using System.Diagnostics;
using System.Globalization;

namespace MdbToSql.AccessProbe;

internal sealed class DaoPropertyInfo
{
    public string Name { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public string? Value { get; set; }
    public string Display => Exists ? $"Existe / {Value}" : "No existe";
}

internal static class Phase1C
{
    private static readonly string OriginalMdb = Path.Combine(
        @"C:\Users\Usuario\Desktop\LaCanasta\ORIGENMDB",
        "IMPORTAR.mdb");

    private static readonly string[] StartupPropertyNames =
    [
        "StartupForm",
        "StartupShowDBWindow",
        "StartupShowStatusBar",
        "AllowFullMenus",
        "AllowBuiltinToolbars",
        "AllowBreakIntoCode",
        "AllowSpecialKeys",
        "AllowBypassKey"
    ];

    public static int Run()
    {
        if (IntPtr.Size != 4)
        {
            Console.WriteLine("ERROR: Fase 1C requiere x86.");
            return 2;
        }

        var runId = Guid.NewGuid().ToString("N");
        var tempDir = Path.Combine(Path.GetTempPath(), "MdbToSql", runId);
        var warnings = new List<string>();
        var beforeAll = ProbeUtil.CurrentAccessPids();
        var report = new Phase1CReport();

        try
        {
            Directory.CreateDirectory(tempDir);
            ProbeUtil.EnsureSafeLocalMdbPath(OriginalMdb, "MDB original");
            if (!File.Exists(OriginalMdb))
            {
                throw new FileNotFoundException(OriginalMdb);
            }

            var originalInfo = new FileInfo(OriginalMdb);
            report.OriginalHashBefore = ProbeUtil.ComputeSha256(OriginalMdb);
            report.OriginalSizeBefore = originalInfo.Length;
            report.OriginalWriteBeforeUtc = originalInfo.LastWriteTimeUtc;

            var sourceCopy = Path.Combine(tempDir, "source-original-copy.mdb");
            var analysisSafe = Path.Combine(tempDir, "analysis-safe.mdb");
            var startupProbe = Path.Combine(tempDir, "StartupProbe.mdb");
            File.Copy(OriginalMdb, sourceCopy, overwrite: true);
            ProbeUtil.PrepareLocalWorkingCopy(sourceCopy);
            ProbeUtil.EnsureSafeLocalMdbPath(sourceCopy, "source-original-copy.mdb");
            report.SourceCopyHash = ProbeUtil.ComputeSha256(sourceCopy);
            report.SourceCopySize = new FileInfo(sourceCopy).Length;

            ForbidOriginal(sourceCopy);
            InventoryCopy(sourceCopy, report, warnings);
            ClassifyBypassKey(report);

            CreateStartupProbeDatabase(startupProbe, warnings);
            report.BypassControl = OpenInnocent(startupProbe, holdShift: false, warnings);
            report.BypassShift = OpenInnocent(startupProbe, holdShift: true, warnings);
            report.BypassDemonstrated =
                report.BypassControl.StartupExecuted
                && !report.BypassShift.StartupExecuted
                && report.BypassShift.Opened;

            File.Copy(sourceCopy, analysisSafe, overwrite: true);
            ProbeUtil.PrepareLocalWorkingCopy(analysisSafe);
            ProbeUtil.EnsureSafeLocalMdbPath(analysisSafe, "analysis-safe.mdb");
            report.AnalysisHashBefore = ProbeUtil.ComputeSha256(analysisSafe);

            if (report.BypassDemonstrated)
            {
                report.AnalysisChanges.Add(
                    "Ruta SHIFT: AllowBypassKey=true en analysis-safe.mdb si faltaba o era false. " +
                    "No se tocó AutoExec ni StartupForm.");
                EnsureAllowBypassKey(analysisSafe, report, warnings);
                report.AnalysisHashAfter = ProbeUtil.ComputeSha256(analysisSafe);
                report.ImportarOpen = OpenImportarCopy(
                    analysisSafe,
                    holdShift: true,
                    warnings,
                    report);
            }
            else
            {
                warnings.Add(
                    "La tabla SHIFT no quedó demostrada. No se abre analysis-safe con bypass. " +
                    "Fallback: neutralizar solo arranque en analysis-safe.mdb.");
                report.UsedFallback = true;
                NeutralizeStartup(analysisSafe, report, warnings);
                report.AnalysisHashAfter = ProbeUtil.ComputeSha256(analysisSafe);
                report.ImportarOpen = OpenImportarCopy(
                    analysisSafe,
                    holdShift: false,
                    warnings,
                    report);
                report.AnalysisChanges.Add("Apertura fallback: Low SIN SHIFT tras neutralizar arranque.");
            }
        }
        catch (Exception exception)
        {
            warnings.Add("Fase 1C abortada: " + ProbeUtil.Describe(exception).Message);
            Console.Error.WriteLine(exception);
        }
        finally
        {
            ProbeUtil.WaitUntilAccessGone(beforeAll);
            report.Orphan = ProbeUtil.CurrentAccessPids().Except(beforeAll).Any();
            var after = new FileInfo(OriginalMdb);
            report.OriginalHashAfter = ProbeUtil.ComputeSha256(OriginalMdb);
            report.OriginalSizeAfter = after.Length;
            report.OriginalWriteAfterUtc = after.LastWriteTimeUtc;
            report.TempsDeleted = ProbeUtil.TryDeleteDirectory(tempDir, warnings);
            report.Warnings.AddRange(warnings);
        }

        Print(report);
        return report.Orphan || !report.OriginalIntact ? 2 : 0;
    }

    private static void ForbidOriginal(string path)
    {
        if (ProbeUtil.SamePath(path, OriginalMdb))
        {
            throw new InvalidOperationException("Rechazado: se intentó usar el MDB original.");
        }
    }

    private static void InventoryCopy(string copyPath, Phase1CReport report, List<string> warnings)
    {
        using var lifetime = new ComLifetime();
        object? engine = null;
        object? database = null;
        try
        {
            engine = lifetime.Track(Com.Create(ProbeConstants.DaoProgId));
            database = lifetime.Track(Com.Call(engine, "OpenDatabase", copyPath, false, true)!);
            report.JetVersion = Com.GetString(database, "Version");
            CountTables(lifetime, database, report);
            report.QueryDefs = CountUserCollection(lifetime, database, "QueryDefs");
            report.Forms = CountDocuments(lifetime, database, "Forms");
            report.Reports = CountDocuments(lifetime, database, "Reports");
            report.Modules = CountDocuments(lifetime, database, "Modules");
            report.Macros = CountDocuments(lifetime, database, "Scripts");
            report.AutoExecExists = DocumentExists(lifetime, database, "Scripts", "AutoExec");
            foreach (var name in StartupPropertyNames)
            {
                report.StartupProperties.Add(ReadProperty(lifetime, database, name));
            }
        }
        catch (Exception exception)
        {
            warnings.Add("Inventario DAO: " + ProbeUtil.Describe(exception).Message);
        }
        finally
        {
            ProbeUtil.TryCall(database, "Close");
            ProbeUtil.TryCall(engine, "Idle", 8);
        }

        ProbeUtil.WaitUntilFileUnlocked(copyPath);
    }

    private static void ClassifyBypassKey(Phase1CReport report)
    {
        var prop = report.StartupProperties.Find(item => item.Name == "AllowBypassKey");
        if (prop is null || !prop.Exists)
        {
            report.BypassKeyCase = "A — AllowBypassKey not present";
            return;
        }

        if (IsDaoTrue(prop.Value))
        {
            report.BypassKeyCase = "B — bypass permitido";
            return;
        }

        report.BypassKeyCase = "C — bypass deshabilitado";
    }

    private static bool IsDaoTrue(string? value)
    {
        return value is "True" or "true" or "-1" or "1";
    }

    private static void CountTables(ComLifetime lifetime, object database, Phase1CReport report)
    {
        var tableDefs = lifetime.Track(Com.Get(database, "TableDefs"));
        var count = Com.Count(tableDefs);
        for (var index = 0; index < count; index++)
        {
            try
            {
                var table = lifetime.Track(Com.Item(tableDefs, index));
                var name = Com.GetString(table, "Name") ?? string.Empty;
                if (ProbeUtil.IsSystemObjectName(name))
                {
                    continue;
                }

                var connect = Com.GetString(table, "Connect");
                var attributesObject = Com.TryGet(table, "Attributes");
                var attributes = attributesObject is null
                    ? 0
                    : Convert.ToInt32(attributesObject, CultureInfo.InvariantCulture);
                var attached =
                    (attributes & ProbeConstants.DbAttachedTable) != 0
                    || (attributes & ProbeConstants.DbAttachedOdbc) != 0
                    || !string.IsNullOrWhiteSpace(connect);
                if (attached)
                {
                    report.LinkedTables++;
                }
                else
                {
                    report.LocalTables++;
                }
            }
            catch
            {
                // Siguiente TableDef.
            }
        }
    }

    private static int CountUserCollection(ComLifetime lifetime, object database, string collectionName)
    {
        var collection = lifetime.Track(Com.Get(database, collectionName));
        var count = Com.Count(collection);
        var visible = 0;
        for (var index = 0; index < count; index++)
        {
            try
            {
                var item = lifetime.Track(Com.Item(collection, index));
                var name = Com.GetString(item, "Name") ?? string.Empty;
                if (!ProbeUtil.IsSystemObjectName(name))
                {
                    visible++;
                }
            }
            catch
            {
                // Siguiente.
            }
        }

        return visible;
    }

    private static int CountDocuments(ComLifetime lifetime, object database, string containerName)
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
                var document = lifetime.Track(Com.Item(documents, index));
                var name = Com.GetString(document, "Name") ?? string.Empty;
                if (!ProbeUtil.IsSystemObjectName(name))
                {
                    visible++;
                }
            }

            return visible;
        }
        catch
        {
            return 0;
        }
    }

    private static bool DocumentExists(ComLifetime lifetime, object database, string containerName, string documentName)
    {
        try
        {
            var containers = lifetime.Track(Com.Get(database, "Containers"));
            var container = lifetime.Track(Com.Item(containers, containerName));
            var documents = lifetime.Track(Com.Get(container, "Documents"));
            var count = Com.Count(documents);
            for (var index = 0; index < count; index++)
            {
                var document = lifetime.Track(Com.Item(documents, index));
                var name = Com.GetString(document, "Name");
                if (string.Equals(name, documentName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static DaoPropertyInfo ReadProperty(ComLifetime lifetime, object database, string name)
    {
        var info = new DaoPropertyInfo { Name = name };
        try
        {
            var properties = lifetime.Track(Com.Get(database, "Properties"));
            var property = lifetime.Track(Com.Item(properties, name));
            var value = Com.TryGet(property, "Value");
            info.Exists = true;
            info.Value = value is null or DBNull ? "(null)" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            info.Exists = false;
        }

        return info;
    }

    private static void CreateStartupProbeDatabase(string path, List<string> warnings)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var before = ProbeUtil.CurrentAccessPids();
        using var lifetime = new ComLifetime();
        object? app = null;
        try
        {
            app = lifetime.Track(Com.Create(ProbeConstants.AccessProgId));
            var pid = ProbeUtil.CurrentAccessPids().Except(before).FirstOrDefault();
            var version = Com.GetString(app, "Version");
            var exe = ProbeUtil.TryGetProcessPath(pid == 0 ? null : pid);
            if (!ProbeUtil.IsAccess2003(version, exe))
            {
                throw new InvalidOperationException($"Probe startup no es Access 2003: {version} {exe}");
            }

            Com.Set(app, "Visible", false);
            Com.Set(app, "AutomationSecurity", ProbeConstants.MsoAutomationSecurityLow);
            try
            {
                Com.Call(app, "NewCurrentDatabase", path);
            }
            catch
            {
                Com.Call(app, "NewCurrentDatabase", path, ProbeConstants.AcFileFormatAccess2000);
            }

            var form = lifetime.Track(Com.Call(app, "CreateForm")!);
            var tempName = Com.GetString(form, "Name") ?? "Form1";
            Com.Set(form, "Caption", "StartupProbeForm");
            var doCmd = lifetime.Track(Com.Get(app, "DoCmd"));
            Com.Call(doCmd, "Close", ProbeConstants.AcForm, tempName, ProbeConstants.AcSaveYes);
            if (!string.Equals(tempName, "StartupProbeForm", StringComparison.OrdinalIgnoreCase))
            {
                Com.Call(doCmd, "Rename", "StartupProbeForm", ProbeConstants.AcForm, tempName);
            }
        }
        catch (Exception exception)
        {
            warnings.Add("No se pudo construir StartupProbe.mdb: " + ProbeUtil.Describe(exception).Message);
        }
        finally
        {
            ProbeUtil.TryCall(app, "CloseCurrentDatabase");
            ProbeUtil.TryCall(app, "Quit", ProbeConstants.AcQuitSaveNone);
            ProbeUtil.WaitUntilAccessGone(before);
        }

        if (!File.Exists(path))
        {
            return;
        }

        SetDaoProperty(path, "StartupForm", ProbeConstants.DbText, "StartupProbeForm", warnings);
        SetDaoProperty(path, "AllowBypassKey", ProbeConstants.DbBoolean, true, warnings);
        ProbeUtil.WaitUntilFileUnlocked(path);
    }

    private static OpenSnapshot OpenInnocent(string path, bool holdShift, List<string> warnings)
    {
        var snapshot = OpenWithLow(path, holdShift, visible: false, warnings);
        snapshot.CaseName = holdShift ? "Bypass" : "Control";
        return snapshot;
    }

    private static OpenSnapshot OpenImportarCopy(
        string path,
        bool holdShift,
        List<string> warnings,
        Phase1CReport report)
    {
        ForbidOriginal(path);
        if (ProbeUtil.SamePath(path, OriginalMdb))
        {
            throw new InvalidOperationException("No se abre IMPORTAR.mdb original con Low.");
        }

        var snapshot = OpenWithLow(path, holdShift, visible: false, warnings);
        snapshot.CaseName = holdShift ? "analysis-safe + SHIFT" : "analysis-safe fallback";
        if (snapshot.Opened && snapshot.OpenForms > 0)
        {
            warnings.Add("ABORTADO: quedaron formularios abiertos tras OpenCurrentDatabase.");
            snapshot.Aborted = true;
        }

        if (snapshot.Opened && snapshot.OpenReports > 0)
        {
            warnings.Add("ABORTADO: quedaron informes abiertos tras OpenCurrentDatabase.");
            snapshot.Aborted = true;
        }

        report.LinkedAccessAttempted = false;
        return snapshot;
    }

    private static OpenSnapshot OpenWithLow(string path, bool holdShift, bool visible, List<string> warnings)
    {
        var result = new OpenSnapshot { HoldShift = holdShift };
        ForbidOriginal(path);
        ProbeUtil.EnsureSafeLocalMdbPath(path, Path.GetFileName(path));
        ProbeUtil.WaitUntilFileUnlocked(path);

        var before = ProbeUtil.CurrentAccessPids();
        using var lifetime = new ComLifetime();
        object? app = null;
        var capture = new DialogCapture();
        ShiftHold? shift = null;
        var opened = false;
        try
        {
            app = lifetime.Track(Com.Create(ProbeConstants.AccessProgId));
            var pid = ProbeUtil.CurrentAccessPids().Except(before).FirstOrDefault();
            result.Pid = pid == 0 ? null : pid;
            result.AccessVersion = Com.GetString(app, "Version");
            result.AccessPath = ProbeUtil.TryGetProcessPath(result.Pid);
            if (!ProbeUtil.IsAccess2003(result.AccessVersion, result.AccessPath))
            {
                result.Message = "No es Access 2003.";
                return result;
            }

            Com.Set(app, "Visible", visible);
            Com.Set(app, "AutomationSecurity", ProbeConstants.MsoAutomationSecurityLow);
            result.Security = ReadSecurity(app);
            using (new DialogGuard(result.Pid ?? 0, capture))
            {
                if (holdShift)
                {
                    shift = new ShiftHold();
                    shift.Press();
                }

                Com.Call(app, "OpenCurrentDatabase", path, false);
                opened = true;
                result.Opened = true;
                FillSnapshot(app, lifetime, result);
            }
        }
        catch (Exception exception)
        {
            var info = ProbeUtil.Describe(exception);
            result.Opened = false;
            result.HResultHex = info.HResultHex;
            result.AccessError = info.AccessError;
            result.Message = info.Message;
        }
        finally
        {
            shift?.Dispose();
            result.DialogDetected = capture.Detected;
            result.DialogTitle = capture.Title;
            if (opened)
            {
                ProbeUtil.TryCall(app, "CloseCurrentDatabase");
            }

            ProbeUtil.TryCall(app, "Quit", ProbeConstants.AcQuitSaveNone);
            ProbeUtil.WaitUntilAccessGone(before);
        }

        if (result.DialogDetected)
        {
            warnings.Add($"Diálogo en {Path.GetFileName(path)}: {result.DialogTitle}. No se pulsó.");
        }

        return result;
    }

    private static void FillSnapshot(object app, ComLifetime lifetime, OpenSnapshot result)
    {
        try
        {
            var project = lifetime.Track(Com.Get(app, "CurrentProject"));
            result.ProjectName = Com.GetString(project, "Name");
            result.ProjectFullName = Com.GetString(project, "FullName");
            result.AllForms = CountAccessObjects(lifetime, project, "AllForms");
            result.AllReports = CountAccessObjects(lifetime, project, "AllReports");
            result.AllModules = CountAccessObjects(lifetime, project, "AllModules");
            result.AllMacros = CountAccessObjects(lifetime, project, "AllMacros");
        }
        catch (Exception exception)
        {
            result.Message = "CurrentProject: " + ProbeUtil.Describe(exception).Message;
        }

        result.OpenForms = CountOpenCollection(lifetime, app, "Forms", result.OpenFormNames);
        result.OpenReports = CountOpenCollection(lifetime, app, "Reports", result.OpenReportNames);
        result.StartupExecuted = result.OpenFormNames.Exists(name =>
            string.Equals(name, "StartupProbeForm", StringComparison.OrdinalIgnoreCase))
            || result.OpenForms > 0;
    }

    private static int CountAccessObjects(ComLifetime lifetime, object parent, string collectionName)
    {
        var collection = lifetime.Track(Com.Get(parent, collectionName));
        var count = Com.Count(collection);
        var visible = 0;
        for (var index = 0; index < count; index++)
        {
            var item = lifetime.Track(Com.Item(collection, index));
            var name = Com.GetString(item, "Name") ?? string.Empty;
            if (!ProbeUtil.IsSystemObjectName(name))
            {
                visible++;
            }
        }

        return visible;
    }

    private static int CountOpenCollection(
        ComLifetime lifetime,
        object app,
        string collectionName,
        List<string> names)
    {
        try
        {
            var collection = lifetime.Track(Com.Get(app, collectionName));
            var count = Com.Count(collection);
            for (var index = 0; index < count; index++)
            {
                var item = lifetime.Track(Com.Item(collection, index));
                var name = Com.GetString(item, "Name") ?? $"[{index}]";
                names.Add(name);
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadSecurity(object app)
    {
        var value = Com.TryGet(app, "AutomationSecurity");
        return value is null ? "(n/d)" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(n/d)";
    }

    private static void EnsureAllowBypassKey(string path, Phase1CReport report, List<string> warnings)
    {
        var current = report.StartupProperties.Find(item => item.Name == "AllowBypassKey");
        if (current is { Exists: true } && IsDaoTrue(current.Value))
        {
            report.AnalysisChanges.Add("AllowBypassKey ya era true; no se modificó.");
            return;
        }

        SetDaoProperty(path, "AllowBypassKey", ProbeConstants.DbBoolean, true, warnings);
        report.AnalysisChanges.Add("AllowBypassKey establecido a true en analysis-safe.mdb.");
    }

    private static void NeutralizeStartup(string path, Phase1CReport report, List<string> warnings)
    {
        report.AutoExecEvidence = report.AutoExecExists
            ? "AutoExec existía en la copia origen. En analysis-safe se renombra a AutoExec_ProbeDisabled."
            : "No había AutoExec.";
        using var lifetime = new ComLifetime();
        object? engine = null;
        object? database = null;
        try
        {
            engine = lifetime.Track(Com.Create(ProbeConstants.DaoProgId));
            database = lifetime.Track(Com.Call(engine, "OpenDatabase", path, true, false)!);
            SetOrCreateProperty(lifetime, database, "AllowBypassKey", ProbeConstants.DbBoolean, true);
            report.AnalysisChanges.Add("AllowBypassKey=true");
            var startup = ReadProperty(lifetime, database, "StartupForm");
            if (startup.Exists && !string.IsNullOrWhiteSpace(startup.Value) && startup.Value != "(null)")
            {
                SetOrCreateProperty(lifetime, database, "StartupForm", ProbeConstants.DbText, string.Empty);
                report.AnalysisChanges.Add($"StartupForm '{startup.Value}' vaciado.");
            }

            if (report.AutoExecExists)
            {
                RenameAutoExec(lifetime, database, warnings);
                report.AnalysisChanges.Add("Macro AutoExec → AutoExec_ProbeDisabled");
            }
        }
        catch (Exception exception)
        {
            warnings.Add("Neutralización DAO: " + ProbeUtil.Describe(exception).Message);
        }
        finally
        {
            ProbeUtil.TryCall(database, "Close");
            ProbeUtil.TryCall(engine, "Idle", 8);
            ProbeUtil.WaitUntilFileUnlocked(path);
        }
    }

    private static void RenameAutoExec(ComLifetime lifetime, object database, List<string> warnings)
    {
        try
        {
            var containers = lifetime.Track(Com.Get(database, "Containers"));
            var scripts = lifetime.Track(Com.Item(containers, "Scripts"));
            var documents = lifetime.Track(Com.Get(scripts, "Documents"));
            var count = Com.Count(documents);
            for (var index = 0; index < count; index++)
            {
                var document = lifetime.Track(Com.Item(documents, index));
                var name = Com.GetString(document, "Name");
                if (!string.Equals(name, "AutoExec", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Com.Set(document, "Name", "AutoExec_ProbeDisabled");
                return;
            }
        }
        catch (Exception exception)
        {
            warnings.Add("No se pudo renombrar AutoExec: " + ProbeUtil.Describe(exception).Message);
        }
    }

    private static void SetDaoProperty(string path, string name, int type, object value, List<string> warnings)
    {
        using var lifetime = new ComLifetime();
        object? engine = null;
        object? database = null;
        try
        {
            engine = lifetime.Track(Com.Create(ProbeConstants.DaoProgId));
            database = lifetime.Track(Com.Call(engine, "OpenDatabase", path, true, false)!);
            SetOrCreateProperty(lifetime, database, name, type, value);
        }
        catch (Exception exception)
        {
            warnings.Add($"No se pudo asignar {name}: " + ProbeUtil.Describe(exception).Message);
        }
        finally
        {
            ProbeUtil.TryCall(database, "Close");
            ProbeUtil.TryCall(engine, "Idle", 8);
            ProbeUtil.WaitUntilFileUnlocked(path);
        }
    }

    private static void SetOrCreateProperty(
        ComLifetime lifetime,
        object database,
        string name,
        int type,
        object value)
    {
        var properties = lifetime.Track(Com.Get(database, "Properties"));
        try
        {
            var property = lifetime.Track(Com.Item(properties, name));
            Com.Set(property, "Value", value);
        }
        catch
        {
            var created = lifetime.Track(Com.Call(database, "CreateProperty", name, type, value)!);
            Com.Call(properties, "Append", created);
        }
    }

    private static void Print(Phase1CReport report)
    {
        Console.WriteLine("====================================================");
        Console.WriteLine("FASE 1C — APERTURA SEGURA DE COPIA IMPORTAR.mdb");
        Console.WriteLine("====================================================");
        Console.WriteLine($"IntPtr.Size={IntPtr.Size}");
        Console.WriteLine();
        Console.WriteLine("--- Startup original (copia DAO, sin modificar) ---");
        Console.WriteLine($"Jet: {report.JetVersion}");
        Console.WriteLine($"Tablas locales={report.LocalTables} vinculadas={report.LinkedTables} QueryDefs={report.QueryDefs}");
        Console.WriteLine($"Forms={report.Forms} Reports={report.Reports} Modules={report.Modules} Macros={report.Macros}");
        Console.WriteLine($"AutoExecExists={report.AutoExecExists}");
        foreach (var property in report.StartupProperties)
        {
            Console.WriteLine($"  {property.Name}: {property.Display}");
        }

        Console.WriteLine($"AllowBypassKey: {report.BypassKeyCase}");
        Console.WriteLine();
        Console.WriteLine("--- Probe bypass SHIFT (MDB inocuo StartupProbe.mdb) ---");
        Console.WriteLine("SHIFT simulado con SendInput (VK_SHIFT + VK_LSHIFT), try/finally.");
        PrintSnapshot(report.BypassControl);
        PrintSnapshot(report.BypassShift);
        Console.WriteLine("| Caso    | Security | Bypass SHIFT | Startup ejecutado |");
        Console.WriteLine("| ------- | -------: | -----------: | ----------------: |");
        Console.WriteLine(
            $"| Control |      Low |           No | {(report.BypassControl.StartupExecuted ? "Sí" : "No"),17} |");
        Console.WriteLine(
            $"| Bypass  |      Low |           Sí | {(report.BypassShift.StartupExecuted ? "Sí" : "No"),17} |");
        Console.WriteLine($"Tabla demostrada: {(report.BypassDemonstrated ? "sí" : "NO")}");
        Console.WriteLine($"Fallback usado: {(report.UsedFallback ? "sí" : "no")}");
        Console.WriteLine();
        Console.WriteLine("--- analysis-safe.mdb ---");
        foreach (var change in report.AnalysisChanges)
        {
            Console.WriteLine($"Cambio: {change}");
        }

        if (!string.IsNullOrWhiteSpace(report.AutoExecEvidence))
        {
            Console.WriteLine(report.AutoExecEvidence);
        }

        Console.WriteLine($"Hash antes:  {report.AnalysisHashBefore}");
        Console.WriteLine($"Hash después:{report.AnalysisHashAfter}");
        PrintSnapshot(report.ImportarOpen);
        Console.WriteLine();
        Console.WriteLine("--- Inventario Access (solo counts) ---");
        if (report.ImportarOpen is { Opened: true, Aborted: false })
        {
            Console.WriteLine($"AllForms={report.ImportarOpen.AllForms} AllReports={report.ImportarOpen.AllReports} " +
                              $"AllModules={report.ImportarOpen.AllModules} AllMacros={report.ImportarOpen.AllMacros}");
            Console.WriteLine($"Forms abiertos={report.ImportarOpen.OpenForms} Reports abiertos={report.ImportarOpen.OpenReports}");
        }
        else
        {
            Console.WriteLine("No hay inventario Access: no se abrió de forma segura o se abortó.");
        }

        Console.WriteLine();
        Console.WriteLine("--- Seguridad ---");
        Console.WriteLine($"Original hash antes:  {report.OriginalHashBefore}");
        Console.WriteLine($"Original hash después:{report.OriginalHashAfter}");
        Console.WriteLine($"Original hash igual:  {report.OriginalIntactHash}");
        Console.WriteLine($"Original size {report.OriginalSizeBefore} → {report.OriginalSizeAfter}");
        Console.WriteLine($"Original LastWrite antes:  {report.OriginalWriteBeforeUtc:O}");
        Console.WriteLine($"Original LastWrite después:{report.OriginalWriteAfterUtc:O}");
        Console.WriteLine($"source-original-copy SHA-256: {report.SourceCopyHash}");
        Console.WriteLine("Accesos UNC intencionales: 0 (no RefreshLink, no QueryDefs, no Form View).");
        Console.WriteLine("No se midió tráfico de red a bajo nivel.");
        Console.WriteLine($"MSACCESS huérfano: {report.Orphan}");
        Console.WriteLine($"Temporales eliminados: {(report.TempsDeleted ? "sí" : "no")}");
        Console.WriteLine($"IMPORTAR.mdb abierto con Low: NO");
        foreach (var warning in report.Warnings)
        {
            Console.WriteLine("WARNING: " + warning);
        }

        Console.WriteLine();
        Console.WriteLine("--- Conclusión ---");
        Console.WriteLine(BuildConclusion(report));
        Console.WriteLine("====================================================");
    }

    private static void PrintSnapshot(OpenSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            Console.WriteLine("(no ejecutado)");
            return;
        }

        Console.WriteLine(
            $"{snapshot.CaseName}: opened={snapshot.Opened} security={snapshot.Security} shift={snapshot.HoldShift} " +
            $"startup={snapshot.StartupExecuted} formsAbiertos={snapshot.OpenForms} " +
            $"[{string.Join(", ", snapshot.OpenFormNames)}] HRESULT={snapshot.HResultHex ?? "OK"} " +
            $"err={snapshot.AccessError?.ToString() ?? "-"} dialog={(snapshot.DialogDetected ? snapshot.DialogTitle : "no")} " +
            $"msg={snapshot.Message} abort={snapshot.Aborted}");
        if (snapshot.Opened)
        {
            Console.WriteLine(
                $"  CurrentProject={snapshot.ProjectName} FullName={snapshot.ProjectFullName}");
        }
    }

    private static string BuildConclusion(Phase1CReport report)
    {
        var open = report.ImportarOpen;
        var safeOpen = open is { Opened: true, Aborted: false, OpenForms: 0, OpenReports: 0 };
        if (report.BypassDemonstrated && safeOpen && report.OriginalIntact && !report.Orphan)
        {
            return "A) Podemos abrir una copia de IMPORTAR.mdb de forma reproducible sin ejecutar startup.";
        }

        if (!report.BypassDemonstrated && report.UsedFallback && safeOpen && report.OriginalIntact && !report.Orphan)
        {
            return "A) Podemos abrir una copia de IMPORTAR.mdb de forma reproducible sin ejecutar startup. " +
                   "(SHIFT no quedó demostrado; se usó fallback de neutralización de arranque en analysis-safe.mdb.)";
        }

        return "B) Todavía no podemos demostrar una apertura segura; no continuar con análisis de Forms/VBA.";
    }
}

internal sealed class OpenSnapshot
{
    public string CaseName { get; set; } = string.Empty;
    public bool HoldShift { get; set; }
    public bool Opened { get; set; }
    public bool StartupExecuted { get; set; }
    public bool Aborted { get; set; }
    public bool DialogDetected { get; set; }
    public string? DialogTitle { get; set; }
    public string? Security { get; set; }
    public string? ProjectName { get; set; }
    public string? ProjectFullName { get; set; }
    public int AllForms { get; set; }
    public int AllReports { get; set; }
    public int AllModules { get; set; }
    public int AllMacros { get; set; }
    public int OpenForms { get; set; }
    public int OpenReports { get; set; }
    public List<string> OpenFormNames { get; } = [];
    public List<string> OpenReportNames { get; } = [];
    public string? HResultHex { get; set; }
    public int? AccessError { get; set; }
    public string? Message { get; set; }
    public int? Pid { get; set; }
    public string? AccessVersion { get; set; }
    public string? AccessPath { get; set; }
}

internal sealed class Phase1CReport
{
    public string? JetVersion { get; set; }
    public int LocalTables { get; set; }
    public int LinkedTables { get; set; }
    public int QueryDefs { get; set; }
    public int Forms { get; set; }
    public int Reports { get; set; }
    public int Modules { get; set; }
    public int Macros { get; set; }
    public bool AutoExecExists { get; set; }
    public string BypassKeyCase { get; set; } = string.Empty;
    public List<DaoPropertyInfo> StartupProperties { get; } = [];
    public OpenSnapshot BypassControl { get; set; } = new();
    public OpenSnapshot BypassShift { get; set; } = new();
    public bool BypassDemonstrated { get; set; }
    public bool UsedFallback { get; set; }
    public List<string> AnalysisChanges { get; } = [];
    public string? AutoExecEvidence { get; set; }
    public string AnalysisHashBefore { get; set; } = string.Empty;
    public string AnalysisHashAfter { get; set; } = string.Empty;
    public OpenSnapshot? ImportarOpen { get; set; }
    public bool LinkedAccessAttempted { get; set; }
    public string OriginalHashBefore { get; set; } = string.Empty;
    public string OriginalHashAfter { get; set; } = string.Empty;
    public long OriginalSizeBefore { get; set; }
    public long OriginalSizeAfter { get; set; }
    public DateTime OriginalWriteBeforeUtc { get; set; }
    public DateTime OriginalWriteAfterUtc { get; set; }
    public string SourceCopyHash { get; set; } = string.Empty;
    public long SourceCopySize { get; set; }
    public bool Orphan { get; set; }
    public bool TempsDeleted { get; set; }
    public List<string> Warnings { get; } = [];
    public bool OriginalIntactHash =>
        string.Equals(OriginalHashBefore, OriginalHashAfter, StringComparison.OrdinalIgnoreCase);
    public bool OriginalIntact =>
        OriginalIntactHash
        && OriginalSizeBefore == OriginalSizeAfter
        && OriginalWriteBeforeUtc == OriginalWriteAfterUtc;
}
