using MdbToSql.Core.Analysis;

namespace MdbToSql.AccessApplication.Workspace;

internal sealed class AnalysisWorkspace : IDisposable
{
    private bool _disposed;

    private AnalysisWorkspace(string directory, string sourceCopyPath, string analysisSafePath)
    {
        DirectoryPath = directory;
        SourceCopyPath = sourceCopyPath;
        AnalysisSafePath = analysisSafePath;
    }

    public string DirectoryPath { get; }

    public string SourceCopyPath { get; }

    public string AnalysisSafePath { get; }

    public static AnalysisWorkspace CreateFrom(string originalMdbPath)
    {
        EnsureLocal(originalMdbPath, "MDB original");
        var directory = Path.Combine(Path.GetTempPath(), "MdbToSql", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.mdb");
        var analysisSafe = Path.Combine(directory, "analysis-safe.mdb");
        File.Copy(originalMdbPath, source, overwrite: true);
        PrepareCopy(source);
        File.Copy(source, analysisSafe, overwrite: true);
        PrepareCopy(analysisSafe);
        EnsureLocal(source, "source.mdb");
        EnsureLocal(analysisSafe, "analysis-safe.mdb");
        return new AnalysisWorkspace(directory, source, analysisSafe);
    }

    public bool TryDelete(out string? error)
    {
        error = null;
        if (!Directory.Exists(DirectoryPath))
        {
            return true;
        }

        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
            return !Directory.Exists(DirectoryPath);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        TryDelete(out _);
        _disposed = true;
    }

    internal static void EnsureLocal(string path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Ruta vacía para {purpose}.");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Rechazado {purpose}: no se abren rutas UNC ({path}).");
        }

        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Rechazado {purpose}: la ruta resuelta es UNC ({full}).");
        }
    }

    internal static void PrepareCopy(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch
        {
            // El ADS puede no existir.
        }
    }

    internal static bool WaitUntilUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
        }

        return false;
    }
}

internal static class AccessProcessTracker
{
    public static HashSet<int> CurrentPids()
    {
        return System.Diagnostics.Process.GetProcessesByName("MSACCESS")
            .Select(process => process.Id)
            .ToHashSet();
    }

    public static void WaitUntilGone(IReadOnlySet<int> beforePids)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (!CurrentPids().Except(beforePids).Any())
            {
                return;
            }

            Thread.Sleep(400);
        }
    }

    public static string? TryGetPath(int? pid)
    {
        if (pid is null or 0)
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid.Value);
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsAccess2003(string? version, string? path)
    {
        var versionOk = version is not null && version.StartsWith("11.", StringComparison.Ordinal);
        var pathOk = path is null || path.Contains("OFFICE11", StringComparison.OrdinalIgnoreCase);
        var isOffice15 = path is not null && path.Contains("Office15", StringComparison.OrdinalIgnoreCase);
        return versionOk && pathOk && !isOffice15;
    }
}
