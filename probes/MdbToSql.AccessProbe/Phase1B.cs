using System.Diagnostics;

namespace MdbToSql.AccessProbe;

internal sealed class OpenCaseResult
{
    public string CaseId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string OpenCall { get; set; } = string.Empty;
    public string? SecurityRequested { get; set; }
    public string? SecurityBefore { get; set; }
    public string? SecurityAfter { get; set; }
    public bool Opened { get; set; }
    public string? HResultHex { get; set; }
    public int? AccessError { get; set; }
    public string? Message { get; set; }
    public string? Source { get; set; }
    public string? InnerException { get; set; }
    public bool DialogDetected { get; set; }
    public string? DialogTitle { get; set; }
    public int? Pid { get; set; }
    public string? AccessVersion { get; set; }
    public string? AccessPath { get; set; }
    public long DurationMs { get; set; }
    public string? Notes { get; set; }
}

internal static class Phase1B
{
    public static int Run()
    {
        if (IntPtr.Size != 4)
        {
            Console.WriteLine("ERROR: Fase 1B requiere x86. IntPtr.Size=" + IntPtr.Size);
            return 2;
        }

        var runId = Guid.NewGuid().ToString("N");
        var tempDir = Path.Combine(Path.GetTempPath(), "MdbToSql", runId);
        var warnings = new List<string>();
        var cases = new List<OpenCaseResult>();
        string? accessCreated = null;
        string? daoCreated = null;
        string? newCurrentProjectName = null;
        string? newCurrentProjectFullName = null;
        string? newCurrentDbName = null;
        var newCurrentDatabaseWorked = false;
        var tempsDeleted = false;
        var orphan = false;
        var beforeAll = ProbeUtil.CurrentAccessPids();

        try
        {
            Directory.CreateDirectory(tempDir);
            accessCreated = Path.Combine(tempDir, "AccessCreated.mdb");
            daoCreated = Path.Combine(tempDir, "DaoCreated.mdb");
            ProbeUtil.EnsureSafeLocalMdbPath(accessCreated, "AccessCreated.mdb");
            ProbeUtil.EnsureSafeLocalMdbPath(daoCreated, "DaoCreated.mdb");

            var created = CreateAccessDatabase(accessCreated, cases, warnings);
            newCurrentDatabaseWorked = created.Opened;
            ParseCreateNotes(
                created,
                out newCurrentProjectName,
                out newCurrentProjectFullName,
                out newCurrentDbName);
            if (!File.Exists(accessCreated))
            {
                warnings.Add("NewCurrentDatabase no dejó AccessCreated.mdb.");
            }

            CreateDaoDatabase(daoCreated, warnings);

            if (File.Exists(accessCreated))
            {
                cases.Add(OpenWithNewInstance(
                    "A",
                    "AutomationSecurity: no tocar (valor inicial)",
                    accessCreated,
                    "Access.Application.11.NewCurrentDatabase",
                    security: null,
                    openArgs: [accessCreated, false],
                    openCall: "OpenCurrentDatabase(path, false)"));

                cases.Add(OpenWithNewInstance(
                    "B",
                    "AutomationSecurity = msoAutomationSecurityByUI (2)",
                    accessCreated,
                    "Access.Application.11.NewCurrentDatabase",
                    security: ProbeConstants.MsoAutomationSecurityByUI,
                    openArgs: [accessCreated, false],
                    openCall: "OpenCurrentDatabase(path, false)"));

                cases.Add(OpenWithNewInstance(
                    "C",
                    "AutomationSecurity = msoAutomationSecurityForceDisable (3)",
                    accessCreated,
                    "Access.Application.11.NewCurrentDatabase",
                    security: ProbeConstants.MsoAutomationSecurityForceDisable,
                    openArgs: [accessCreated, false],
                    openCall: "OpenCurrentDatabase(path, false)"));

                cases.Add(OpenWithNewInstance(
                    "D-LOW",
                    "AISLADO: msoAutomationSecurityLow (1) SOLO AccessCreated.mdb",
                    accessCreated,
                    "Access.Application.11.NewCurrentDatabase",
                    security: ProbeConstants.MsoAutomationSecurityLow,
                    openArgs: [accessCreated, false],
                    openCall: "OpenCurrentDatabase(path, false)"));
            }

            var bestSecurity = ChooseBestSecurity(cases);
            var bestLabel = bestSecurity is null
                ? "ninguna (se usa valor inicial, sin asignar)"
                : SecurityName(bestSecurity.Value);
            warnings.Add($"H3/H4 usan AutomationSecurity={bestLabel}.");

            if (File.Exists(daoCreated))
            {
                cases.Add(OpenWithNewInstance(
                    "H3-DAO",
                    "DaoCreated.mdb con la mejor AutomationSecurity de la matriz",
                    daoCreated,
                    "DAO.DBEngine.36.CreateDatabase",
                    security: bestSecurity,
                    openArgs: [daoCreated, false],
                    openCall: "OpenCurrentDatabase(path, false)"));
            }

            if (File.Exists(accessCreated))
            {
                cases.Add(OpenWithNewInstance(
                    "H3-ACC",
                    "AccessCreated.mdb con la mejor AutomationSecurity de la matriz",
                    accessCreated,
                    "Access.Application.11.NewCurrentDatabase",
                    security: bestSecurity,
                    openArgs: [accessCreated, false],
                    openCall: "OpenCurrentDatabase(path, false)"));

                cases.Add(OpenWithNewInstance(
                    "H4-1",
                    "OpenCurrentDatabase(path) — un argumento",
                    accessCreated,
                    "Access.Application.11.NewCurrentDatabase",
                    security: bestSecurity,
                    openArgs: [accessCreated],
                    openCall: "OpenCurrentDatabase(path)"));

                cases.Add(OpenWithNewInstance(
                    "H4-2",
                    "OpenCurrentDatabase(path, false)",
                    accessCreated,
                    "Access.Application.11.NewCurrentDatabase",
                    security: bestSecurity,
                    openArgs: [accessCreated, false],
                    openCall: "OpenCurrentDatabase(path, false)"));

                cases.Add(OpenWithNewInstance(
                    "H4-3",
                    "OpenCurrentDatabase(path, true) Exclusive",
                    accessCreated,
                    "Access.Application.11.NewCurrentDatabase",
                    security: bestSecurity,
                    openArgs: [accessCreated, true],
                    openCall: "OpenCurrentDatabase(path, true)"));

                cases.Add(OpenWithNewInstance(
                    "H4-4",
                    "OpenCurrentDatabase(path, false, \"\") password vacío",
                    accessCreated,
                    "Access.Application.11.NewCurrentDatabase",
                    security: bestSecurity,
                    openArgs: [accessCreated, false, string.Empty],
                    openCall: "OpenCurrentDatabase(path, false, \"\")"));
            }
        }
        catch (Exception exception)
        {
            warnings.Add("Fase 1B abortada: " + ProbeUtil.Describe(exception).Message);
            Console.Error.WriteLine(exception);
        }
        finally
        {
            ProbeUtil.WaitUntilAccessGone(beforeAll);
            orphan = ProbeUtil.CurrentAccessPids().Except(beforeAll).Any();
            tempsDeleted = ProbeUtil.TryDeleteDirectory(tempDir, warnings);
        }

        PrintReport(
            cases,
            accessCreated,
            daoCreated,
            newCurrentDatabaseWorked,
            newCurrentProjectName,
            newCurrentProjectFullName,
            newCurrentDbName,
            warnings,
            orphan,
            tempsDeleted);
        return orphan ? 2 : 0;
    }

