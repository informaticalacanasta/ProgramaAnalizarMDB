using System.Text.RegularExpressions;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessControlTypeClassifier
{
    public static (AccessControlTypeKind Kind, string Name) Classify(int? rawControlType)
    {
        if (rawControlType is null)
        {
            return (AccessControlTypeKind.Unknown, "Unknown");
        }

        return rawControlType.Value switch
        {
            100 => (AccessControlTypeKind.Label, "Label"),
            101 => (AccessControlTypeKind.Rectangle, "Rectangle"),
            102 => (AccessControlTypeKind.Line, "Line"),
            103 => (AccessControlTypeKind.Image, "Image"),
            104 => (AccessControlTypeKind.CommandButton, "CommandButton"),
            105 => (AccessControlTypeKind.OptionButton, "OptionButton"),
            106 => (AccessControlTypeKind.CheckBox, "CheckBox"),
            107 => (AccessControlTypeKind.OptionGroup, "OptionGroup"),
            108 => (AccessControlTypeKind.BoundObjectFrame, "BoundObjectFrame"),
            109 => (AccessControlTypeKind.TextBox, "TextBox"),
            110 => (AccessControlTypeKind.ListBox, "ListBox"),
            111 => (AccessControlTypeKind.ComboBox, "ComboBox"),
            112 => (AccessControlTypeKind.SubForm, "SubForm"),
            114 => (AccessControlTypeKind.UnboundObjectFrame, "UnboundObjectFrame"),
            122 => (AccessControlTypeKind.ToggleButton, "ToggleButton"),
            123 => (AccessControlTypeKind.TabControl, "TabControl"),
            124 => (AccessControlTypeKind.Page, "Page"),
            _ => (AccessControlTypeKind.Unknown, "Unknown")
        };
    }
}

public static class AccessEventBindingClassifier
{
    public static AccessEventBindingKind Classify(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return AccessEventBindingKind.None;
        }

        var trimmed = expression.Trim();
        if (string.Equals(trimmed, "[Event Procedure]", StringComparison.OrdinalIgnoreCase))
        {
            return AccessEventBindingKind.EventProcedure;
        }

        if (trimmed.StartsWith('='))
        {
            return AccessEventBindingKind.Expression;
        }

        return AccessEventBindingKind.Macro;
    }

    public static AccessEventBinding? Bind(
        string objectName,
        AccessObjectKind objectKind,
        string eventName,
        string? expression)
    {
        var kind = Classify(expression);
        if (kind == AccessEventBindingKind.None)
        {
            return null;
        }

        return new AccessEventBinding(objectName, objectKind, eventName, kind, expression?.Trim());
    }
}

public static class AccessExpressionDetector
{
    private static readonly Regex FunctionShape = new(
        @"^[A-Za-z_][A-Za-z0-9_]*\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsExpression(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith('=') || FunctionShape.IsMatch(trimmed);
    }
}

public static class AccessRecordSourceClassifier
{
    public static AccessRecordSourceKind Classify(
        string? recordSource,
        IReadOnlyCollection<string> tableNames,
        IReadOnlyCollection<string> queryNames)
    {
        if (string.IsNullOrWhiteSpace(recordSource))
        {
            return AccessRecordSourceKind.Empty;
        }

        var trimmed = recordSource.Trim();
        if (LooksLikeSql(trimmed))
        {
            return AccessRecordSourceKind.SqlText;
        }

        if (ContainsName(queryNames, trimmed))
        {
            return AccessRecordSourceKind.SavedQuery;
        }

        if (ContainsName(tableNames, trimmed))
        {
            return AccessRecordSourceKind.Table;
        }

        return AccessRecordSourceKind.Unknown;
    }

    public static AccessRowSourceKind ClassifyRowSource(
        string? rowSource,
        string? rowSourceType,
        IReadOnlyCollection<string> tableNames,
        IReadOnlyCollection<string> queryNames)
    {
        if (IsValueList(rowSourceType))
        {
            return AccessRowSourceKind.ValueList;
        }

        if (string.IsNullOrWhiteSpace(rowSource))
        {
            return AccessRowSourceKind.Empty;
        }

        return Classify(rowSource, tableNames, queryNames) switch
        {
            AccessRecordSourceKind.Table => AccessRowSourceKind.Table,
            AccessRecordSourceKind.SavedQuery => AccessRowSourceKind.SavedQuery,
            AccessRecordSourceKind.SqlText => AccessRowSourceKind.SqlText,
            AccessRecordSourceKind.Empty => AccessRowSourceKind.Empty,
            _ => AccessRowSourceKind.Unknown
        };
    }

    public static bool LooksLikeSql(string value)
    {
        var trimmed = value.TrimStart();
        return StartsWithKeyword(trimmed, "SELECT")
            || StartsWithKeyword(trimmed, "TRANSFORM")
            || StartsWithKeyword(trimmed, "PARAMETERS")
            || StartsWithKeyword(trimmed, "INSERT")
            || StartsWithKeyword(trimmed, "UPDATE")
            || StartsWithKeyword(trimmed, "DELETE")
            || StartsWithKeyword(trimmed, "WITH");
    }

