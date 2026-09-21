using MdbToSql.AccessApplication.Com;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication.Analysis;

internal sealed class AccessControlAnalyzer
{
    private readonly ComLifetime _lifetime;
    private readonly ComPropertyReader _reader;
    private readonly AccessEventAnalyzer _events;
    private readonly IReadOnlyCollection<string> _tableNames;
    private readonly IReadOnlyCollection<string> _queryNames;
    private readonly HashSet<string> _formNames;

    public AccessControlAnalyzer(
        ComLifetime lifetime,
        ComPropertyReader reader,
        AccessEventAnalyzer events,
        IReadOnlyCollection<string> tableNames,
        IReadOnlyCollection<string> queryNames,
        IReadOnlyCollection<string> formNames)
    {
        _lifetime = lifetime;
        _reader = reader;
        _events = events;
        _tableNames = tableNames;
        _queryNames = queryNames;
        _formNames = formNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public List<AccessControlAnalysis> ReadTree(
        object container,
        string? parentName,
        string? sectionName,
        int? pageIndex,
        List<string> warnings)
    {
        var results = new List<AccessControlAnalysis>();
        ReadTree(container, parentName, sectionName, pageIndex, results, warnings);
        return results;
    }

    private void ReadTree(
        object container,
        string? parentName,
        string? sectionName,
        int? pageIndex,
        List<AccessControlAnalysis> results,
        List<string> warnings)
    {
        object? controls;
        int count;
        try
        {
            controls = _lifetime.Track(ComInterop.Get(container, "Controls"));
            count = ComInterop.Count(controls);
        }
        catch (Exception exception)
        {
            warnings.Add($"Controls de '{parentName}': {exception.Message}");
            return;
        }

        for (var index = 0; index < count; index++)
        {
            object control;
            try
            {
                control = _lifetime.Track(ComInterop.Item(controls, index));
            }
            catch (Exception exception)
            {
                warnings.Add($"Control[{index}] de '{parentName}': {exception.Message}");
                continue;
            }

            var analysis = ReadOne(control, parentName, sectionName, pageIndex, warnings);
            results.Add(analysis);

            if (analysis.ControlType == AccessControlTypeKind.TabControl)
            {
                ReadPages(control, analysis.Name, analysis.Section, results, warnings);
            }
            else if (analysis.ControlType == AccessControlTypeKind.OptionGroup)
            {
                ReadTree(control, analysis.Name, analysis.Section, pageIndex, results, warnings);
            }
        }
    }

    public List<AccessDependency> DependenciesOf(string formName, AccessControlAnalysis control)
    {
        var dependencies = new List<AccessDependency>();
        if (control.ControlType == AccessControlTypeKind.SubForm
            && !string.IsNullOrWhiteSpace(control.SourceObject))
        {
            var parsed = AccessSourceObjectParser.Parse(control.SourceObject);
            var targetKind = _formNames.Contains(parsed.Name) ? AccessObjectKind.Form : parsed.Kind;
            dependencies.Add(new AccessDependency(
                formName,
                AccessObjectKind.Form,
                AccessDependencyKind.SourceObject,
                string.IsNullOrWhiteSpace(parsed.Name) ? control.SourceObject : parsed.Name,
                targetKind,
                $"{control.Name}.SourceObject = \"{control.SourceObject}\""));
        }

        if ((control.ControlType is AccessControlTypeKind.ComboBox or AccessControlTypeKind.ListBox)
            && control.RowSourceKind is AccessRowSourceKind.Table or AccessRowSourceKind.SavedQuery
            && !string.IsNullOrWhiteSpace(control.RowSource))
        {
            var targetKind = control.RowSourceKind == AccessRowSourceKind.SavedQuery
                ? AccessObjectKind.Query
                : AccessObjectKind.Table;
            dependencies.Add(new AccessDependency(
                control.Name,
                AccessObjectKind.Control,
                AccessDependencyKind.RowSource,
                control.RowSource.Trim(),
                targetKind,
                $"RowSource = \"{control.RowSource}\""));
        }

        if (control.IsExpression && !string.IsNullOrWhiteSpace(control.ControlSource))
        {
            dependencies.Add(new AccessDependency(
                control.Name,
                AccessObjectKind.Control,
                AccessDependencyKind.Expression,
                control.ControlSource.Trim(),
                AccessObjectKind.Expression,
                $"ControlSource = \"{control.ControlSource}\""));
        }

        return dependencies;
    }

    private void ReadPages(
        object tab,
        string tabName,
        string? sectionName,
        List<AccessControlAnalysis> results,
        List<string> warnings)
    {
        object pages;
        int count;
        try
        {
            pages = _lifetime.Track(ComInterop.Get(tab, "Pages"));
            count = ComInterop.Count(pages);
        }
        catch (Exception exception)
        {
            warnings.Add($"Pages de '{tabName}': {exception.Message}");
            return;
        }

        for (var index = 0; index < count; index++)
        {
            object page;
            try
            {
                page = _lifetime.Track(ComInterop.Item(pages, index));
            }
            catch (Exception exception)
            {
                warnings.Add($"Page[{index}] de '{tabName}': {exception.Message}");
                continue;
            }

            var pageIndex = _reader.ReadInt(page, "PageIndex", warnings) ?? index;
            var pageAnalysis = ReadOne(page, tabName, sectionName, pageIndex, warnings);
            results.Add(pageAnalysis);
            ReadTree(page, pageAnalysis.Name, sectionName, pageIndex, results, warnings);
        }
    }

    private AccessControlAnalysis ReadOne(
        object control,
        string? parentName,
        string? sectionName,
        int? pageIndex,
        List<string> warnings)
    {
        var localWarnings = new List<string>();
        var name = _reader.ReadString(control, "Name", localWarnings) ?? "(sin nombre)";
        var rawType = _reader.ReadInt(control, "ControlType", localWarnings);
        var classified = AccessControlTypeClassifier.Classify(rawType);
        if (classified.Kind == AccessControlTypeKind.Unknown && rawType is not null)
        {
            localWarnings.Add($"ControlType desconocido: {rawType}.");
        }

        var resolvedSection = ResolveSection(control, sectionName, localWarnings);
        var attached = classified.Kind == AccessControlTypeKind.Label
            ? ReadAttachedControl(control, localWarnings)
            : null;

        string? rowSource = null;
        string? rowSourceType = null;
        AccessRowSourceKind? rowSourceKind = null;
        int? boundColumn = null;
        int? columnCount = null;
        string? columnWidths = null;
        bool? columnHeads = null;
        bool? limitToList = null;
        int? listRows = null;
        string? sourceObject = null;
        string? linkMaster = null;
        string? linkChild = null;
        var caption = _reader.ReadString(control, "Caption", localWarnings);
        var controlSource = _reader.ReadString(control, "ControlSource", localWarnings);
        var left = _reader.ReadInt(control, "Left", localWarnings);
        var top = _reader.ReadInt(control, "Top", localWarnings);
        var width = _reader.ReadInt(control, "Width", localWarnings);
        var height = _reader.ReadInt(control, "Height", localWarnings);
        var visible = _reader.ReadBool(control, "Visible", localWarnings);
        var enabled = _reader.ReadBool(control, "Enabled", localWarnings);
        var locked = _reader.ReadBool(control, "Locked", localWarnings);
        var tabIndex = _reader.ReadInt(control, "TabIndex", localWarnings);
        var tabStop = _reader.ReadBool(control, "TabStop", localWarnings);
        var defaultValue = _reader.ReadString(control, "DefaultValue", localWarnings);
        var format = _reader.ReadString(control, "Format", localWarnings);
        var decimalPlaces = _reader.ReadString(control, "DecimalPlaces", localWarnings);
        var inputMask = _reader.ReadString(control, "InputMask", localWarnings);
        var validationRule = _reader.ReadString(control, "ValidationRule", localWarnings);
        var validationText = _reader.ReadString(control, "ValidationText", localWarnings);
        var statusBarText = _reader.ReadString(control, "StatusBarText", localWarnings);
        var tag = _reader.ReadString(control, "Tag", localWarnings);

        if (classified.Kind is AccessControlTypeKind.ComboBox or AccessControlTypeKind.ListBox)
        {
            rowSource = _reader.ReadString(control, "RowSource", localWarnings);
            rowSourceType = _reader.ReadString(control, "RowSourceType", localWarnings);
            rowSourceKind = AccessRecordSourceClassifier.ClassifyRowSource(
                rowSource,
                rowSourceType,
                _tableNames,
                _queryNames);
            boundColumn = _reader.ReadInt(control, "BoundColumn", localWarnings);
            columnCount = _reader.ReadInt(control, "ColumnCount", localWarnings);
            columnWidths = _reader.ReadString(control, "ColumnWidths", localWarnings);
            columnHeads = _reader.ReadBool(control, "ColumnHeads", localWarnings);
            limitToList = classified.Kind == AccessControlTypeKind.ComboBox
                ? _reader.ReadBool(control, "LimitToList", localWarnings)
                : null;
            listRows = classified.Kind == AccessControlTypeKind.ComboBox
                ? _reader.ReadInt(control, "ListRows", localWarnings)
                : null;
        }

        if (classified.Kind == AccessControlTypeKind.SubForm)
        {
            sourceObject = _reader.ReadString(control, "SourceObject", localWarnings);
            linkMaster = _reader.ReadString(control, "LinkMasterFields", localWarnings);
            linkChild = _reader.ReadString(control, "LinkChildFields", localWarnings);
        }

        if (classified.Kind == AccessControlTypeKind.Page)
        {
            pageIndex ??= _reader.ReadInt(control, "PageIndex", localWarnings);
        }

        var events = _events.ReadControlEvents(control, name, localWarnings);
        warnings.AddRange(localWarnings.Select(item => $"{name}: {item}"));

        return new AccessControlAnalysis(
            name,
            classified.Kind,
            classified.Name,
            rawType,
            parentName,
            resolvedSection,
            left,
            top,
            width,
            height,
            visible,
            enabled,
            locked,
            tabIndex,
            tabStop,
            caption,
            controlSource,
            AccessExpressionDetector.IsExpression(controlSource),
            defaultValue,
            format,
            decimalPlaces,
            inputMask,
            validationRule,
            validationText,
            statusBarText,
            tag,
            rowSource,
            rowSourceType,
            rowSourceKind,
            boundColumn,
            columnCount,
            columnWidths,
            columnHeads,
            limitToList,
            listRows,
            sourceObject,
            linkMaster,
            linkChild,
            attached,
            pageIndex,
            events,
            localWarnings);
    }

    private string? ResolveSection(object control, string? fallback, List<string> warnings)
    {
        var raw = _reader.ReadInt(control, "Section", warnings);
        if (raw is null)
        {
            return fallback;
        }

        var kind = AccessFormSectionClassifier.Classify(raw);
        return kind == AccessFormSectionKind.Unknown ? fallback ?? raw.ToString() : AccessFormSectionClassifier.Name(kind);
    }

    private string? ReadAttachedControl(object label, List<string> warnings)
    {
        var parent = _reader.ReadObject(label, "Parent", warnings);
        if (parent is null)
        {
            return null;
        }

        _lifetime.Track(parent);
        var parentControlName = _reader.ReadString(parent, "Name", warnings);
        if (string.IsNullOrWhiteSpace(parentControlName))
        {
            return null;
        }

        var parentType = _reader.ReadInt(parent, "ControlType", warnings);
        var kind = AccessControlTypeClassifier.Classify(parentType).Kind;
        return kind is AccessControlTypeKind.TextBox
            or AccessControlTypeKind.ComboBox
            or AccessControlTypeKind.ListBox
            or AccessControlTypeKind.CommandButton
            or AccessControlTypeKind.CheckBox
            or AccessControlTypeKind.OptionButton
            or AccessControlTypeKind.ToggleButton
            or AccessControlTypeKind.OptionGroup
            or AccessControlTypeKind.BoundObjectFrame
            or AccessControlTypeKind.UnboundObjectFrame
            or AccessControlTypeKind.SubForm
            ? parentControlName
            : null;
    }
}