    private static void ParseCreateNotes(
        OpenCaseResult created,
        out string? projectName,
        out string? projectFullName,
        out string? dbName)
    {
        projectName = null;
        projectFullName = null;
        dbName = null;
        if (string.IsNullOrWhiteSpace(created.Notes))
        {
            return;
        }

        foreach (var line in created.Notes.Split('|', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Name=", StringComparison.Ordinal))
            {
                projectName = line[5..];
            }
            else if (line.StartsWith("FullName=", StringComparison.Ordinal))
            {
                projectFullName = line[9..];
            }
            else if (line.StartsWith("CurrentDb=", StringComparison.Ordinal))
            {
                dbName = line[10..];
            }
        }
    }

    private static OpenCaseResult CreateAccessDatabase(
        string path,
        List<OpenCaseResult> cases,
        List<string> warnings)
    {
        var result = new OpenCaseResult
        {
            CaseId = "H2-NEW",
            Title = "NewCurrentDatabase crea y deja abierta la base actual",
            FileName = Path.GetFileName(path),
            CreatedBy = "Access.Application.11.NewCurrentDatabase",
            OpenCall = "NewCurrentDatabase(path)",
            SecurityRequested = "(no tocar)"
        };

        var before = ProbeUtil.CurrentAccessPids();
        using var lifetime = new ComLifetime();
        object? app = null;
        var capture = new DialogCapture();
        var clock = Stopwatch.StartNew();
        try
        {
            app = lifetime.Track(Com.Create(ProbeConstants.AccessProgId));
            var pid = ProbeUtil.CurrentAccessPids().Except(before).FirstOrDefault();
            result.Pid = pid == 0 ? null : pid;
            result.AccessVersion = Com.GetString(app, "Version");
            result.AccessPath = ProbeUtil.TryGetProcessPath(result.Pid);
            if (!ProbeUtil.IsAccess2003(result.AccessVersion, result.AccessPath))
            {
                result.Message =
                    $"No es Access 2003: Version={result.AccessVersion} Path={result.AccessPath}";
                warnings.Add(result.Message);
                return result;
            }

            result.SecurityBefore = ReadSecurity(app);
            using (new DialogGuard(result.Pid ?? 0, capture))
            {
                try
                {
                    Com.Call(app, "NewCurrentDatabase", path);
                }
                catch (Exception first)
                {
                    result.Message = "NewCurrentDatabase(path) falló: " + ProbeUtil.Describe(first).Message;
                    Com.Call(app, "NewCurrentDatabase", path, ProbeConstants.AcFileFormatAccess2000);
                    result.OpenCall = "NewCurrentDatabase(path, acFileFormatAccess2000)";
                }

                result.Opened = File.Exists(path) && new FileInfo(path).Length > 0;
                result.SecurityAfter = ReadSecurity(app);
                var projectName = TryReadProject(app, lifetime, "Name");
                var projectFull = TryReadProject(app, lifetime, "FullName");
                var dbName = TryReadCurrentDbName(app, lifetime);
                result.Notes =
                    "Name=" + (projectName ?? "(n/d)") +
                    " | FullName=" + (projectFull ?? "(n/d)") +
                    " | CurrentDb=" + (dbName ?? "(n/d)");
                if (result.Opened && string.IsNullOrWhiteSpace(result.Message))
                {
                    result.Message = "NewCurrentDatabase OK; la instancia tiene base actual.";
                }
                else if (!result.Opened)
                {
                    result.Message = (result.Message ?? "") + " NewCurrentDatabase no creó el archivo.";
                }
            }
        }
        catch (Exception exception)
        {
            ApplyException(result, exception);
        }
        finally
        {
            result.DialogDetected = capture.Detected;
            result.DialogTitle = capture.Title;
            result.DurationMs = clock.ElapsedMilliseconds;
            ProbeUtil.TryCall(app, "CloseCurrentDatabase");
            ProbeUtil.TryCall(app, "Quit", ProbeConstants.AcQuitSaveNone);
            ProbeUtil.WaitUntilAccessGone(before);
        }

        cases.Add(result);
        return result;
    }

    private static void CreateDaoDatabase(string path, List<string> warnings)
    {
        using var lifetime = new ComLifetime();
        object? engine = null;
        object? database = null;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            engine = lifetime.Track(Com.Create(ProbeConstants.DaoProgId));
            database = lifetime.Track(
                Com.Call(engine, "CreateDatabase", path, ";LANGID=0x0409;CP=1252;COUNTRY=0")!);
        }
        catch (Exception exception)
        {
            warnings.Add("No se pudo crear DaoCreated.mdb: " + ProbeUtil.Describe(exception).Message);
        }
        finally
        {
            ProbeUtil.TryCall(database, "Close");
            ProbeUtil.TryCall(engine, "Idle", 8);
        }
    }

