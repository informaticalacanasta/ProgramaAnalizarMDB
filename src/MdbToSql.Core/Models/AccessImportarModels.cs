using MdbToSql.Core.Analysis;

namespace MdbToSql.Core.Models;

public sealed record AccessCallEdge(
    string From,
    string To,
    int Line,
    bool Cycle);

public sealed record AccessProcedureFunctionDoc(
    string Name,
    string Kind,
    string? Signature,
    int StartLine,
    int EndLine,
    string Purpose,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> TablesRead,
    IReadOnlyList<string> TablesWritten,
    IReadOnlyList<string> Queries,
    IReadOnlyList<string> Transformations,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> ErrorHandling,
    IReadOnlyList<string> InternalCalls,
    IReadOnlyList<AccessVbaReference> Evidence);

public sealed record AccessFileProcessingDoc(
    string WhenRegistered,
    IReadOnlyList<string> BeforeRegistration,
    IReadOnlyList<string> AfterRegistration,
    IReadOnlyList<string> IfIntermediateFails,
    IReadOnlyList<string> WhatPreventsReprocess,
    IReadOnlyList<string> WhatDoesNotPreventReprocess);

public sealed record AccessImportarFunctionalResult(
    string ModuleName,
    string EntryProcedure,
    IReadOnlyList<string> RequestedProcedures,
    IReadOnlyList<AccessCallEdge> CallGraph,
    IReadOnlyList<string> WalkOrder,
    IReadOnlyList<AccessProcedureFunctionDoc> Procedures,
    IReadOnlyList<AccessQueryUse> QueryDefs,
    IReadOnlyList<AccessTableUse> Tables,
    AccessFileProcessingDoc FileProcessing,
    IReadOnlyList<string> ProcessFlow,
    IReadOnlyList<string> Pending,
    string ApiUsed,
    FileIntegritySnapshot? OriginalBefore,
    FileIntegritySnapshot? OriginalAfter,
    bool OriginalIntegrityVerified,
    bool TempsDeleted,
    bool NoNewAccessProcesses,
    string JsonPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
