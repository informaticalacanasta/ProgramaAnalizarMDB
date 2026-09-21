using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.Tests;

public sealed class AccessImportarFunctionalAnalyzerTests
{
    private const string Source = """
        Private Sub Importar_Click()
            If TIENDA <> 162 Then
                Call tiquets_sin_pago
                Call COBRADO
            End If
            tabla.AddNew
            tabla!fichero = sql_buff
            tabla.Update
        End Sub

        Function tiquets_sin_pago()
            CurrentDb.Execute "insert into venta_clientes2 select * from tiquets_sin_pago"
        End Function

        Function COBRADO()
            Set tabla = CurrentDb.OpenRecordset("cobrado", dbOpenDynaset)
            If tabla!TIENDA = 92 Then
                CAJA = 2
            End If
        End Function
        """;

    [Fact]
    public void WalksImportarClickWithoutRepeating()
    {
        var (edges, order, pending) = AccessCallGraph.Walk(
            Source,
            "Importar_Click",
            ["tiquets_sin_pago", "COBRADO"]);
        Assert.Equal(new[] { "Importar_Click", "tiquets_sin_pago", "COBRADO" }, order);
        Assert.Contains(edges, item => item is { From: "Importar_Click", To: "tiquets_sin_pago", Cycle: false });
        Assert.DoesNotContain(edges, item => item.Cycle);
        Assert.DoesNotContain(pending, item => item.Contains("Ciclo", StringComparison.Ordinal));
    }

    [Fact]
    public void ExpandsNestedQueryDefsAndCommaFrom()
    {
        var queries = new AccessQueryAnalysis[]
        {
            new("cobrado", "SELECT * FROM tiquets_pagg, t_lin", 0, [], null, true, false, false, []),
            new("tiquets_pagg", "SELECT t_pag.* FROM t_pag, tiquetl WHERE id_tiquetl=tiquetl", 0, [], null, true, false, false, []),
            new("arque", "SELECT a2.tienda FROM a2, A3", 0, [], null, true, false, false, [])
        };
        var tables = new AccessTableReference[]
        {
            new("t_pag", true, "T_PAG.TXT", null, AccessLinkKind.Text, null, true, false),
            new("t_lin", true, "T_LIN.TXT", null, AccessLinkKind.Text, null, true, false),
            new("a2", true, "A2.TXT", null, AccessLinkKind.Text, null, true, false),
            new("A3", true, "A3.TXT", null, AccessLinkKind.Text, null, true, false)
        };
        var expanded = AccessQueryNesting.Expand(["cobrado", "arque"], queries, tables);
        Assert.Contains(expanded, item => item.Name == "cobrado");
        Assert.Contains(expanded, item => item.Name == "tiquets_pagg");
        var arque = Assert.Single(expanded, item => item.Name == "arque");
        Assert.Contains("a2", arque.TablesInSql, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("A3", arque.TablesInSql, StringComparer.OrdinalIgnoreCase);
        var paguitos = AccessQueryNesting.Expand(
            ["paguitos"],
            [
                new("paguitos", "SELECT paguitos1.import*i_divises.factorpropia AS pagos FROM paguitos1, i_divises", 0, [], null, true, false, false, [])
            ],
            [
                new("paguitos1", true, "PAGUITOS1.TXT", null, AccessLinkKind.Text, null, true, false),
                new("pagos", true, "PAGOS", null, AccessLinkKind.AccessMdb, null, true, false)
            ]);
        var pagoQuery = Assert.Single(paguitos);
        Assert.DoesNotContain(pagoQuery.TablesInSql, item => item.Equals("pagos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DocumentsImportarClickAndFileLedger()
    {
        var analysis = AccessImportarFunctionalAnalyzer.Analyze(Source, [], []);
        var importar = Assert.Single(analysis.Procedures, item => item.Name == "Importar_Click");
        Assert.Contains("fichero", importar.Purpose, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Importar_Click", analysis.EntryProcedure);
        Assert.Contains("tiquets_sin_pago", analysis.WalkOrder);
        Assert.Contains("Seek exacto", analysis.FileProcessing.WhatPreventsReprocess[0], StringComparison.Ordinal);
        Assert.NotEmpty(analysis.ProcessFlow);
    }
}