    private static OpenCaseResult OpenWithNewInstance(
        string caseId,
        string title,
        string path,
        string createdBy,
        int? security,
        object[] openArgs,
        string openCall)
    {
        var result = new OpenCaseResult
        {
            CaseId = caseId,
            Title = title,
            FileName = Path.GetFileName(path),
            CreatedBy = createdBy,
            OpenCall = openCall,
            SecurityRequested = security is null ? "(no tocar)" : SecurityName(security.Value)
        };

        if (!File.Exists(path))
        {
            result.Message = "El archivo no existe.";
            return result;
        }

        ProbeUtil.EnsureSafeLocalMdbPath(path, result.FileName);
        ProbeUtil.WaitUntilFileUnlocked(path);

        var before = ProbeUtil.CurrentAccessPids();
        using var lifetime = new ComLifetime();
        object? app = null;
        var capture = new DialogCapture();
        var opened = false;
        var clock = Stopwatch.StartNew();
        try
        {
            app = lifetime.Track(Com.Create(ProbeConstants.AccessProgId));
            var pid = ProbeUtil.CurrentAccessPids().Except(before).FirstOrDefault();
            result.Pid = pid == 0 ? null : pid;
            result.AccessVersion = Com.GetString(app, "Version");
            result.AccessPath = ProbeUtil.TryGetProcessPath(result.Pid);
            if (!ProbeUtil.IsAccess2003(result.AccessVersion, result.AccessPath))
            {
                result.Message =
                    $"No es Access 2003: Version={result.AccessVersion} Path={result.AccessPath}";
                return result;
            }

            result.SecurityBefore = ReadSecurity(app);
            if (security is not null)
            {
                Com.Set(app, "AutomationSecurity", security.Value);
            }

            result.SecurityAfter = ReadSecurity(app);
            using (new DialogGuard(result.Pid ?? 0, capture))
            {
                Com.Call(app, "OpenCurrentDatabase", openArgs);
                opened = true;
                result.Opened = true;
                result.Message = "OpenCurrentDatabase OK.";
                var fullName = TryReadProject(app, lifetime, "FullName");
                result.Notes = "CurrentProject.FullName=" + (fullName ?? "(n/d)");
            }
        }
        catch (Exception exception)
        {
            ApplyException(result, exception);
            result.Opened = false;
        }
        finally
        {
            result.DialogDetected = capture.Detected;
            result.DialogTitle = capture.Title;
            result.DurationMs = clock.ElapsedMilliseconds;
            if (opened)
            {
                ProbeUtil.TryCall(app, "CloseCurrentDatabase");
            }

            ProbeUtil.TryCall(app, "Quit", ProbeConstants.AcQuitSaveNone);
            ProbeUtil.WaitUntilAccessGone(before);
        }

        return result;
    }