    private static bool IsValueList(string? rowSourceType)
    {
        if (string.IsNullOrWhiteSpace(rowSourceType))
        {
            return false;
        }

        var trimmed = rowSourceType.Trim();
        return trimmed is "1"
            || trimmed.Equals("Value List", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithKeyword(string value, string keyword)
    {
        return value.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
            && (value.Length == keyword.Length || !char.IsLetterOrDigit(value[keyword.Length]));
    }

    private static bool ContainsName(IReadOnlyCollection<string> names, string candidate)
    {
        return names.Any(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
    }
}

public static class AccessFormSectionClassifier
{
    public static AccessFormSectionKind Classify(int? rawType)
    {
        return rawType switch
        {
            0 => AccessFormSectionKind.Detail,
            1 => AccessFormSectionKind.FormHeader,
            2 => AccessFormSectionKind.FormFooter,
            3 => AccessFormSectionKind.PageHeader,
            4 => AccessFormSectionKind.PageFooter,
            _ => AccessFormSectionKind.Unknown
        };
    }

    public static string Name(AccessFormSectionKind kind)
    {
        return kind.ToString();
    }
}

public static class AccessSourceObjectParser
{
    public static (string Name, AccessObjectKind Kind) Parse(string? sourceObject)
    {
        if (string.IsNullOrWhiteSpace(sourceObject))
        {
            return (string.Empty, AccessObjectKind.Unknown);
        }

        var trimmed = sourceObject.Trim();
        if (trimmed.StartsWith("Form.", StringComparison.OrdinalIgnoreCase))
        {
            return (trimmed["Form.".Length..], AccessObjectKind.Form);
        }

        if (trimmed.StartsWith("Report.", StringComparison.OrdinalIgnoreCase))
        {
            return (trimmed["Report.".Length..], AccessObjectKind.Report);
        }

        return (trimmed, AccessObjectKind.Form);
    }
}

public static class AccessFormAnalysisOrder
{
    public static IReadOnlyList<string> Resolve(IReadOnlyList<AccessFormAnalysis> inventory)
    {
        var remaining = inventory.ToList();
        var ordered = new List<string>();

        var simple = remaining
            .Where(form => !string.Equals(form.Name, "MENU", StringComparison.OrdinalIgnoreCase))
            .OrderBy(form => form.HasModule == true ? 1 : 0)
            .ThenBy(form => form.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (simple is not null)
        {
            ordered.Add(simple.Name);
            remaining.RemoveAll(form => string.Equals(form.Name, simple.Name, StringComparison.OrdinalIgnoreCase));
        }

        var menu = remaining.FirstOrDefault(form =>
            string.Equals(form.Name, "MENU", StringComparison.OrdinalIgnoreCase));
        if (menu is not null)
        {
            ordered.Add(menu.Name);
            remaining.RemoveAll(form => string.Equals(form.Name, menu.Name, StringComparison.OrdinalIgnoreCase));
        }

        ordered.AddRange(remaining.Select(form => form.Name));
        return ordered;
    }
}

public sealed record AccessFormStats(
    int Total,
    int Ok,
    int WithWarnings,
    int Failed,
    int Controls,
    IReadOnlyDictionary<string, int> ControlsByType,
    int CommandButtons,
    int SubForms,
    int ComboBoxes,
    int ListBoxes,
    int EventProcedures,
    int EventMacros,
    int EventExpressions,
    int RecordSourceDependencies,
    int RowSourceDependencies,
    int SubFormDependencies);

public static class AccessFormStatistics
{
    public static AccessFormStats From(IReadOnlyList<AccessFormAnalysis> forms)
    {
        var controls = forms.SelectMany(form => form.Controls).ToList();
        var events = forms
            .SelectMany(form => form.Events)
            .Concat(forms.SelectMany(form => form.Controls.SelectMany(control => control.Events)))
            .ToList();
        var dependencies = forms.SelectMany(form => form.Dependencies).ToList();
        var byType = controls
            .GroupBy(control => control.ControlTypeName)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new AccessFormStats(
            forms.Count,
            forms.Count(form => form.Error is null && form.OpenedInDesignView),
            forms.Count(form => form.Warnings.Count > 0),
            forms.Count(form => form.Error is not null || !form.OpenedInDesignView),
            controls.Count,
            byType,
            controls.Count(control => control.ControlType == AccessControlTypeKind.CommandButton),
            controls.Count(control => control.ControlType == AccessControlTypeKind.SubForm),
            controls.Count(control => control.ControlType == AccessControlTypeKind.ComboBox),
            controls.Count(control => control.ControlType == AccessControlTypeKind.ListBox),
            events.Count(item => item.BindingKind == AccessEventBindingKind.EventProcedure),
            events.Count(item => item.BindingKind == AccessEventBindingKind.Macro),
            events.Count(item => item.BindingKind == AccessEventBindingKind.Expression),
            dependencies.Count(item => item.RelationKind == AccessDependencyKind.RecordSource),
            dependencies.Count(item => item.RelationKind == AccessDependencyKind.RowSource),
            dependencies.Count(item => item.RelationKind == AccessDependencyKind.SourceObject));
    }
}
