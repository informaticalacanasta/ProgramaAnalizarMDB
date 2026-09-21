using System.Reflection;
using MdbToSql.AccessApplication.Com;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication.Analysis;

internal sealed class AccessFormAnalyzer
{
    private static readonly int[] SectionTypes =
    [
        AccessConstants.AcDetail,
        AccessConstants.AcHeader,
        AccessConstants.AcFooter,
        AccessConstants.AcPageHeader,
        AccessConstants.AcPageFooter
    ];

    private readonly ComLifetime _lifetime;
    private readonly object _app;
    private readonly object _doCmd;
    private readonly AccessDialogGuard _dialogs;
    private readonly ComPropertyReader _reader;
    private readonly AccessEventAnalyzer _events;
    private readonly AccessControlAnalyzer _controls;
    private readonly IReadOnlyCollection<string> _tableNames;
    private readonly IReadOnlyCollection<string> _queryNames;

    public AccessFormAnalyzer(
        ComLifetime lifetime,
        object app,
        AccessDialogGuard dialogs,
        IReadOnlyList<AccessTableReference> tables,
        IReadOnlyList<AccessQueryAnalysis> queries,
        IReadOnlyList<string> formNames)
    {
        _lifetime = lifetime;
        _app = app;
        _dialogs = dialogs;
        _reader = new ComPropertyReader();
        _events = new AccessEventAnalyzer(_reader);
        _tableNames = tables.Select(table => table.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _queryNames = queries.Select(query => query.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _controls = new AccessControlAnalyzer(
            lifetime,
            _reader,
            _events,
            _tableNames,
            _queryNames,
            formNames);
        _doCmd = lifetime.Track(ComInterop.Get(app, "DoCmd"));
    }

    public AccessFormAnalysis ReadOpened(object form, string name, List<string> warnings)
    {
        return ReadOpenedForm(form, name, stub: null, warnings);
    }

    public IReadOnlyList<AccessFormAnalysis> Analyze(
        IReadOnlyList<AccessFormAnalysis> inventory,
        List<string> sessionWarnings,
        CancellationToken cancellationToken)
    {
        var byName = inventory.ToDictionary(form => form.Name, StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, AccessFormAnalysis>(StringComparer.OrdinalIgnoreCase);
        var order = AccessFormAnalysisOrder.Resolve(inventory);

        foreach (var name in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byName.TryGetValue(name, out var stub);
            try
            {
                EnsureIdle($"antes de '{name}'");
                results[name] = AnalyzeOne(name, stub);
                EnsureIdle($"después de '{name}'");
            }
            catch (AccessSessionUnsafeException exception)
            {
                if (!results.ContainsKey(name))
                {
                    results[name] = Fail(stub, name, exception.Message, Opened: false, Closed: false);
                }

                foreach (var remaining in order.Where(item => !results.ContainsKey(item)))
                {
                    byName.TryGetValue(remaining, out var remainingStub);
                    results[remaining] = Fail(
                        remainingStub,
                        remaining,
                        "Análisis abortado: sesión Access insegura.",
                        Opened: false,
                        Closed: false);
                }

                sessionWarnings.Add($"Sesión Access insegura al analizar '{name}': {exception.Message}");
                break;
            }
        }

        return order.Select(name => results[name]).ToList();
    }

    private AccessFormAnalysis AnalyzeOne(string name, AccessFormAnalysis? stub)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var opened = false;
        var closed = false;
        try
        {
            ComInterop.Call(
                _doCmd,
                "OpenForm",
                name,
                AccessConstants.AcViewDesign,
                Missing.Value,
                Missing.Value,
                Missing.Value,
                AccessConstants.AcHidden);
            opened = true;

            var dialogs = _dialogs.ConsumeDetected();
            if (dialogs.Count > 0)
            {
                errors.Add("Diálogo modal al abrir en Design View: " + string.Join("; ", dialogs));
                closed = TryClose(name);
                return Fail(stub, name, errors[0], opened, closed, warnings, errors);
            }

            var unexpected = LoadedFormNames()
                .Where(item => !string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (unexpected.Count > 0)
            {
                warnings.Add("Al abrir en Design View aparecieron Forms extra: " + string.Join(", ", unexpected));
                foreach (var extra in unexpected)
                {
                    TryClose(extra);
                }
            }

            var forms = _lifetime.Track(ComInterop.Get(_app, "Forms"));
            var form = _lifetime.Track(ComInterop.Item(forms, name));
            var analysis = ReadOpenedForm(form, name, stub, warnings);
            closed = TryClose(name);
            if (!closed)
            {
                errors.Add("No se pudo cerrar con acSaveNo.");
            }

            var leftovers = LoadedFormNames();
            if (leftovers.Count > 0)
            {
                warnings.Add("Tras cerrar seguían cargados: " + string.Join(", ", leftovers));
                foreach (var leftover in leftovers)
                {
                    TryClose(leftover);
                }
            }

            return analysis with
            {
                IsLoaded = false,
                OpenedInDesignView = true,
                ClosedCleanly = closed && LoadedFormNames().Count == 0 && LoadedReportNames().Count == 0,
                Warnings = warnings,
                Errors = errors,
                Error = errors.Count == 0 ? null : errors[0]
            };
        }
        catch (AccessSessionUnsafeException)
        {
            TryClose(name);
            throw;
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            closed = opened && TryClose(name);
            return Fail(stub, name, exception.Message, opened, closed, warnings, errors);
        }
    }

    private AccessFormAnalysis ReadOpenedForm(
        object form,
        string name,
        AccessFormAnalysis? stub,
        List<string> warnings)
    {
        var hasModule = _reader.ReadBool(form, "HasModule", warnings) ?? stub?.HasModule;
        var recordSource = _reader.ReadString(form, "RecordSource", warnings);
        var recordSourceKind = AccessRecordSourceClassifier.Classify(recordSource, _tableNames, _queryNames);
        var events = _events.ReadFormEvents(form, name, warnings);
        var sections = ReadSections(form, warnings);
        var controls = _controls.ReadTree(form, name, sectionName: null, pageIndex: null, warnings);
        var dependencies = new List<AccessDependency>();
        if (recordSourceKind is AccessRecordSourceKind.Table or AccessRecordSourceKind.SavedQuery
            && !string.IsNullOrWhiteSpace(recordSource))
        {
            dependencies.Add(new AccessDependency(
                name,
                AccessObjectKind.Form,
                AccessDependencyKind.RecordSource,
                recordSource.Trim(),
                recordSourceKind == AccessRecordSourceKind.SavedQuery
                    ? AccessObjectKind.Query
                    : AccessObjectKind.Table,
                $"RecordSource = \"{recordSource}\""));
        }

        foreach (var control in controls)
        {
            dependencies.AddRange(_controls.DependenciesOf(name, control));
        }

        foreach (var binding in events.Where(item => item.BindingKind == AccessEventBindingKind.Expression))
        {
            dependencies.Add(new AccessDependency(
                name,
                AccessObjectKind.Form,
                AccessDependencyKind.Expression,
                binding.Expression ?? binding.EventName,
                AccessObjectKind.Expression,
                $"{binding.EventName} = \"{binding.Expression}\""));
        }

        return new AccessFormAnalysis(
            name,
            hasModule,
            IsLoaded: true,
            _reader.ReadString(form, "Caption", warnings),
            recordSource,
            recordSourceKind,
            _reader.ReadInt(form, "DefaultView", warnings),
            _reader.ReadString(form, "Filter", warnings),
            _reader.ReadBool(form, "FilterOn", warnings),
            _reader.ReadString(form, "OrderBy", warnings),
            _reader.ReadBool(form, "OrderByOn", warnings),
            _reader.ReadBool(form, "AllowEdits", warnings),
            _reader.ReadBool(form, "AllowAdditions", warnings),
            _reader.ReadBool(form, "AllowDeletions", warnings),
            _reader.ReadBool(form, "DataEntry", warnings),
            _reader.ReadBool(form, "Modal", warnings),
            _reader.ReadBool(form, "PopUp", warnings),
            _reader.ReadBool(form, "NavigationButtons", warnings),
            _reader.ReadBool(form, "RecordSelectors", warnings),
            _reader.ReadBool(form, "DividingLines", warnings),
            _reader.ReadInt(form, "Width", warnings),
            _reader.ReadBool(form, "AutoCenter", warnings),
            _reader.ReadBool(form, "AutoResize", warnings),
            sections,
            controls,
            events,
            dependencies,
            OpenedInDesignView: true,
            ClosedCleanly: false,
            warnings,
            Errors: [],
            Error: null);
    }

    private List<AccessFormSectionAnalysis> ReadSections(object form, List<string> warnings)
    {
        var sections = new List<AccessFormSectionAnalysis>();
        foreach (var type in SectionTypes)
        {
            object? section;
            try
            {
                var raw = ComInterop.TryGetIndexed(form, "Section", type);
                if (raw is null)
                {
                    continue;
                }

                section = _lifetime.Track(raw);
            }
            catch (Exception)
            {
                continue;
            }

            var kind = AccessFormSectionClassifier.Classify(type);
            var name = _reader.ReadString(section, "Name", warnings) ?? AccessFormSectionClassifier.Name(kind);
            var controlCount = 0;
            try
            {
                var controls = _lifetime.Track(ComInterop.Get(section, "Controls"));
                controlCount = ComInterop.Count(controls);
            }
            catch (Exception exception)
            {
                warnings.Add($"Section '{name}' Controls: {exception.Message}");
            }

            sections.Add(new AccessFormSectionAnalysis(
                name,
                kind,
                type,
                _reader.ReadInt(section, "Height", warnings),
                _reader.ReadBool(section, "Visible", warnings),
                _events.ReadSectionEvents(section, name, warnings),
                controlCount));
        }

        return sections;
    }

    private void EnsureIdle(string context)
    {
        var dialogs = _dialogs.ConsumeDetected();
        if (dialogs.Count > 0)
        {
            throw new AccessSessionUnsafeException(
                $"Diálogo modal {context}: {string.Join("; ", dialogs)}");
        }

        foreach (var extra in LoadedFormNames())
        {
            TryClose(extra);
        }

        foreach (var extra in LoadedReportNames())
        {
            TryCloseReport(extra);
        }

        var forms = LoadedFormNames();
        var reports = LoadedReportNames();
        if (forms.Count > 0 || reports.Count > 0)
        {
            throw new AccessSessionUnsafeException(
                $"Access no quedó idle {context}. Forms=[{string.Join(", ", forms)}] Reports=[{string.Join(", ", reports)}]");
        }
    }

    private bool TryClose(string name)
    {
        try
        {
            ComInterop.Call(_doCmd, "Close", AccessConstants.AcForm, name, AccessConstants.AcSaveNo);
            return !LoadedFormNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void TryCloseReport(string name)
    {
        try
        {
            ComInterop.Call(_doCmd, "Close", 3, name, AccessConstants.AcSaveNo);
        }
        catch (Exception)
        {
            // Best-effort: no se analizan reports en esta fase.
        }
    }

    private List<string> LoadedFormNames() => ReadOpenNames("Forms");

    private List<string> LoadedReportNames() => ReadOpenNames("Reports");

    private List<string> ReadOpenNames(string collectionName)
    {
        var names = new List<string>();
        try
        {
            var collection = _lifetime.Track(ComInterop.Get(_app, collectionName));
            var count = ComInterop.Count(collection);
            for (var index = 0; index < count; index++)
            {
                var item = _lifetime.Track(ComInterop.Item(collection, index));
                var name = ComInterop.GetString(item, "Name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception)
        {
            return names;
        }

        return names;
    }

    private static AccessFormAnalysis Fail(
        AccessFormAnalysis? stub,
        string name,
        string error,
        bool Opened,
        bool Closed,
        List<string>? warnings = null,
        List<string>? errors = null)
    {
        var warningList = warnings ?? [];
        var errorList = errors ?? [error];
        if (errorList.Count == 0)
        {
            errorList.Add(error);
        }

        return (stub ?? AccessFormAnalysis.Unanalyzed(name, null, false)) with
        {
            IsLoaded = false,
            OpenedInDesignView = Opened,
            ClosedCleanly = Closed,
            Warnings = warningList,
            Errors = errorList,
            Error = errorList[0]
        };
    }
}
