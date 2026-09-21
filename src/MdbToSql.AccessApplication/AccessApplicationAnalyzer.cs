using System.Globalization;
using MdbToSql.AccessApplication.Analysis;
using MdbToSql.AccessApplication.Com;
using MdbToSql.AccessApplication.Dao;
using MdbToSql.AccessApplication.Workspace;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication;

public sealed class AccessApplicationAnalyzer : IAccessApplicationAnalyzer
{
    public Task<AccessApplicationAnalysis> AnalyzeAsync(
        string mdbPath,
        CancellationToken cancellationToken = default)
    {
        return StaRunner.Run(() => Analyze(mdbPath, cancellationToken), cancellationToken);
    }

    private static AccessApplicationAnalysis Analyze(string mdbPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IntPtr.Size != 4)
        {
            throw new AccessApplicationAnalysisException(
                $"El analizador de aplicación Access requiere x86. IntPtr.Size={IntPtr.Size}.");
        }

        AnalysisWorkspace.EnsureLocal(mdbPath, "MDB original");
        var before = FileIntegrity.Capture(mdbPath);
        var warnings = new List<string>();
        var errors = new List<string>();
        var beforePids = AccessProcessTracker.CurrentPids();
        AnalysisWorkspace? workspace = null;
        AccessApplicationAnalysis? built = null;
        try
        {
            workspace = AnalysisWorkspace.CreateFrom(mdbPath);
            cancellationToken.ThrowIfCancellationRequested();

            string? jetVersion;
            AccessStartupAnalysis startup;
            IReadOnlyList<AccessTableReference> tables;
            IReadOnlyList<AccessQueryAnalysis> queries;
            IReadOnlyList<AccessRelationAnalysis> relations;
            IReadOnlyList<string> formNames;
            IReadOnlyList<string> reportNames;
            IReadOnlyList<string> macroNames;
            IReadOnlyList<string> moduleNames;
            using (var dao = DaoSession.OpenReadOnly(workspace.SourceCopyPath))
            {
                jetVersion = dao.JetVersion;
                startup = dao.ReadStartup();
                tables = dao.ReadTables(warnings);
                queries = dao.ReadQueries(warnings);
                relations = dao.ReadRelations(warnings);
                formNames = dao.ReadDocumentNames("Forms");
                reportNames = dao.ReadDocumentNames("Reports");
                macroNames = dao.ReadDocumentNames("Scripts");
                moduleNames = dao.ReadDocumentNames("Modules");
            }

            if (!AnalysisWorkspace.WaitUntilUnlocked(workspace.SourceCopyPath))
            {
                warnings.Add("source.mdb siguió bloqueado tras DAO de inventario.");
            }

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
            var accessInventory = SafeAccessAnalysisSession.OpenAndInventory(
                workspace.AnalysisSafePath,
                beforePids,
                formNames,
                reportNames,
                macroNames,
                moduleNames,
                tables,
                queries,
                warnings,
                cancellationToken);

            var after = FileIntegrity.Capture(mdbPath);
            var verified = FileIntegrity.EqualsSnapshot(before, after);
            if (!verified)
            {
                errors.Add("ERROR CRÍTICO: el MDB original cambió durante el análisis.");
            }

            var linked = tables.Where(table => table.IsLinked).ToList();
            var dependencies = linked
                .Select(table => table.SourcePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();

            built = new AccessApplicationAnalysis(
                mdbPath,
                Path.GetFileName(mdbPath),
                before.Sha256,
                jetVersion,
                before,
                after,
                verified,
                startup,
                tables,
                linked,
                queries,
                relations,
                accessInventory.Forms,
                accessInventory.Reports,
                accessInventory.Macros.Count > 0
                    ? accessInventory.Macros
                    : macroNames.Select(name => new AccessMacroAnalysis(name)).ToList(),
                accessInventory.Modules.Count > 0
                    ? accessInventory.Modules
                    : moduleNames.Select(name => new AccessModuleAnalysis(name, null, null, null)).ToList(),
                accessInventory.LoadedForms,
                accessInventory.LoadedReports,
                dependencies,
                warnings,
                errors);
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

        return built
            ?? throw new AccessApplicationAnalysisException("El análisis no produjo resultado.");
    }
}

internal sealed class AccessObjectInventory
{
    public List<AccessFormAnalysis> Forms { get; } = [];
    public List<AccessReportAnalysis> Reports { get; } = [];
    public List<AccessMacroAnalysis> Macros { get; } = [];
    public List<AccessModuleAnalysis> Modules { get; } = [];
    public int LoadedForms { get; set; }
    public int LoadedReports { get; set; }
}

internal static class SafeAccessAnalysisSession
{
    public static AccessObjectInventory OpenAndInventory(
        string analysisSafePath,
        IReadOnlySet<int> beforePids,
        IReadOnlyList<string> daoForms,
        IReadOnlyList<string> daoReports,
        IReadOnlyList<string> daoMacros,
        IReadOnlyList<string> daoModules,
        IReadOnlyList<AccessTableReference> tables,
        IReadOnlyList<AccessQueryAnalysis> queries,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        AnalysisWorkspace.EnsureLocal(analysisSafePath, "analysis-safe.mdb");
        using var runtime = SafeAccessRuntime.Open(analysisSafePath, beforePids);
        var lifetime = runtime.Lifetime;
        var app = runtime.App;
        var dialogs = runtime.Dialogs;

        var inventory = new AccessObjectInventory
        {
            LoadedForms = 0,
            LoadedReports = 0
        };
        inventory.Forms.AddRange(ReadForms(lifetime, app, daoForms, warnings));
        inventory.Reports.AddRange(ReadReports(lifetime, app, daoReports, warnings));
        FillMacros(lifetime, app, daoMacros, inventory.Macros);
        FillModules(lifetime, app, daoModules, inventory.Modules, warnings);

        var formAnalyzer = new AccessFormAnalyzer(
            lifetime,
            app,
            dialogs,
            tables,
            queries,
            inventory.Forms.Select(form => form.Name).ToList());
        var detailedForms = formAnalyzer.Analyze(inventory.Forms, warnings, cancellationToken);
        inventory.Forms.Clear();
        inventory.Forms.AddRange(detailedForms);
        return inventory;
    }

