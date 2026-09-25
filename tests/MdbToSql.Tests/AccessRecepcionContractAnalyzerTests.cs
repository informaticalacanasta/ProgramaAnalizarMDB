using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.Tests;

public sealed class AccessRecepcionContractAnalyzerTests
{
    [Fact]
    public void ExtractsBangFieldsAndUnknownOrdinal()
    {
        const string source = """
            Function COBRADO()
                Set BD = OpenDatabase("c:\programas\transmisiones\zetas.mdb")
                Set tabla2 = BD.OpenRecordset("zetas", dbOpenTable)
                tabla2.Index = "zeta"
                Set tabla = CurrentDb.OpenRecordset("cobrado", dbOpenDynaset)
                tabla2.Seek "=", tabla!TIENDA, tabla!FECHA, "Z", CAJA
                tabla2.Edit
                tabla2!VENTA = tabla2!VENTA + tabla!COBRADO
                tabla2.Update
            End Function
            """;
        var extracted = AccessVbaDataUseExtractor.Extract("COBRADO", source, 1);
        Assert.Contains(extracted.Fields, item => item is { FieldName: "VENTA", Role: "Write", ObjectName: "zetas" });
        Assert.Contains(extracted.Fields, item => item is { FieldName: "COBRADO", ObjectName: "cobrado", NameVerified: false });
        Assert.Contains(extracted.Searches, item => item.IndexName == "zeta" && item.ObjectName == "zetas");
        Assert.Contains(extracted.Operations, item => item is { Kind: "Update", ObjectName: "zetas" });
    }

    [Fact]
    public void FieldsOrdinalKeepsUnknownName()
    {
        const string source = """
            Function ventas_seccion()
                Set tabla3 = CurrentDb.OpenRecordset("v_seccion", dbOpenDynaset)
                venta_seccion = tabla3.Fields(0)
            End Function
            """;
        var extracted = AccessVbaDataUseExtractor.Extract("ventas_seccion", source, 430);
        var mention = Assert.Single(extracted.Fields);
        Assert.Equal(0, mention.Ordinal);
        Assert.Null(mention.FieldName);
        Assert.False(mention.NameVerified);
        Assert.Equal("v_seccion", mention.ObjectName);
        Assert.Equal(432, mention.Line);
    }

    [Fact]
    public void DocumentsAllEightTpvTypes()
    {
        var formats = AccessTpvFormatCatalog.Document();
        Assert.Equal(new[] { "07", "05", "01", "02", "03", "06", "00", "04" }, formats.Select(item => item.TypeCode));
        Assert.Contains(formats, item => item.TypeCode == "07" && item.ProcessedBy.Contains("Importar_Click", StringComparison.Ordinal));
        Assert.All(formats, item => Assert.NotEmpty(item.Pending));
    }
}
