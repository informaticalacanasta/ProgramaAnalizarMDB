using System.Globalization;
using System.Reflection;
using MdbToSql.AccessApplication.Analysis;
using MdbToSql.AccessApplication.Com;
using MdbToSql.AccessApplication.Dao;
using MdbToSql.AccessApplication.Workspace;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication;

public sealed class AccessVentasAnalysisProbe
{
    public const string FormName = "ventas";
    public const string ApiUsed =
        "Access.Form Design View + Form.Module.Lines + DAO QueryDef.SQL (sin ejecutar; VBE no utilizado)";

    public Task<AccessVentasAnalysisResult> RunAsync(
        string mdbPath,
        string jsonPath,
        CancellationToken cancellationToken = default)
    {
        return StaRunner.Run(() => Run(mdbPath, jsonPath, cancellationToken), cancellationToken);
    }

    private static AccessVentasAnalysisResult Run(
        string mdbPath,
        string jsonPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IntPtr.Size != 4)
        {
            throw new AccessApplicationAnalysisException(
                $"El análisis de ventas requiere x86. IntPtr.Size={IntPtr.Size}.");
        }

        AnalysisWorkspace.EnsureLocal(mdbPath, "MDB original");
        var before = FileIntegrity.Capture(mdbPath);
        var warnings = new List<string>();
        var errors = new List<string>();
        var beforePids = AccessProcessTracker.CurrentPids();
        AnalysisWorkspace? workspace = null;
        AccessFormAnalysis? form = null;
        string? moduleName = null;
        int? lineCount = null;
        string? source = null;
        IReadOnlyList<AccessVbaProcedure> procedures = [];
        IReadOnlyList<AccessEventProcedureBinding> bindings = [];
        IReadOnlyList<AccessVbaReference> references = [];
        IReadOnlyList<AccessQueryUse> queryDefs = [];
        IReadOnlyList<AccessTableUse> tables = [];
        IReadOnlyList<AccessObjectUse> objectUses = [];
        IReadOnlyList<string> pending = [];
        var opened = false;
        var closedCleanly = false;
        try
        {
            workspace = AnalysisWorkspace.CreateFrom(mdbPath);
            IReadOnlyList<AccessTableReference> tableCatalog;
            IReadOnlyList<AccessQueryAnalysis> queryCatalog;
            IReadOnlyList<string> formNames;
            using (var dao = DaoSession.OpenExclusive(workspace.AnalysisSafePath))
            {
                dao.EnsureAllowBypassKey();
                tableCatalog = dao.ReadTables(warnings);
                queryCatalog = dao.ReadQueries(warnings);
                formNames = dao.ReadDocumentNames("Forms");
            }

            if (!formNames.Contains(FormName, StringComparer.OrdinalIgnoreCase))
            {
                throw new AccessApplicationAnalysisException(
                    "DAO no enumeró el formulario 'ventas'. No se abre ningún otro form.");
            }

            if (!AnalysisWorkspace.WaitUntilUnlocked(workspace.AnalysisSafePath))
            {
                throw new AccessApplicationAnalysisException(
                    "analysis-safe.mdb sigue bloqueada; no se llama a OpenCurrentDatabase.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var runtime = SafeAccessRuntime.Open(workspace.AnalysisSafePath, beforePids);
            var inspected = InspectVentas(runtime, tableCatalog, queryCatalog, formNames, warnings, errors);
            form = inspected.Form;
            moduleName = inspected.ModuleName;
            lineCount = inspected.LineCount;
            source = inspected.Source;
            procedures = inspected.Procedures;
            bindings = inspected.Bindings;
            references = inspected.References;
            queryDefs = inspected.QueryDefs;
            tables = inspected.Tables;
            objectUses = inspected.ObjectUses;
            pending = inspected.Pending;
            opened = inspected.OpenedInDesignView;
            closedCleanly = inspected.ClosedCleanly;
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

        return new AccessVentasAnalysisResult(
            FormName,
            ApiUsed,
            form,
            moduleName,
            lineCount,
            source,
            procedures,
            bindings,
            references,
            queryDefs,
            tables,
            objectUses,
            pending,
            opened,
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

    private static VentasInspection InspectVentas(
        SafeAccessRuntime runtime,
        IReadOnlyList<AccessTableReference> tables,
        IReadOnlyList<AccessQueryAnalysis> queries,
        IReadOnlyList<string> formNames,
        List<string> warnings,
        List<string> errors)
    {
        var reader = new ComPropertyReader();
        var formAnalyzer = new AccessFormAnalyzer(
            runtime.Lifetime,
            runtime.App,
            runtime.Dialogs,
            tables,
            queries,
            formNames);
        var doCmd = runtime.Lifetime.Track(ComInterop.Get(runtime.App, "DoCmd"));
        var opened = false;
        var closed = false;
        try
        {
            ComInterop.Call(
                doCmd,
                "OpenForm",
                FormName,
                AccessConstants.AcViewDesign,
                Missing.Value,
                Missing.Value,
                Missing.Value,
                AccessConstants.AcHidden);
            opened = true;

            var dialogs = runtime.Dialogs.ConsumeDetected();
            if (dialogs.Count > 0)
            {
                errors.Add("Diálogo modal al abrir ventas: " + string.Join("; ", dialogs));
                closed = TryClose(doCmd, runtime);
                return VentasInspection.Aborted(opened, closed);
            }

            var loadedForms = runtime.LoadedNames("Forms");
            var loadedReports = runtime.LoadedNames("Reports");
            var unexpectedForms = loadedForms
                .Where(name => !string.Equals(name, FormName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (unexpectedForms.Count > 0 || loadedReports.Count > 0)
            {
                errors.Add(
                    "Objetos inesperados al abrir ventas. " +
                    $"Forms=[{string.Join(", ", unexpectedForms)}] Reports=[{string.Join(", ", loadedReports)}]");
                closed = TryClose(doCmd, runtime);
                return VentasInspection.Aborted(opened, closed);
            }

            var forms = runtime.Lifetime.Track(ComInterop.Get(runtime.App, "Forms"));
            var formObject = runtime.Lifetime.Track(ComInterop.Item(forms, FormName));
            var formWarnings = new List<string>();
            var form = formAnalyzer.ReadOpened(formObject, FormName, formWarnings);
            warnings.AddRange(formWarnings);

            string? moduleName = null;
            int? lineCount = null;
            string? source = null;
            IReadOnlyList<AccessVbaProcedure> procedures = [];
            if (form.HasModule == true)
            {
                var moduleRead = TryReadModule(runtime, reader, formObject, warnings, errors);
                moduleName = moduleRead.Name;
                lineCount = moduleRead.LineCount;
                source = moduleRead.Source;
                procedures = AccessVbaTextAnalyzer.ParseProcedures(source);
            }
            else if (form.HasModule == false)
            {
                warnings.Add("ventas.HasModule=false; no se accede a Form.Module para no crear un módulo.");
            }
            else
            {
                warnings.Add("ventas.HasModule no está disponible; no se accede a Form.Module.");
            }

            var bindings = BindEvents(form, procedures);
            var vbaReferences = AccessVbaTextAnalyzer.ParseReferences(source, procedures).ToList();
            foreach (var procedure in procedures)
            {
                vbaReferences.AddRange(
                    AccessVbaTextAnalyzer.ParseFacts(source, procedure.StartLine, procedure.EndLine));
            }

            var resolved = AccessDirectReferenceResolver.Resolve(form, vbaReferences, queries, tables);
            closed = TryClose(doCmd, runtime);
            if (!closed)
            {
                errors.Add("No se pudo cerrar ventas con acSaveNo.");
            }

            var leftoverForms = runtime.LoadedNames("Forms");
            var leftoverReports = runtime.LoadedNames("Reports");
            if (leftoverForms.Count > 0 || leftoverReports.Count > 0)
            {
                errors.Add(
                    $"Tras cerrar seguían cargados Forms=[{string.Join(", ", leftoverForms)}] " +
                    $"Reports=[{string.Join(", ", leftoverReports)}]");
            }

            return new VentasInspection(
                form with
                {
                    ClosedCleanly = closed
                        && leftoverForms.Count == 0
                        && leftoverReports.Count == 0
                },
                moduleName,
                lineCount,
                source,
                procedures,
                bindings,
                vbaReferences,
                resolved.Queries,
                resolved.Tables,
                resolved.Uses,
                resolved.Pending,
                opened,
                closed
                    && leftoverForms.Count == 0
                    && leftoverReports.Count == 0);
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            if (opened)
            {
                closed = TryClose(doCmd, runtime);
            }

            return VentasInspection.Aborted(opened, closed);
        }
    }

    private static (string? Name, int? LineCount, string? Source) TryReadModule(
        SafeAccessRuntime runtime,
        ComPropertyReader reader,
        object form,
        List<string> warnings,
        List<string> errors)
    {
        var module = reader.ReadObject(form, "Module", warnings);
        if (module is null)
        {
            errors.Add("HasModule=true pero Form.Module no está disponible. No se usa VBE ni se cambia la seguridad.");
            return (null, null, null);
        }

        runtime.Lifetime.Track(module);
        var name = reader.ReadString(module, "Name", warnings);
        var count = reader.ReadInt(module, "CountOfLines", warnings);
        if (count is null)
        {
            errors.Add("No se pudo leer Module.CountOfLines.");
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
            errors.Add("Module.Lines bloqueado o falló: " + exception.Message + " No se usa VBE ni se cambia la seguridad.");
            return (name, count, null);
        }
    }

    private static List<AccessEventProcedureBinding> BindEvents(
        AccessFormAnalysis form,
        IReadOnlyList<AccessVbaProcedure> procedures)
    {
        var bindings = new List<AccessEventProcedureBinding>();
        foreach (var binding in form.Events)
        {
            bindings.Add(AccessEventProcedureNames.Bind(
                binding.ObjectName,
                AccessObjectKind.Form,
                binding.EventName,
                binding.Expression,
                eventProcPrefix: null,
                FormName,
                procedures));
        }

        foreach (var control in form.Controls)
        {
            foreach (var binding in control.Events)
            {
                bindings.Add(AccessEventProcedureNames.Bind(
                    control.Name,
                    AccessObjectKind.Control,
                    binding.EventName,
                    binding.Expression,
                    eventProcPrefix: null,
                    FormName,
                    procedures));
            }
        }

        return bindings;
    }

    private static bool TryClose(object doCmd, SafeAccessRuntime runtime)
    {
        try
        {
            ComInterop.Call(doCmd, "Close", AccessConstants.AcForm, FormName, AccessConstants.AcSaveNo);
            return !runtime.LoadedNames("Forms").Contains(FormName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed record VentasInspection(
        AccessFormAnalysis? Form,
        string? ModuleName,
        int? LineCount,
        string? Source,
        IReadOnlyList<AccessVbaProcedure> Procedures,
        IReadOnlyList<AccessEventProcedureBinding> Bindings,
        IReadOnlyList<AccessVbaReference> References,
        IReadOnlyList<AccessQueryUse> QueryDefs,
        IReadOnlyList<AccessTableUse> Tables,
        IReadOnlyList<AccessObjectUse> ObjectUses,
        IReadOnlyList<string> Pending,
        bool OpenedInDesignView,
        bool ClosedCleanly)
    {
        public static VentasInspection Aborted(bool opened, bool closed)
        {
            return new VentasInspection(null, null, null, null, [], [], [], [], [], [], [], opened, closed);
        }
    }
}