    private static List<string> ReadOpenNames(ComLifetime lifetime, object app, string collectionName)
    {
        var names = new List<string>();
        try
        {
            var collection = lifetime.Track(ComInterop.Get(app, collectionName));
            var count = ComInterop.Count(collection);
            for (var index = 0; index < count; index++)
            {
                var item = lifetime.Track(ComInterop.Item(collection, index));
                var name = ComInterop.GetString(item, "Name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
            return names;
        }

        return names;
    }

    private sealed record AccessObjectSnapshot(bool IsLoaded, bool? HasModule, int? Type);

    private static Dictionary<string, AccessObjectSnapshot> ReadObjectMap(
        ComLifetime lifetime,
        object app,
        string collectionName,
        List<string> warnings)
    {
        var map = new Dictionary<string, AccessObjectSnapshot>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var project = lifetime.Track(ComInterop.Get(app, "CurrentProject"));
            var collection = lifetime.Track(ComInterop.Get(project, collectionName));
            var count = ComInterop.Count(collection);
            for (var index = 0; index < count; index++)
            {
                var item = lifetime.Track(ComInterop.Item(collection, index));
                var name = ComInterop.GetString(item, "Name") ?? string.Empty;
                if (AccessSystemObject.IsSystemName(name))
                {
                    continue;
                }

                map[name] = new AccessObjectSnapshot(
                    IsComTrue(ComInterop.TryGet(item, "IsLoaded")),
                    TryComBool(ComInterop.TryGet(item, "HasModule")),
                    TryComInt(ComInterop.TryGet(item, "Type")));
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"{collectionName}: {exception.Message}");
        }

        return map;
    }

    private static List<AccessFormAnalysis> ReadForms(
        ComLifetime lifetime,
        object app,
        IReadOnlyList<string> daoNames,
        List<string> warnings)
    {
        var snapshots = ReadObjectMap(lifetime, app, "AllForms", warnings);
        var names = daoNames.Count > 0 ? daoNames : snapshots.Keys.ToList();
        return names
            .Select(name =>
            {
                snapshots.TryGetValue(name, out var snapshot);
                return AccessFormAnalysis.Unanalyzed(
                    name,
                    snapshot?.HasModule,
                    snapshot?.IsLoaded ?? false);
            })
            .ToList();
    }

    private static List<AccessReportAnalysis> ReadReports(
        ComLifetime lifetime,
        object app,
        IReadOnlyList<string> daoNames,
        List<string> warnings)
    {
        var snapshots = ReadObjectMap(lifetime, app, "AllReports", warnings);
        var names = daoNames.Count > 0 ? daoNames : snapshots.Keys.ToList();
        return names
            .Select(name =>
            {
                snapshots.TryGetValue(name, out var snapshot);
                return new AccessReportAnalysis(
                    name,
                    snapshot?.HasModule,
                    snapshot?.IsLoaded ?? false,
                    Error: null,
                    Warning: null);
            })
            .ToList();
    }

    private static void FillMacros(
        ComLifetime lifetime,
        object app,
        IReadOnlyList<string> daoNames,
        List<AccessMacroAnalysis> macros)
    {
        var names = daoNames.Count > 0 ? daoNames : ReadAllNames(lifetime, app, "AllMacros");
        macros.AddRange(names.Select(name => new AccessMacroAnalysis(name)));
    }

    private static void FillModules(
        ComLifetime lifetime,
        object app,
        IReadOnlyList<string> daoNames,
        List<AccessModuleAnalysis> modules,
        List<string> warnings)
    {
        var snapshots = ReadObjectMap(lifetime, app, "AllModules", warnings);
        var names = daoNames.Count > 0
            ? daoNames
            : snapshots.Count > 0
                ? snapshots.Keys.ToList()
                : ReadAllNames(lifetime, app, "AllModules");
        foreach (var name in names)
        {
            snapshots.TryGetValue(name, out var snapshot);
            modules.Add(new AccessModuleAnalysis(
                name,
                snapshot?.Type,
                HasSourceCode: null,
                SourceAccessible: null));
        }
    }

    private static bool IsComTrue(object? value)
    {
        return value switch
        {
            bool flag => flag,
            sbyte number => number != 0,
            byte number => number != 0,
            short number => number != 0,
            ushort number => number != 0,
            int number => number != 0,
            uint number => number != 0,
            long number => number != 0,
            ulong number => number != 0,
            _ => false
        };
    }

    private static bool? TryComBool(object? value)
    {
        return value is null ? null : IsComTrue(value);
    }

    private static int? TryComInt(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<string> ReadAllNames(ComLifetime lifetime, object app, string collectionName)
    {
        var names = new List<string>();
        try
        {
            var project = lifetime.Track(ComInterop.Get(app, "CurrentProject"));
            var collection = lifetime.Track(ComInterop.Get(project, collectionName));
            var count = ComInterop.Count(collection);
            for (var index = 0; index < count; index++)
            {
                var item = lifetime.Track(ComInterop.Item(collection, index));
                var name = ComInterop.GetString(item, "Name") ?? string.Empty;
                if (!AccessSystemObject.IsSystemName(name))
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
            return names;
        }

        return names;
    }
}

internal static class StaRunner
{
    public static Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }
}
