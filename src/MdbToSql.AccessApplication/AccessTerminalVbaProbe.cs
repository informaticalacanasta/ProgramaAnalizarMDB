using System.Globalization;
using MdbToSql.AccessApplication.Com;
using MdbToSql.AccessApplication.Dao;
using MdbToSql.AccessApplication.Workspace;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication;

public sealed class AccessTerminalVbaProbe
{
    public const string ProcedureName = "terminal";
    public const string ApiUsed =
        "DoCmd.OpenModule + Application.Modules.Lines (object model Access 2003; VBE no utilizado)";

    public Task<AccessTerminalVbaProbeResult> RunAsync(
        string mdbPath,
        string jsonPath,
        CancellationToken cancellationToken = default)
    {
        return StaRunner.Run(() => Run(mdbPath, jsonPath, cancellationToken), cancellationToken);
    }

    private static AccessTerminalVbaProbeResult Run(
        string mdbPath,
        string jsonPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IntPtr.Size != 4)
        {
            throw new AccessApplicationAnalysisException(
                $"El probe VBA de terminal requiere x86. IntPtr.Size={IntPtr.Size}.");
        }

        AnalysisWorkspace.EnsureLocal(mdbPath, "MDB original");
        var before = FileIntegrity.Capture(mdbPath);
        var warnings = new List<string>();
        var errors = new List<string>();
        var beforePids = AccessProcessTracker.CurrentPids();
        AnalysisWorkspace? workspace = null;
        IReadOnlyList<string> modulesSearched = [];
        IReadOnlyList<AccessProcedureDefinition> definitions = [];
        IReadOnlyList<AccessVbaReference> references = [];
        string? moduleDeclarations = null;
        var closedCleanly = false;
        try
        {
            workspace = AnalysisWorkspace.CreateFrom(mdbPath);
            using (var dao = DaoSession.OpenExclusive(workspace.AnalysisSafePath))
            {
                dao.EnsureAllowBypassKey();
                modulesSearched = dao.ReadDocumentNames("Modules");
            }

            if (!AnalysisWorkspace.WaitUntilUnlocked(workspace.AnalysisSafePath))
            {
                throw new AccessApplicationAnalysisException(
                    "analysis-safe.mdb sigue bloqueada; no se llama a OpenCurrentDatabase.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var runtime = SafeAccessRuntime.Open(workspace.AnalysisSafePath, beforePids);
            var inspection = InspectModules(runtime, modulesSearched, warnings, errors);
            modulesSearched = inspection.ModulesSearched;
            definitions = inspection.Definitions;
            references = inspection.References;
            moduleDeclarations = inspection.ModuleDeclarations;
            closedCleanly = inspection.ClosedCleanly;

            if (definitions.Count == 0 && !errors.Exists(item => item.Contains("terminal", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(
                    "No hay declaración de procedimiento 'terminal' en los módulos estándar: " +
                    (modulesSearched.Count == 0
                        ? "(ninguno enumerado)"
                        : string.Join(", ", modulesSearched)) +
                    ". No se abrieron Forms ni Reports para ampliar la búsqueda.");
            }
            else if (definitions.Count > 1)
            {
                warnings.Add(
                    "Ambigüedad: hay " + definitions.Count +
                    " declaraciones llamadas 'terminal': " +
                    string.Join("; ", definitions.Select(item =>
                        item.ModuleName + " " + item.Kind + " líneas " + item.StartLine + "-" + item.EndLine)));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add(exception.Message);
        }
        finally
        {
            AccessProcessTracker.WaitUntilGone(beforePids);
            if (workspace is not null && !workspace.TryDelete(out var cleanupError) && cleanupError is not null)
            {
                errors.Add("No se eliminó el temporal: " + cleanupError);
            }

            workspace?.Dispose();
        }

        var after = FileIntegrity.Capture(mdbPath);
        var verified = FileIntegrity.EqualsSnapshot(before, after);
        if (!verified)
        {
            errors.Add("ERROR CRÍTICO: el MDB original cambió durante el análisis.");
        }

        if (definitions.Count == 1)
        {
            moduleDeclarations = definitions[0].ModuleDeclarations;
        }

        return new AccessTerminalVbaProbeResult(
            modulesSearched,
            definitions,
            moduleDeclarations,
            references,
            ApiUsed,
            closedCleanly,
            before,
            after,
            verified,
            workspace is null || !Directory.Exists(workspace.DirectoryPath),
            !AccessProcessTracker.CurrentPids().Except(beforePids).Any(),
            jsonPath,
            warnings,
            errors);
    }

    private static ModuleInspection InspectModules(
        SafeAccessRuntime runtime,
        IReadOnlyList<string> moduleNames,
        List<string> warnings,
        List<string> errors)
    {
        var reader = new ComPropertyReader();
        var doCmd = runtime.Lifetime.Track(ComInterop.Get(runtime.App, "DoCmd"));
        var searched = new List<string>();
        var definitions = new List<AccessProcedureDefinition>();
        var references = new List<AccessVbaReference>();
        var allClosed = true;
        if (moduleNames.Count == 0)
        {
            errors.Add("DAO no enumeró módulos estándar. No se abre ningún Form ni Report.");
            return new ModuleInspection([], [], [], null, true);
        }

        foreach (var moduleName in moduleNames)
        {
            searched.Add(moduleName);
            var opened = false;
            var closed = false;
            try
            {
                ComInterop.Call(doCmd, "OpenModule", moduleName);
                opened = true;
                var dialogs = runtime.Dialogs.ConsumeDetected();
                if (dialogs.Count > 0)
                {
                    errors.Add(
                        "Diálogo modal al abrir el módulo '" + moduleName + "': " +
                        string.Join("; ", dialogs) +
                        ". No se pulsó ningún botón ni se cambia la seguridad.");
                    closed = TryCloseModule(doCmd, runtime, moduleName);
                    allClosed &= closed;
                    break;
                }

                var loadedForms = runtime.LoadedNames("Forms");
                var loadedReports = runtime.LoadedNames("Reports");
                if (loadedForms.Count > 0 || loadedReports.Count > 0)
                {
                    errors.Add(
                        "Objetos inesperados al abrir el módulo '" + moduleName + "'. " +
                        "Forms=[" + string.Join(", ", loadedForms) + "] Reports=[" +
                        string.Join(", ", loadedReports) + "]. No se continúa la búsqueda.");
                    closed = TryCloseModule(doCmd, runtime, moduleName);
                    allClosed &= closed;
                    break;
                }

                var sourceRead = TryReadStandardModule(runtime, reader, moduleName, warnings, errors);
                if (sourceRead.Source is not null)
                {
                    var procedures = AccessVbaTextAnalyzer.ParseProcedures(sourceRead.Source);
                    var hits = AccessVbaTextAnalyzer.FindByName(sourceRead.Source, ProcedureName);
                    var declarations = AccessVbaTextAnalyzer.ExtractDeclarations(sourceRead.Source);
                    var moduleReferences = AccessVbaTextAnalyzer.ParseReferences(sourceRead.Source, procedures);
                    foreach (var hit in hits)
                    {
                        var procedureSource = AccessVbaTextAnalyzer.ExtractRange(
                            sourceRead.Source,
                            hit.StartLine,
                            hit.EndLine);
                        definitions.Add(new AccessProcedureDefinition(
                            moduleName,
                            hit.Kind,
                            hit.Name,
                            AccessVbaTextAnalyzer.Signature(sourceRead.Source, hit),
                            hit.StartLine,
                            hit.EndLine,
                            procedureSource,
                            declarations));
                        references.AddRange(AccessVbaTextAnalyzer.InRange(moduleReferences, hit.StartLine, hit.EndLine));
                        references.AddRange(
                            AccessVbaTextAnalyzer.ParseFacts(sourceRead.Source, hit.StartLine, hit.EndLine));
                    }
                }
            }
            catch (Exception exception)
            {
                errors.Add(
                    "Módulo '" + moduleName + "': lectura bloqueada o falló: " +
                    exception.Message +
                    " No se usa VBE ni se cambia la seguridad.");
            }
            finally
            {
                if (opened)
                {
                    closed = TryCloseModule(doCmd, runtime, moduleName);
                    if (!closed)
                    {
                        errors.Add("No se pudo cerrar el módulo '" + moduleName + "' con acSaveNo.");
                    }

                    allClosed &= closed;
                }
            }
        }

        var leftoverForms = runtime.LoadedNames("Forms");
        var leftoverReports = runtime.LoadedNames("Reports");
        var leftoverModules = runtime.LoadedNames("Modules");
        if (leftoverForms.Count > 0 || leftoverReports.Count > 0)
        {
            errors.Add(
                "Tras cerrar seguían cargados Forms=[" + string.Join(", ", leftoverForms) +
                "] Reports=[" + string.Join(", ", leftoverReports) + "]");
            allClosed = false;
        }

        if (leftoverModules.Count > 0)
        {
            warnings.Add(
                "Tras Close acModule seguían en Application.Modules: " +
                string.Join(", ", leftoverModules) +
                ". Se cierra la base sin guardar.");
        }

        return new ModuleInspection(
            searched,
            definitions,
            references,
            definitions.Count == 1 ? definitions[0].ModuleDeclarations : null,
            allClosed
                && leftoverForms.Count == 0
                && leftoverReports.Count == 0);
    }

    private static (string? Name, int? LineCount, string? Source) TryReadStandardModule(
        SafeAccessRuntime runtime,
        ComPropertyReader reader,
        string moduleName,
        List<string> warnings,
        List<string> errors)
    {
        object? module = null;
        try
        {
            var modules = runtime.Lifetime.Track(ComInterop.Get(runtime.App, "Modules"));
            module = runtime.Lifetime.Track(ComInterop.Item(modules, moduleName));
        }
        catch (Exception exception)
        {
            errors.Add(
                "Application.Modules('" + moduleName + "') no está disponible: " +
                exception.Message +
                " No se usa VBE ni se cambia la seguridad.");
            return (null, null, null);
        }

        var name = reader.ReadString(module, "Name", warnings) ?? moduleName;
        var count = reader.ReadInt(module, "CountOfLines", warnings);
        if (count is null)
        {
            errors.Add("No se pudo leer Modules('" + moduleName + "').CountOfLines.");
            return (name, null, null);
        }

        if (count <= 0)
        {
            return (name, count, string.Empty);
        }

        try
        {
            var text = ComInterop.GetWithArgs(module, "Lines", 1, count.Value)
                ?? ComInterop.Call(module, "Lines", 1, count.Value);
            return (name, count, Convert.ToString(text, CultureInfo.InvariantCulture));
        }
        catch (Exception exception)
        {
            errors.Add(
                "Modules.Lines bloqueado o falló en '" + moduleName + "': " +
                exception.Message +
                " No se usa VBE ni se cambia la seguridad.");
            return (name, count, null);
        }
    }

    private static bool TryCloseModule(object doCmd, SafeAccessRuntime runtime, string moduleName)
    {
        try
        {
            ComInterop.Call(doCmd, "Close", AccessConstants.AcModule, moduleName, AccessConstants.AcSaveNo);
            return !runtime.LoadedNames("Modules").Contains(moduleName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed record ModuleInspection(
        IReadOnlyList<string> ModulesSearched,
        IReadOnlyList<AccessProcedureDefinition> Definitions,
        IReadOnlyList<AccessVbaReference> References,
        string? ModuleDeclarations,
        bool ClosedCleanly);
}
