using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public sealed class ApplicationAnalysisCoordinator
{
    private readonly IAccessApplicationAnalyzer _analyzer;

    public ApplicationAnalysisCoordinator(IAccessApplicationAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    public Task<AccessApplicationAnalysis> AnalyzeAsync(
        string mdbPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mdbPath);
        if (mdbPath.StartsWith(@"\\", StringComparison.Ordinal)
            || mdbPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"No se analizan MDB en rutas UNC ({mdbPath}).");
        }

        var fullPath = Path.GetFullPath(mdbPath);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"No se analizan MDB en rutas UNC ({fullPath}).");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"No existe el MDB '{fullPath}'.", fullPath);
        }

        return _analyzer.AnalyzeAsync(fullPath, cancellationToken);
    }
}
