using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.Tests;

public sealed class AccessReportProbeSelectorTests
{
    [Fact]
    public void ChoosesShortestNonSubName()
    {
        var chosen = AccessReportProbeSelector.Choose(
        [
            "InformeLargoDeVentas",
            "subDetalle",
            "Zeta",
            "Caja"
        ]);
        Assert.Equal("Caja", chosen);
    }

    [Fact]
    public void FallsBackToFullListIfAllLookLikeSubreports()
    {
        var chosen = AccessReportProbeSelector.Choose(["subUno", "subDos"]);
        Assert.Equal("subDos", chosen);
    }

    [Fact]
    public void SectionClassifier_MapsFormNamesAndGroupLevels()
    {
        Assert.Equal(AccessReportSectionKind.ReportHeader, AccessReportSectionClassifier.Classify(1));
        Assert.Equal(AccessReportSectionKind.GroupHeader, AccessReportSectionClassifier.Classify(5));
        Assert.Equal(1, AccessReportSectionClassifier.GroupLevel(5));
        Assert.Equal(AccessReportSectionKind.GroupFooter, AccessReportSectionClassifier.Classify(6));
        Assert.Equal("ReportHeader", AccessReportSectionClassifier.MapControlSection("FormHeader"));
        Assert.Equal("GroupHeader1", AccessReportSectionClassifier.MapControlSection("5"));
    }
}
