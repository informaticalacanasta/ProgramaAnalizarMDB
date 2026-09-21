namespace MdbToSql.Core.Models;

public enum AccessLinkKind
{
    Local,
    AccessMdb,
    Odbc,
    InterBase,
    Text,
    Unknown
}

public sealed record FileIntegritySnapshot(
    string Sha256,
    long Size,
    DateTime LastWriteUtc);

public sealed record AccessStartupAnalysis(
    bool AutoExecExists,
    string? StartupForm,
    bool? StartupShowDbWindow,
    bool? StartupShowStatusBar,
    bool? AllowFullMenus,
    bool? AllowBuiltinToolbars,
    bool? AllowBreakIntoCode,
    bool? AllowSpecialKeys,
    bool? AllowBypassKey);

public sealed record AccessTableReference(
    string Name,
    bool IsLinked,
    string? SourceTableName,
    string? Connect,
    AccessLinkKind LinkKind,
    string? SourcePath,
    bool IsUnc,
    bool IsLocal);

public sealed record AccessQueryParameter(
    string Name,
    int? Type);

public sealed record AccessQueryAnalysis(
    string Name,
    string? Sql,
    int? Type,
    IReadOnlyList<AccessQueryParameter> Parameters,
    string? Connect,
    bool? ReturnsRecords,
    bool IsSystem,
    bool? IsHidden,
    IReadOnlyList<string> Warnings);

public sealed record AccessRelationField(
    string Name,
    string? ForeignName);

public sealed record AccessRelationAnalysis(
    string Name,
    string? PrimaryTable,
    string? ForeignTable,
    IReadOnlyList<AccessRelationField> Fields,
    int Attributes,
    bool ReferentialIntegrity,
    bool CascadeUpdate,
    bool CascadeDelete);

public sealed record AccessFormAnalysis(
    string Name,
    bool? HasModule,
    bool IsLoaded,
    string? Caption,
    string? RecordSource,
    AccessRecordSourceKind RecordSourceKind,
    int? DefaultView,
    string? Filter,
    bool? FilterOn,
    string? OrderBy,
    bool? OrderByOn,
    bool? AllowEdits,
    bool? AllowAdditions,
    bool? AllowDeletions,
    bool? DataEntry,
    bool? Modal,
    bool? PopUp,
    bool? NavigationButtons,
    bool? RecordSelectors,
    bool? DividingLines,
    int? Width,
    bool? AutoCenter,
    bool? AutoResize,
    IReadOnlyList<AccessFormSectionAnalysis> Sections,
    IReadOnlyList<AccessControlAnalysis> Controls,
    IReadOnlyList<AccessEventBinding> Events,
    IReadOnlyList<AccessDependency> Dependencies,
    bool OpenedInDesignView,
    bool ClosedCleanly,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    string? Error)
{
    public static AccessFormAnalysis Unanalyzed(string name, bool? hasModule, bool isLoaded)
    {
        return new AccessFormAnalysis(
            name,
            hasModule,
            isLoaded,
            Caption: null,
            RecordSource: null,
            AccessRecordSourceKind.Empty,
            DefaultView: null,
            Filter: null,
            FilterOn: null,
            OrderBy: null,
            OrderByOn: null,
            AllowEdits: null,
            AllowAdditions: null,
            AllowDeletions: null,
            DataEntry: null,
            Modal: null,
            PopUp: null,
            NavigationButtons: null,
            RecordSelectors: null,
            DividingLines: null,
            Width: null,
            AutoCenter: null,
            AutoResize: null,
            Sections: [],
            Controls: [],
            Events: [],
            Dependencies: [],
            OpenedInDesignView: false,
            ClosedCleanly: false,
            Warnings: [],
            Errors: [],
            Error: null);
    }
}

public sealed record AccessReportAnalysis(
    string Name,
    bool? HasModule,
    bool IsLoaded,
    string? Error,
    string? Warning);

public sealed record AccessMacroAnalysis(
    string Name);

public sealed record AccessModuleAnalysis(
    string Name,
    int? Type,
    bool? HasSourceCode,
    bool? SourceAccessible);

public sealed record AccessApplicationAnalysis(
    string MdbPath,
    string MdbName,
    string MdbHash,
    string? JetVersion,
    FileIntegritySnapshot OriginalBefore,
    FileIntegritySnapshot OriginalAfter,
    bool OriginalIntegrityVerified,
    AccessStartupAnalysis Startup,
    IReadOnlyList<AccessTableReference> Tables,
    IReadOnlyList<AccessTableReference> LinkedTables,
    IReadOnlyList<AccessQueryAnalysis> Queries,
    IReadOnlyList<AccessRelationAnalysis> Relations,
    IReadOnlyList<AccessFormAnalysis> Forms,
    IReadOnlyList<AccessReportAnalysis> Reports,
    IReadOnlyList<AccessMacroAnalysis> Macros,
    IReadOnlyList<AccessModuleAnalysis> Modules,
    int LoadedForms,
    int LoadedReports,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
