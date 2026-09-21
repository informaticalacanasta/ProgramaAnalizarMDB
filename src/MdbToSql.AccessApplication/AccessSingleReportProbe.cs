using System.Reflection;
using MdbToSql.AccessApplication.Analysis;
using MdbToSql.AccessApplication.Com;
using MdbToSql.AccessApplication.Dao;
using MdbToSql.AccessApplication.Workspace;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication;

public sealed class AccessSingleReportProbe
{
    private static readonly int[] ReportSectionTypes = CreateSectionTypes();

    public Task<AccessSingleReportProbeResult> RunAsync(
        string mdbPath,
        CancellationToken cancellationToken = default)
    {
        return StaRunner.Run(() => Run(mdbPath, cancellationToken), cancellationToken);
    }

    private static AccessSingleReportProbeResult Run(string mdbPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IntPtr.Size != 4)
        {
            throw new AccessApplicationAnalysisException(
                $"El probe de informes requiere x86. IntPtr.Size={IntPtr.Size}.");
        }

        AnalysisWorkspace.EnsureLocal(mdbPath, "MDB original");
        var before = FileIntegrity.Capture(mdbPath);
        var warnings = new List<string>();
        var errors = new List<string>();
        var beforePids = AccessProcessTracker.CurrentPids();
        AnalysisWorkspace? workspace = null;
        AccessReportDesignAnalysis? report = null;
        IReadOnlyList<string> reportNames = [];
        var chosen = string.Empty;
        try
        {
            workspace = AnalysisWorkspace.CreateFrom(mdbPath);
            IReadOnlyList<AccessTableReference> tables;
            IReadOnlyList<AccessQueryAnalysis> queries;
            using (var dao = DaoSession.OpenReadOnly(workspace.SourceCopyPath))
            {
                reportNames = dao.ReadDocumentNames("Reports");
                tables = dao.ReadTables(warnings);
                queries = dao.ReadQueries(warnings);
            }

            if (reportNames.Count == 0)
            {
                throw new AccessApplicationAnalysisException("DAO no enumeró ningún informe.");
            }

            chosen = AccessReportProbeSelector.Choose(reportNames);
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
            report = InspectOne(runtime, chosen, tables, queries, reportNames, warnings, errors);
        }
        catch (Exception exception) when (exception is not AccessApplicationAnalysisException
            and not UnsafeAccessStartupException
            and not AccessSessionUnsafeException
            and not OperationCanceledException)
        {
            errors.Add(exception.Message);
        }
        catch (Exception exception) when (exception is AccessApplicationAnalysisException
            or UnsafeAccessStartupException
            or AccessSessionUnsafeException)
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

