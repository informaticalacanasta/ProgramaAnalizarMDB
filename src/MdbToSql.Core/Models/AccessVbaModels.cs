using MdbToSql.Core.Analysis;

namespace MdbToSql.Core.Models;

public sealed record AccessVbaProcedure(
    string Kind,
    string Name,
    int StartLine,
    int EndLine);

public sealed record AccessEventProcedureBinding(
    string ObjectName,
    string ObjectKind,
    string EventName,
    string? BindingExpression,
    string? EventProcPrefix,
    IReadOnlyList<string> CandidateProcedures,
    string? ResolvedProcedure,
    bool Resolved);

public sealed record AccessVbaReference(
    string Kind,
    string? Target,
    bool Dynamic,
    int Line,
    string Evidence);

public sealed record AccessMenuVbaProbeResult(
    string FormName,
    string ApiUsed,
    bool? HasModule,
    string? ModuleName,
    int? LineCount,
    string? Source,
    IReadOnlyList<AccessVbaProcedure> Procedures,
    IReadOnlyList<AccessEventProcedureBinding> Bindings,
    IReadOnlyList<AccessVbaReference> References,
    bool OpenedInDesignView,
    bool ClosedCleanly,
    FileIntegritySnapshot OriginalBefore,
    FileIntegritySnapshot OriginalAfter,
    bool OriginalIntegrityVerified,
    bool TempsDeleted,
    bool NoNewAccessProcesses,
    string JsonPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public sealed record AccessProcedureDefinition(
    string ModuleName,
    string Kind,
    string Name,
    string? Signature,
    int StartLine,
    int EndLine,
    string ProcedureSource,
    string? ModuleDeclarations);

public sealed record AccessTerminalVbaProbeResult(
    IReadOnlyList<string> ModulesSearched,
    IReadOnlyList<AccessProcedureDefinition> Definitions,
    string? ModuleDeclarations,
    IReadOnlyList<AccessVbaReference> References,
    string ApiUsed,
    bool ClosedCleanly,
    FileIntegritySnapshot OriginalBefore,
    FileIntegritySnapshot OriginalAfter,
    bool OriginalIntegrityVerified,
    bool TempsDeleted,
    bool NoNewAccessProcesses,
    string JsonPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public sealed record AccessVentasAnalysisResult(
    string FormName,
    string ApiUsed,
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
    bool ClosedCleanly,
    FileIntegritySnapshot OriginalBefore,
    FileIntegritySnapshot OriginalAfter,
    bool OriginalIntegrityVerified,
    bool TempsDeleted,
    bool NoNewAccessProcesses,
    string JsonPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
