using MdbToSql.AccessApplication;
using MdbToSql.Core.Analysis;

namespace MdbToSql.AccessProbe;

internal static class Phase3
{
    public static int Run(string mdbPath)
    {
        Console.WriteLine("FASE 3 — análisis detallado de formularios Access");
        Console.WriteLine(mdbPath);
        Console.WriteLine();

        try
        {
            var coordinator = new ApplicationAnalysisCoordinator(new AccessApplicationAnalyzer());
            var analysis = coordinator.AnalyzeAsync(mdbPath).GetAwaiter().GetResult();
            Console.WriteLine(AccessApplicationAnalysisPrinter.Format(analysis));
            return analysis.OriginalIntegrityVerified && analysis.Errors.Count == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine("ERROR: " + exception);
            return 2;
        }
    }
}
