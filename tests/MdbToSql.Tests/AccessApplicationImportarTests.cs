using MdbToSql.AccessApplication;
using MdbToSql.AccessApplication.Workspace;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.Tests;

[Collection("AccessApplication")]
public sealed class AccessApplicationImportarTests
{
    [AccessApplicationFact]
    public async Task Analyze_ImportarCopy_InventoryAndOriginalSafety()
    {
        var original = AccessApplicationFactAttribute.ImportarPath!;
        var originalBefore = FileIntegrity.Capture(original);
        var beforePids = AccessProcessTracker.CurrentPids();
        var beforeTempDirs = SnapshotTempDirs();

        using var directory = new TempDir();
        var copy = Path.Combine(directory.Path, "IMPORTAR.mdb");
        File.Copy(original, copy, overwrite: true);
        File.SetAttributes(copy, FileAttributes.Normal);

        var coordinator = new ApplicationAnalysisCoordinator(new AccessApplicationAnalyzer());
        var analysis = await coordinator.AnalyzeAsync(copy);

        var originalAfter = FileIntegrity.Capture(original);
        var copyAfter = FileIntegrity.Capture(copy);
        var afterPids = AccessProcessTracker.CurrentPids();
        var afterTempDirs = SnapshotTempDirs();

        Console.WriteLine(AccessApplicationAnalysisPrinter.Format(analysis));

        Assert.True(analysis.OriginalIntegrityVerified);
        Assert.True(FileIntegrity.EqualsSnapshot(originalBefore, originalAfter));
        Assert.True(FileIntegrity.EqualsSnapshot(analysis.OriginalBefore, copyAfter));
        Assert.Empty(analysis.Errors);

        Assert.Equal("3.0", analysis.JetVersion);
        Assert.Equal(13, analysis.Tables.Count(table => !table.IsLinked));
        Assert.Equal(42, analysis.LinkedTables.Count);
        Assert.Equal(83, analysis.Queries.Count);
        Assert.Empty(analysis.Relations);
        Assert.Equal(32, analysis.Forms.Count);
        Assert.All(analysis.Forms, form => Assert.False(form.IsLoaded));
        Assert.All(
            analysis.Forms,
            form => Assert.True(
                form.OpenedInDesignView || form.Error is not null,
                form.Name + ": no se abrió en Design View y no hay error."));
        Assert.All(
            analysis.Forms,
            form => Assert.True(
                form.ClosedCleanly || form.Error is not null,
                form.Name + ": no se cerró limpiamente."));

        var menu = Assert.Single(analysis.Forms, form => string.Equals(form.Name, "MENU", StringComparison.OrdinalIgnoreCase));
        Assert.True(menu.OpenedInDesignView);
        Assert.True(menu.ClosedCleanly);
        Assert.Null(menu.Error);
        Console.WriteLine(AccessApplicationAnalysisPrinter.FormatFormDetail(menu));
        Assert.Equal(13, analysis.Reports.Count);
        Assert.Equal(5, analysis.Modules.Count);
        Assert.Empty(analysis.Macros);
        Assert.Equal("MENU", analysis.Startup.StartupForm);
        Assert.False(analysis.Startup.AutoExecExists);
        Assert.Null(analysis.Startup.AllowBypassKey);
        Assert.Equal(0, analysis.LoadedForms);
        Assert.Equal(0, analysis.LoadedReports);
        Assert.All(analysis.Forms, form => Assert.False(form.IsLoaded));
        Assert.All(analysis.Reports, report => Assert.False(report.IsLoaded));
        Assert.DoesNotContain(
            analysis.LinkedTables,
            table => string.IsNullOrWhiteSpace(table.Connect) && table.IsLinked);
        Assert.Contains(
            analysis.LinkedTables,
            table => table.LinkKind == AccessLinkKind.AccessMdb && table.IsUnc);
        Assert.True(afterPids.IsSubsetOf(beforePids));
        Assert.True(afterTempDirs.IsSubsetOf(beforeTempDirs));
        Assert.True(File.Exists(original));
    }

    private static HashSet<string> SnapshotTempDirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "MdbToSql");
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.GetDirectories(root).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

[CollectionDefinition("AccessApplication", DisableParallelization = true)]
public sealed class AccessApplicationCollection
{
}

public sealed class AccessApplicationFactAttribute : FactAttribute
{
    public static string? ImportarPath { get; } = FindImportar();

    public AccessApplicationFactAttribute()
    {
        if (IntPtr.Size != 4)
        {
            Skip = "El análisis de aplicación Access requiere x86.";
            return;
        }

        if (Type.GetTypeFromProgID("Access.Application.11") is null)
        {
            Skip = "Access 2003 (Access.Application.11) no está instalado.";
            return;
        }

        if (Type.GetTypeFromProgID("DAO.DBEngine.36") is null)
        {
            Skip = "DAO 3.6 no está instalado.";
            return;
        }

        if (ImportarPath is null)
        {
            Skip = "IMPORTAR.mdb no está disponible.";
        }
    }

    private static string? FindImportar()
    {
        var env = Environment.GetEnvironmentVariable("MDBTOSQL_IMPORTAR_MDB");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var fallback = Path.Combine(
            @"C:\Users\Usuario\Desktop\LaCanasta\ORIGENMDB",
            "IMPORTAR.mdb");
        return File.Exists(fallback) ? fallback : null;
    }
}
