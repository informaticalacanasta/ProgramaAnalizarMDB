using MdbToSql.AccessApplication.Dao;
using MdbToSql.AccessApplication.Workspace;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication;

public sealed class AccessDaoCatalogProbe
{
    public const string ApiUsed =
        "DAO.DBEngine.36 TableDefs + QueryDef.SQL en copia temporal (solo lectura; sin ejecutar; sin Access.Application)";

    public Task<AccessDaoCatalogResult> RunAsync(string mdbPath, CancellationToken cancellationToken = default)
    {
        return StaRunner.Run(() => Load(mdbPath, cancellationToken), cancellationToken);
    }

    internal static AccessDaoCatalogResult Load(string mdbPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisWorkspace.EnsureLocal(mdbPath, "MDB original");
        var before = FileIntegrity.Capture(mdbPath);
        var warnings = new List<string>();
        var errors = new List<string>();
        var beforePids = AccessProcessTracker.CurrentPids();
        IReadOnlyList<AccessQueryAnalysis> queries = [];
        IReadOnlyList<AccessTableReference> tables = [];
        AnalysisWorkspace? workspace = null;
        try
        {
            workspace = AnalysisWorkspace.CreateFrom(mdbPath);
            using var dao = DaoSession.OpenReadOnly(workspace.SourceCopyPath);
            tables = dao.ReadTables(warnings);
            queries = dao.ReadQueries(warnings);
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
            errors.Add("ERROR CRÍTICO: el MDB original cambió durante la lectura DAO.");
        }

        return new AccessDaoCatalogResult(
            queries,
            tables,
            before,
            after,
            verified,
            workspace is null || !Directory.Exists(workspace.DirectoryPath),
            !AccessProcessTracker.CurrentPids().Except(beforePids).Any(),
            warnings,
            errors);
    }
}

public sealed record AccessDaoCatalogResult(
    IReadOnlyList<AccessQueryAnalysis> Queries,
    IReadOnlyList<AccessTableReference> Tables,
    FileIntegritySnapshot OriginalBefore,
    FileIntegritySnapshot OriginalAfter,
    bool OriginalIntegrityVerified,
    bool TempsDeleted,
    bool NoNewAccessProcesses,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
