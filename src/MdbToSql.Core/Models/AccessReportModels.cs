namespace MdbToSql.Core.Models;

public enum AccessReportSectionKind
{
    Detail,
    ReportHeader,
    ReportFooter,
    PageHeader,
    PageFooter,
    GroupHeader,
    GroupFooter,
    Unknown
}

public sealed record AccessReportSectionAnalysis(
    string Name,
    AccessReportSectionKind Kind,
    int? RawType,
    int? GroupLevel,
    int? Height,
    bool? Visible,
    IReadOnlyList<AccessEventBinding> Events,
    int ControlCount);

public sealed record AccessReportDesignAnalysis(
    string Name,
    string? Caption,
    string? RecordSource,
    string? Filter,
    bool? FilterOn,
    string? OrderBy,
    bool? OrderByOn,
    bool? HasModule,
    int? Width,
    IReadOnlyList<AccessReportSectionAnalysis> Sections,
    IReadOnlyList<AccessControlAnalysis> Controls,
    IReadOnlyList<AccessEventBinding> Events,
    bool OpenedInDesignView,
    bool ClosedCleanly,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    string? Error);

public sealed record AccessSingleReportProbeResult(
    string MdbPath,
    IReadOnlyList<string> ReportNames,
    string SelectionRule,
    string ChosenReport,
    FileIntegritySnapshot OriginalBefore,
    FileIntegritySnapshot OriginalAfter,
    bool OriginalIntegrityVerified,
    bool TempsDeleted,
    bool NoNewAccessProcesses,
    string? TempCleanupError,
    AccessReportDesignAnalysis? Report,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
