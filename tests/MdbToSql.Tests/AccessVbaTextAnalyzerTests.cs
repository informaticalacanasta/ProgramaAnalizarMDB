using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.Tests;

public sealed class AccessVbaTextAnalyzerTests
{
    private const string Source = """
        Option Compare Database

        Private Sub Form_Open(Cancel As Integer)
            DoCmd.OpenForm "ventas"
        End Sub

        Private Sub recepcion_Click()
            Call Recibir
            DoCmd.OpenForm nombreForm
            Recibir
        End Sub

        Private Function Recibir() As Integer
            CurrentDb.Execute "DELETE FROM temp"
            MsgBox "ok"
        End Function

        Public Property Get Total() As Currency
            Total = 0
        End Property
        """;

    [Fact]
    public void ParsesSubFunctionAndProperty()
    {
        var procedures = AccessVbaTextAnalyzer.ParseProcedures(Source);
        Assert.Equal(4, procedures.Count);
        Assert.Equal("Sub", procedures[0].Kind);
        Assert.Equal("Form_Open", procedures[0].Name);
        Assert.Equal(3, procedures[0].StartLine);
        Assert.Equal(5, procedures[0].EndLine);
        Assert.Contains(procedures, item => item is { Kind: "Function", Name: "Recibir" });
        Assert.Contains(procedures, item => item is { Kind: "Property Get", Name: "Total" });
    }

    [Fact]
    public void ResolvesControlClickOnlyWhenProcedureExists()
    {
        var procedures = AccessVbaTextAnalyzer.ParseProcedures(Source);
        var resolved = AccessEventProcedureNames.Bind(
            "recepcion",
            AccessObjectKind.Control,
            "OnClick",
            "[Event Procedure]",
            eventProcPrefix: null,
            "MENU",
            procedures);
        Assert.True(resolved.Resolved);
        Assert.Equal("recepcion_Click", resolved.ResolvedProcedure);

        var missing = AccessEventProcedureNames.Bind(
            "salir",
            AccessObjectKind.Control,
            "OnClick",
            "[Event Procedure]",
            null,
            "MENU",
            procedures);
        Assert.False(missing.Resolved);
        Assert.Contains("salir_Click", missing.CandidateProcedures);
    }

    [Fact]
    public void DetectsDoCmdAndDynamicOpenForm()
    {
        var procedures = AccessVbaTextAnalyzer.ParseProcedures(Source);
        var references = AccessVbaTextAnalyzer.ParseReferences(Source, procedures);
        Assert.Contains(references, item => item is { Kind: "DoCmd.OpenForm", Target: "ventas", Dynamic: false });
        Assert.Contains(references, item => item.Kind == "DoCmd.OpenForm" && item.Dynamic);
        Assert.Contains(references, item => item is { Kind: "CurrentDb.Execute", Dynamic: false });
        Assert.Contains(references, item => item is { Kind: "SameModule", Target: "Recibir" });
        Assert.Contains(references, item => item is { Kind: "Call", Target: "Recibir" } or { Kind: "SameModule", Target: "Recibir" });
    }

    [Fact]
    public void IgnoresCommentedProcedureHeaders()
    {
        var procedures = AccessVbaTextAnalyzer.ParseProcedures("' Private Sub Fake_Click()\r\nPrivate Sub Real_Click()\r\nEnd Sub\r\n");
        Assert.Equal("Real_Click", Assert.Single(procedures).Name);
    }

    private const string TerminalModule = """
        Option Compare Database
        Option Explicit
        Global OPERADOR As String

        Public Function Otra() As Integer
            Otra = 1
        End Function

        Public Sub terminal()
            Call Otra
            OPERADOR = InputBox("Operador")
            If OPERADOR = "" Then
                MsgBox "vacio"
            End If
            Input #1, OPERADOR
        End Sub
        """;

    [Fact]
    public void FindsProcedureByNameIgnoringCaseAndIgnoresCalls()
    {
        var source = """
            Call terminal
            ' Sub terminal()
            Public Sub Terminal()
            End Sub
            """;
        var found = AccessVbaTextAnalyzer.FindByName(source, "terminal");
        var procedure = Assert.Single(found);
        Assert.Equal("Sub", procedure.Kind);
        Assert.Equal("Terminal", procedure.Name);
        Assert.Equal(3, procedure.StartLine);
        Assert.Equal(4, procedure.EndLine);
    }

    [Fact]
    public void ExtractsDeclarationsRangeAndSignature()
    {
        var found = Assert.Single(AccessVbaTextAnalyzer.FindByName(TerminalModule, "terminal"));
        var declarations = AccessVbaTextAnalyzer.ExtractDeclarations(TerminalModule);
        Assert.Contains("Global OPERADOR As String", declarations, StringComparison.Ordinal);
        Assert.DoesNotContain("Public Sub terminal", declarations, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "Public Sub terminal()",
            AccessVbaTextAnalyzer.Signature(TerminalModule, found));
        var body = AccessVbaTextAnalyzer.ExtractRange(TerminalModule, found.StartLine, found.EndLine);
        Assert.StartsWith("Public Sub terminal()", body, StringComparison.Ordinal);
        Assert.EndsWith("End Sub", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesFactsForOperadorUiAndCalls()
    {
        var found = Assert.Single(AccessVbaTextAnalyzer.FindByName(TerminalModule, "terminal"));
        var facts = AccessVbaTextAnalyzer.ParseFacts(TerminalModule, found.StartLine, found.EndLine);
        Assert.Contains(facts, item => item is { Kind: "GlobalWrite", Target: "OPERADOR", Line: 11 });
        Assert.Contains(facts, item => item is { Kind: "GlobalRead", Target: "OPERADOR", Line: 12 });
        Assert.Contains(facts, item => item is { Kind: "GlobalWrite", Target: "OPERADOR", Line: 15 });
        Assert.Contains(facts, item => item.Kind == "UserPrompt");
        Assert.Contains(facts, item => item.Kind == "UserMessage");
        Assert.Contains(facts, item => item is { Kind: "FileIo", Line: 15 });
        var references = AccessVbaTextAnalyzer.InRange(
            AccessVbaTextAnalyzer.ParseReferences(TerminalModule, AccessVbaTextAnalyzer.ParseProcedures(TerminalModule)),
            found.StartLine,
            found.EndLine);
        Assert.Contains(references, item => item is { Kind: "Call", Target: "Otra" } or { Kind: "SameModule", Target: "Otra" });
    }
}