    private static int? ChooseBestSecurity(List<OpenCaseResult> cases)
    {
        foreach (var id in new[] { "A", "B", "C" })
        {
            if (cases.Find(item => item.CaseId == id)?.Opened == true)
            {
                return id switch
                {
                    "A" => null,
                    "B" => ProbeConstants.MsoAutomationSecurityByUI,
                    "C" => ProbeConstants.MsoAutomationSecurityForceDisable,
                    _ => null
                };
            }
        }

        if (cases.Find(item => item.CaseId == "D-LOW")?.Opened == true)
        {
            return ProbeConstants.MsoAutomationSecurityLow;
        }

        return null;
    }

    private static string? ReadSecurity(object app)
    {
        try
        {
            var value = Com.TryGet(app, "AutomationSecurity");
            if (value is null)
            {
                return "(no se pudo leer)";
            }

            var number = Convert.ToInt32(value);
            return $"{number} {SecurityName(number)}";
        }
        catch (Exception exception)
        {
            return "(error: " + ProbeUtil.Describe(exception).Message + ")";
        }
    }

    private static string SecurityName(int value) => value switch
    {
        ProbeConstants.MsoAutomationSecurityLow => "msoAutomationSecurityLow",
        ProbeConstants.MsoAutomationSecurityByUI => "msoAutomationSecurityByUI",
        ProbeConstants.MsoAutomationSecurityForceDisable => "msoAutomationSecurityForceDisable",
        _ => "(desconocido)"
    };