        var tempsDeleted = workspace is null
            || !Directory.Exists(workspace.DirectoryPath);
        var noNewAccess = !AccessProcessTracker.CurrentPids().Except(beforePids).Any();
        return new AccessSingleReportProbeResult(
            mdbPath,
            reportNames,
            AccessReportProbeSelector.Rule,
            chosen,
            before,
            after,
            verified,
            tempsDeleted,
            noNewAccess,
            TempCleanupError: tempsDeleted ? null : "El directorio temporal sigue existiendo.",
            report,
            warnings,
            errors);
    }

    private static AccessReportDesignAnalysis InspectOne(
        SafeAccessRuntime runtime,
        string name,
        IReadOnlyList<AccessTableReference> tables,
        IReadOnlyList<AccessQueryAnalysis> queries,
        IReadOnlyList<string> reportNames,
        List<string> warnings,
        List<string> errors)
    {
        var reader = new ComPropertyReader();
        var events = new AccessEventAnalyzer(reader);
        var doCmd = runtime.Lifetime.Track(ComInterop.Get(runtime.App, "DoCmd"));
        var opened = false;
        var closed = false;
        try
        {
            OpenHiddenDesign(doCmd, name);
            opened = true;
            var dialogs = runtime.Dialogs.ConsumeDetected();
            if (dialogs.Count > 0)
            {
                errors.Add("Diálogo modal al abrir el informe: " + string.Join("; ", dialogs));
                closed = TryClose(doCmd, runtime, name);
                return Failed(name, errors[0], opened, closed, warnings, errors);
            }

            var loadedForms = runtime.LoadedNames("Forms");
            var loadedReports = runtime.LoadedNames("Reports");
            if (loadedForms.Count > 0)
            {
                errors.Add("Al abrir el informe aparecieron Forms: " + string.Join(", ", loadedForms));
                closed = TryClose(doCmd, runtime, name);
                return Failed(name, errors[^1], opened, closed, warnings, errors);
            }

            var unexpected = loadedReports
                .Where(item => !string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (unexpected.Count > 0)
            {
                errors.Add("Reports inesperados al abrir Design View: " + string.Join(", ", unexpected));
                foreach (var extra in unexpected)
                {
                    TryClose(doCmd, runtime, extra);
                }

                closed = TryClose(doCmd, runtime, name);
                return Failed(name, errors[^1], opened, closed, warnings, errors);
            }

            var reports = runtime.Lifetime.Track(ComInterop.Get(runtime.App, "Reports"));
            var report = runtime.Lifetime.Track(ComInterop.Item(reports, name));
            var analysis = ReadOpened(runtime, report, name, tables, queries, reportNames, reader, events, warnings);
            closed = TryClose(doCmd, runtime, name);
            if (!closed)
            {
                errors.Add("No se pudo cerrar el informe con acSaveNo.");
            }

            var leftoversForms = runtime.LoadedNames("Forms");
            var leftoversReports = runtime.LoadedNames("Reports");
            if (leftoversForms.Count > 0 || leftoversReports.Count > 0)
            {
                errors.Add(
                    $"Tras cerrar seguían cargados Forms=[{string.Join(", ", leftoversForms)}] " +
                    $"Reports=[{string.Join(", ", leftoversReports)}]");
                foreach (var leftover in leftoversReports)
                {
                    TryClose(doCmd, runtime, leftover);
                }
            }

            return analysis with
            {
                ClosedCleanly = closed
                    && runtime.LoadedNames("Forms").Count == 0
                    && runtime.LoadedNames("Reports").Count == 0,
                Warnings = warnings,
                Errors = errors,
                Error = errors.Count == 0 ? null : errors[0]
            };
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            if (opened)
            {
                closed = TryClose(doCmd, runtime, name);
            }

            return Failed(name, exception.Message, opened, closed, warnings, errors);
        }
    }

    private static AccessReportDesignAnalysis ReadOpened(
        SafeAccessRuntime runtime,
        object report,
        string name,
        IReadOnlyList<AccessTableReference> tables,
        IReadOnlyList<AccessQueryAnalysis> queries,
        IReadOnlyList<string> reportNames,
        ComPropertyReader reader,
        AccessEventAnalyzer events,
        List<string> warnings)
    {
        var controlsAnalyzer = new AccessControlAnalyzer(
            runtime.Lifetime,
            reader,
            events,
            tables.Select(table => table.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            queries.Select(query => query.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            reportNames);
        var rawControls = controlsAnalyzer.ReadTree(report, name, sectionName: null, pageIndex: null, warnings);
        var controls = rawControls
            .Select(control => control with { Section = AccessReportSectionClassifier.MapControlSection(control.Section) })
            .ToList();
        return new AccessReportDesignAnalysis(
            name,
            reader.ReadString(report, "Caption", warnings),
            reader.ReadString(report, "RecordSource", warnings),
            reader.ReadString(report, "Filter", warnings),
            reader.ReadBool(report, "FilterOn", warnings),
            reader.ReadString(report, "OrderBy", warnings),
            reader.ReadBool(report, "OrderByOn", warnings),
            reader.ReadBool(report, "HasModule", warnings),
            reader.ReadInt(report, "Width", warnings),
            ReadSections(runtime, report, events, reader, warnings),
            controls,
            events.ReadReportEvents(report, name, warnings),
            OpenedInDesignView: true,
            ClosedCleanly: false,
            warnings,
            Errors: [],
            Error: null);
    }

    private static List<AccessReportSectionAnalysis> ReadSections(
        SafeAccessRuntime runtime,
        object report,
        AccessEventAnalyzer events,
        ComPropertyReader reader,
        List<string> warnings)
    {
        var sections = new List<AccessReportSectionAnalysis>();
        foreach (var type in ReportSectionTypes)
        {
            var raw = ComInterop.TryGetIndexed(report, "Section", type);
            if (raw is null)
            {
                continue;
            }

            var section = runtime.Lifetime.Track(raw);
            var kind = AccessReportSectionClassifier.Classify(type);
            var name = reader.ReadString(section, "Name", warnings)
                ?? AccessReportSectionClassifier.Name(type);
            var controlCount = 0;
            try
            {
                var controls = runtime.Lifetime.Track(ComInterop.Get(section, "Controls"));
                controlCount = ComInterop.Count(controls);
            }
            catch (Exception exception)
            {
                warnings.Add($"Section '{name}' Controls: {exception.Message}");
            }

            sections.Add(new AccessReportSectionAnalysis(
                name,
                kind,
                type,
                AccessReportSectionClassifier.GroupLevel(type),
                reader.ReadInt(section, "Height", warnings),
                reader.ReadBool(section, "Visible", warnings),
                events.ReadReportSectionEvents(section, name, warnings),
                controlCount));
        }

        return sections;
    }

    private static void OpenHiddenDesign(object doCmd, string name)
    {
        try
        {
            ComInterop.Call(
                doCmd,
                "OpenReport",
                name,
                AccessConstants.AcViewDesign,
                Missing.Value,
                Missing.Value,
                AccessConstants.AcHidden);
        }
        catch (Exception)
        {
            ComInterop.Call(
                doCmd,
                "OpenReport",
                name,
                AccessConstants.AcViewDesign,
                Missing.Value,
                Missing.Value);
        }
    }

    private static bool TryClose(object doCmd, SafeAccessRuntime runtime, string name)
    {
        try
        {
            ComInterop.Call(doCmd, "Close", AccessConstants.AcReport, name, AccessConstants.AcSaveNo);
            return !runtime.LoadedNames("Reports").Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static AccessReportDesignAnalysis Failed(
        string name,
        string error,
        bool opened,
        bool closed,
        List<string> warnings,
        List<string> errors)
    {
        return new AccessReportDesignAnalysis(
            name,
            Caption: null,
            RecordSource: null,
            Filter: null,
            FilterOn: null,
            OrderBy: null,
            OrderByOn: null,
            HasModule: null,
            Width: null,
            Sections: [],
            Controls: [],
            Events: [],
            OpenedInDesignView: opened,
            ClosedCleanly: closed,
            warnings,
            errors,
            error);
    }

    private static int[] CreateSectionTypes()
    {
        var types = new List<int>
        {
            AccessConstants.AcDetail,
            AccessConstants.AcHeader,
            AccessConstants.AcFooter,
            AccessConstants.AcPageHeader,
            AccessConstants.AcPageFooter
        };
        for (var group = 0; group < 10; group++)
        {
            types.Add(5 + (group * 2));
            types.Add(6 + (group * 2));
        }

        return [.. types];
    }
}
