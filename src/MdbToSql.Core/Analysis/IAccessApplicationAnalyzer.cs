using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public interface IAccessApplicationAnalyzer
{
    Task<AccessApplicationAnalysis> AnalyzeAsync(
        string mdbPath,
        CancellationToken cancellationToken = default);
}
