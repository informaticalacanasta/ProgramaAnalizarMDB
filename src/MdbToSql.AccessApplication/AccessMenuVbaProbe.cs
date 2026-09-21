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

public sealed class AccessMenuVbaProbe
{
    public const string FormName = "MENU";
    public const string ApiUsed = "Access.Form.Module.Lines (object model Access 2003; VBE no utilizado)";

    public Task<AccessMenuVbaProbeResult> RunAsync(
        string mdbPath,
        string jsonPath,
        CancellationToken cancellationToken = default)
    {
        return StaRunner.Run(() => Run(mdbPath, jsonPath, cancellationToken), cancellationToken);
    }

    private static AccessMenuVbaProbeResult Run(
        string mdbPath,
        string jsonPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IntPtr.Size != 4)
        {
            throw new AccessApplicationAnalysisException(
                $"El probe VBA de MENU requiere x86. IntPtr.Size={IntPtr.Size}.");
        }

        AnalysisWorkspace.EnsureLocal(mdbPath, "MDB original");
        var before = FileIntegrity.Capture(mdbPath);
        var warnings = new List<string>();
        var errors = new List<string>();
        var beforePids = AccessProcessTracker.CurrentPids();
        AnalysisWorkspace? workspace = null;
        bool? hasModule = null;
        string? moduleName = null;
        int? lineCount = null;
        string? source = null;
        IReadOnlyList<AccessVbaProcedure> procedures = [];
        IReadOnlyList<AccessEventProcedureBinding> bindings = [];
        IReadOnlyList<AccessVbaReference> references = [];
        var opened = false;
        var closedCleanly = false;
        try
        {
            workspace = AnalysisWorkspace.CreateFrom(mdbPath);
            using (var dao = DaoSession.OpenExclusive(workspace.AnalysisSafePath))
            {
                dao.EnsureAllowBypassKey();
            }

            if (!AnalysisWorkspace.WaitUntilUnlocked(workspace.AnalysisSafePath))
            {
                throw new AccessApplicationAnalysisException(
                    "analysis-safe.mdb sigue bloqueada; no se llama a OpenCurrentDatabase.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var runtime = SafeAccessRuntime.Open(workspace.AnalysisSafePath, beforePids);
            var inspected = InspectMenu(runtime, warnings, errors);
            hasModule = inspected.HasModule;
            moduleName = inspected.ModuleName;
            lineCount = inspected.LineCount;
            source = inspected.Source;
            procedures = inspected.Procedures;
            bindings = inspected.Bindings;
            references = inspected.References;
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

        var result = new AccessMenuVbaProbeResult(
            FormName,
            ApiUsed,
            hasModule,
            moduleName,
            lineCount,
            source,
            procedures,
            bindings,
            references,
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
        return result;
    }

    private static MenuInspection InspectMenu(
        SafeAccessRuntime runtime,
        List<string> warnings,
        List<string> errors)
    {
        var reader = new ComPropertyReader();
        var eventAnalyzer = new AccessEventAnalyzer(reader);
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
                errors.Add("Diálogo modal al abrir MENU: " + string.Join("; ", dialogs));
                closed = TryClose(doCmd, runtime);
                return MenuInspection.Aborted(opened, closed);
            }

            var loadedForms = runtime.LoadedNames("Forms");
            var loadedReports = runtime.LoadedNames("Reports");
            var unexpectedForms = loadedForms
                .Where(name => !string.Equals(name, FormName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (unexpectedForms.Count > 0 || loadedReports.Count > 0)
            {
                errors.Add(
                    "Objetos inesperados al abrir MENU. " +
                    $"Forms=[{string.Join(", ", unexpectedForms)}] Reports=[{string.Join(", ", loadedReports)}]");
                closed = TryClose(doCmd, runtime);
                return MenuInspection.Aborted(opened, closed);
            }

            var forms = runtime.Lifetime.Track(ComInterop.Get(runtime.App, "Forms"));
            var form = runtime.Lifetime.Track(ComInterop.Item(forms, FormName));
            var hasModule = reader.ReadBool(form, "HasModule", warnings);
            var eventProcPrefix = reader.ReadString(form, "EventProcPrefix", warnings);
            var formEvents = eventAnalyzer.ReadFormEvents(form, FormName, warnings);
            var controls = new AccessControlAnalyzer(
                    runtime.Lifetime,
                    reader,
                    eventAnalyzer,
                    [],
                    [],
                    [FormName])
                .ReadTree(form, FormName, sectionName: null, pageIndex: null, warnings);

            string? moduleName = null;
            int? lineCount = null;
            string? source = null;
            IReadOnlyList<AccessVbaProcedure> procedures = [];
            if (hasModule == true)
            {
                var moduleRead = TryReadModule(runtime, reader, form, warnings, errors);
                moduleName = moduleRead.Name;
                lineCount = moduleRead.LineCount;
                source = moduleRead.Source;
                procedures = AccessVbaTextAnalyzer.ParseProcedures(source);
            }
            else if (hasModule == false)
            {
                warnings.Add("MENU.HasModule=false; no se accede a Form.Module para no crear un módulo.");
            }
            else
            {
                warnings.Add("MENU.HasModule no está disponible; no se accede a Form.Module.");
            }

            var bindings = BindEvents(formEvents, controls, eventProcPrefix, procedures);
            var references = AccessVbaTextAnalyzer.ParseReferences(source, procedures);
            closed = TryClose(doCmd, runtime);
            if (!closed)
            {
                errors.Add("No se pudo cerrar MENU con acSaveNo.");
            }

            var leftoverForms = runtime.LoadedNames("Forms");
            var leftoverReports = runtime.LoadedNames("Reports");
            if (leftoverForms.Count > 0 || leftoverReports.Count > 0)
            {
                errors.Add(
                    $"Tras cerrar seguían cargados Forms=[{string.Join(", ", leftoverForms)}] " +
                    $"Reports=[{string.Join(", ", leftoverReports)}]");
            }

            return new MenuInspection(
                hasModule,
                moduleName,
                lineCount,
                source,
                procedures,
                bindings,
                references,
                opened,
                closed
                    && runtime.LoadedNames("Forms").Count == 0
                    && runtime.LoadedNames("Reports").Count == 0);
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            if (opened)
            {
                closed = TryClose(doCmd, runtime);
            }

            return MenuInspection.Aborted(opened, closed);
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
        IReadOnlyList<AccessEventBinding> formEvents,
        IReadOnlyList<AccessControlAnalysis> controls,
        string? eventProcPrefix,
        IReadOnlyList<AccessVbaProcedure> procedures)
    {
        var bindings = new List<AccessEventProcedureBinding>();
        foreach (var binding in formEvents)
        {
            bindings.Add(AccessEventProcedureNames.Bind(
                binding.ObjectName,
                AccessObjectKind.Form,
                binding.EventName,
                binding.Expression,
                eventProcPrefix,
                FormName,
                procedures));
        }

        foreach (var control in controls)
        {
            foreach (var binding in control.Events)
            {
                bindings.Add(AccessEventProcedureNames.Bind(
                    control.Name,
                    AccessObjectKind.Control,
                    binding.EventName,
                    binding.Expression,
                    eventProcPrefix,
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

    private sealed record MenuInspection(
        bool? HasModule,
        string? ModuleName,
        int? LineCount,
        string? Source,
        IReadOnlyList<AccessVbaProcedure> Procedures,
        IReadOnlyList<AccessEventProcedureBinding> Bindings,
        IReadOnlyList<AccessVbaReference> References,
        bool OpenedInDesignView,
        bool ClosedCleanly)
    {
        public static MenuInspection Aborted(bool opened, bool closed)
        {
            return new MenuInspection(null, null, null, null, [], [], [], opened, closed);
        }
    }
}