    private static string? TryReadProject(object app, ComLifetime lifetime, string property)
    {
        try
        {
            var project = lifetime.Track(Com.Get(app, "CurrentProject"));
            return Com.GetString(project, property);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadCurrentDbName(object app, ComLifetime lifetime)
    {
        try
        {
            var db = lifetime.Track(Com.Call(app, "CurrentDb")!);
            return Com.GetString(db, "Name");
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyException(OpenCaseResult result, Exception exception)
    {
        var info = ProbeUtil.Describe(exception);
        result.HResultHex = info.HResultHex;
        result.AccessError = info.AccessError;
        result.Message = info.Message;
        result.Source = info.Source;
        result.InnerException = info.InnerException;
    }

    private static void PrintReport(
        List<OpenCaseResult> cases,
        string? accessCreated,
        string? daoCreated,
        bool newCurrentDatabaseWorked,
        string? projectName,
        string? projectFullName,
        string? dbName,
        List<string> warnings,
        bool orphan,
        bool tempsDeleted)
    {
        Console.WriteLine("====================================================");
        Console.WriteLine("FASE 1B — AISLAR OpenCurrentDatabase 0x800A1EBA / 7866");
        Console.WriteLine("====================================================");
        Console.WriteLine($"ProgID: {ProbeConstants.AccessProgId}");
        Console.WriteLine($"IntPtr.Size={IntPtr.Size}");
        Console.WriteLine($"AccessCreated: {accessCreated}");
        Console.WriteLine($"DaoCreated:    {daoCreated}");
        Console.WriteLine();
        Console.WriteLine("--- Hipótesis 2: NewCurrentDatabase ---");
        Console.WriteLine($"NewCurrentDatabase: {(newCurrentDatabaseWorked ? "sí" : "no")}");
        Console.WriteLine($"CurrentProject.Name:     {projectName ?? "(n/d)"}");
        Console.WriteLine($"CurrentProject.FullName: {projectFullName ?? "(n/d)"}");
        Console.WriteLine($"CurrentDb.Name:          {dbName ?? "(n/d)"}");
        Console.WriteLine();

        foreach (var item in cases)
        {
            Console.WriteLine($"--- Caso {item.CaseId}: {item.Title} ---");
            Console.WriteLine($"Archivo: {item.FileName}  creado por: {item.CreatedBy}");
            Console.WriteLine($"Llamada: {item.OpenCall}");
            Console.WriteLine($"AutomationSecurity antes:     {item.SecurityBefore ?? "(n/d)"}");
            Console.WriteLine($"AutomationSecurity solicitado: {item.SecurityRequested ?? "(n/d)"}");
            Console.WriteLine($"AutomationSecurity después:    {item.SecurityAfter ?? "(n/d)"}");
            Console.WriteLine($"OpenCurrentDatabase: {(item.Opened ? "OK" : "Error")}");
            Console.WriteLine($"HRESULT: {item.HResultHex ?? "(n/d)"}");
            Console.WriteLine($"Error Access: {item.AccessError?.ToString() ?? "(n/d)"}");
            Console.WriteLine($"Message: {item.Message}");
            Console.WriteLine($"Source: {item.Source ?? "(n/d)"}");
            Console.WriteLine($"InnerException: {item.InnerException ?? "(n/d)"}");
            Console.WriteLine($"Diálogo: {(item.DialogDetected ? "sí — " + item.DialogTitle : "no")}");
            Console.WriteLine($"PID: {item.Pid}  Version: {item.AccessVersion}  Path: {item.AccessPath}");
            Console.WriteLine($"Duración: {item.DurationMs} ms");
            if (!string.IsNullOrWhiteSpace(item.Notes))
            {
                Console.WriteLine($"Notas: {item.Notes}");
            }

            Console.WriteLine();
        }

        var h3Dao = cases.Find(item => item.CaseId == "H3-DAO");
        var h3Acc = cases.Find(item => item.CaseId == "H3-ACC");
        Console.WriteLine("--- Hipótesis 3: origen del archivo ---");
        Console.WriteLine("| Archivo           | Creado mediante | OpenCurrentDatabase |");
        Console.WriteLine("| ----------------- | --------------- | ------------------- |");
        Console.WriteLine(
            $"| DaoCreated.mdb    | DAO 3.6         | {OkOrError(h3Dao)} |");
        Console.WriteLine(
            $"| AccessCreated.mdb | Access 11       | {OkOrError(h3Acc)} |");
        Console.WriteLine();
        Console.WriteLine("--- Conclusión preliminar ---");
        Console.WriteLine(BuildConclusion(cases, newCurrentDatabaseWorked));
        Console.WriteLine($"MSACCESS huérfano: {orphan}");
        Console.WriteLine($"Temporales eliminados: {(tempsDeleted ? "sí" : "no")}");
        foreach (var warning in warnings)
        {
            Console.WriteLine("WARNING: " + warning);
        }

        Console.WriteLine("====================================================");
    }

    private static string OkOrError(OpenCaseResult? result)
    {
        if (result is null)
        {
            return "no ejecutado";
        }

        return result.Opened ? "OK" : $"Error {result.HResultHex ?? result.Message}";
    }

    private static string BuildConclusion(List<OpenCaseResult> cases, bool newCurrentWorked)
    {
        var matrixOk = cases.Where(item => item.CaseId is "A" or "B" or "C").Any(item => item.Opened);
        var lowOk = cases.Find(item => item.CaseId == "D-LOW")?.Opened == true;
        var reopenOk = cases.Where(item => item.CaseId.StartsWith("H4", StringComparison.Ordinal) || item.CaseId is "A" or "H3-ACC")
            .Any(item => item.Opened);
        var daoOk = cases.Find(item => item.CaseId == "H3-DAO")?.Opened == true;
        var accOk = cases.Find(item => item.CaseId == "H3-ACC")?.Opened == true;
        var forceDisableFails = cases.Find(item => item.CaseId == "C") is { Opened: false };
        var defaultFails = cases.Find(item => item.CaseId == "A") is { Opened: false };
        var byUiOk = cases.Find(item => item.CaseId == "B")?.Opened == true;

        if (newCurrentWorked && !reopenOk)
        {
            return "Access Automation SÍ puede tener una base actual (NewCurrentDatabase), pero OpenCurrentDatabase no puede reabrir el mismo archivo.";
        }

        if (!newCurrentWorked && !reopenOk)
        {
            return "Access Automation no logra ni crear/usar una base actual ni OpenCurrentDatabase. El canal de apertura está roto o bloqueado.";
        }

        if (forceDisableFails && (byUiOk || matrixOk || lowOk) && defaultFails)
        {
            return "msoAutomationSecurityForceDisable es un factor: otras políticas sí permiten abrir.";
        }

        if (forceDisableFails && (byUiOk || lowOk))
        {
            return "ForceDisable impide abrir; otra AutomationSecurity sí funciona. No usar ForceDisable como predeterminado en Access 2003.";
        }

        if (daoOk != accOk)
        {
            return daoOk
                ? "OpenCurrentDatabase abre el MDB creado por DAO, no el creado por Access."
                : "OpenCurrentDatabase abre el MDB creado por Access, no el creado por DAO.";
        }

        if (reopenOk)
        {
            return "OpenCurrentDatabase funciona al menos en un caso de la matriz. Ver el caso OK.";
        }

        return "Ningún caso de OpenCurrentDatabase abrió. Ver HRESULT por caso.";
    }
}
