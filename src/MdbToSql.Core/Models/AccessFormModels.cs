namespace MdbToSql.Core.Models;

public enum AccessControlTypeKind
{
    Label,
    TextBox,
    CommandButton,
    ComboBox,
    ListBox,
    CheckBox,
    OptionButton,
    ToggleButton,
    OptionGroup,
    SubForm,
    TabControl,
    Page,
    Image,
    BoundObjectFrame,
    UnboundObjectFrame,
    Line,
    Rectangle,
    Unknown
}

public enum AccessEventBindingKind
{
    None,
    EventProcedure,
    Macro,
    Expression,
    Unknown
}

public enum AccessObjectKind
{
    Form,
    Report,
    Control,
    Section,
    Table,
    Query,
    Macro,
    Module,
    Expression,
    Unknown
}

public enum AccessDependencyKind
{
    RecordSource,
    RowSource,
    SourceObject,
    Expression
}

public enum AccessRecordSourceKind
{
    Empty,
    Table,
    SavedQuery,
    SqlText,
    Unknown
}

public enum AccessRowSourceKind
{
    Empty,
    Table,
    SavedQuery,
    SqlText,
    ValueList,
    Unknown
}

public enum AccessFormSectionKind
{
    Detail,
    FormHeader,
    FormFooter,
    PageHeader,
    PageFooter,
    Unknown
}

public sealed record AccessEventBinding(
    string ObjectName,
    AccessObjectKind ObjectKind,
    string EventName,
    AccessEventBindingKind BindingKind,
    string? Expression);

public sealed record AccessDependency(
    string SourceObject,
    AccessObjectKind SourceKind,
    AccessDependencyKind RelationKind,
    string TargetObject,
    AccessObjectKind TargetKind,
    string Evidence);

public sealed record AccessFormSectionAnalysis(
    string Name,
    AccessFormSectionKind Kind,
    int? RawType,
    int? Height,
    bool? Visible,
    IReadOnlyList<AccessEventBinding> Events,
    int ControlCount);

public sealed record AccessControlAnalysis(
    string Name,
    AccessControlTypeKind ControlType,
    string ControlTypeName,
    int? RawControlType,
    string? ParentName,
    string? Section,
    int? Left,
    int? Top,
    int? Width,
    int? Height,
    bool? Visible,
    bool? Enabled,
    bool? Locked,
    int? TabIndex,
    bool? TabStop,
    string? Caption,
    string? ControlSource,
    bool IsExpression,
    string? DefaultValue,
    string? Format,
    string? DecimalPlaces,
    string? InputMask,
    string? ValidationRule,
    string? ValidationText,
    string? StatusBarText,
    string? Tag,
    string? RowSource,
    string? RowSourceType,
    AccessRowSourceKind? RowSourceKind,
    int? BoundColumn,
    int? ColumnCount,
    string? ColumnWidths,
    bool? ColumnHeads,
    bool? LimitToList,
    int? ListRows,
    string? SourceObject,
    string? LinkMasterFields,
    string? LinkChildFields,
    string? AttachedControl,
    int? PageIndex,
    IReadOnlyList<AccessEventBinding> Events,
    IReadOnlyList<string> Warnings);
