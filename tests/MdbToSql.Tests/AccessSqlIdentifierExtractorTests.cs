using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.Tests;

public sealed class AccessSqlIdentifierExtractorTests
{
    [Fact]
    public void ExtractsFromJoinUpdateAndInsert()
    {
        var names = AccessSqlIdentifierExtractor.Extract(
            """
            SELECT a.id FROM articulos AS a
            INNER JOIN [detalle ventas] ON a.id = [detalle ventas].id
            """);
        Assert.Contains("articulos", names);
        Assert.Contains("detalle ventas", names);

        var update = AccessSqlIdentifierExtractor.Extract("UPDATE pedidos SET x=1");
        Assert.Equal("pedidos", Assert.Single(update));

        var insert = AccessSqlIdentifierExtractor.Extract("INSERT INTO temp SELECT * FROM local");
        Assert.Contains("temp", insert);
        Assert.Contains("local", insert);
    }

    [Fact]
    public void ResolveDirectQueryAndPendingNested()
    {
        var form = AccessFormAnalysis.Unanalyzed("ventas", true, false) with
        {
            RecordSource = "q_ventas",
            RecordSourceKind = AccessRecordSourceKind.SavedQuery,
            Controls =
            [
                new AccessControlAnalysis(
                    "cbo",
                    AccessControlTypeKind.ComboBox,
                    "ComboBox",
                    111,
                    ParentName: null,
                    Section: null,
                    Left: null,
                    Top: null,
                    Width: null,
                    Height: null,
                    Visible: true,
                    Enabled: true,
                    Locked: false,
                    TabIndex: 0,
                    TabStop: true,
                    Caption: null,
                    ControlSource: "id",
                    IsExpression: false,
                    DefaultValue: null,
                    Format: null,
                    DecimalPlaces: null,
                    InputMask: null,
                    ValidationRule: null,
                    ValidationText: null,
                    StatusBarText: null,
                    Tag: null,
                    RowSource: "clientes",
                    RowSourceType: "Table/Query",
                    RowSourceKind: AccessRowSourceKind.Table,
                    BoundColumn: 1,
                    ColumnCount: 1,
                    ColumnWidths: null,
                    ColumnHeads: false,
                    LimitToList: true,
                    ListRows: 8,
                    SourceObject: null,
                    LinkMasterFields: null,
                    LinkChildFields: null,
                    AttachedControl: null,
                    PageIndex: null,
                    Events: [],
                    Warnings: [])
            ]
        };
        var queries = new[]
        {
            new AccessQueryAnalysis(
                "q_ventas",
                "SELECT * FROM tickets INNER JOIN q_nested ON tickets.id = q_nested.id",
                Type: 0,
                Parameters: [],
                Connect: null,
                ReturnsRecords: true,
                IsSystem: false,
                IsHidden: false,
                Warnings: []),
            new AccessQueryAnalysis(
                "q_nested",
                "SELECT * FROM otros",
                Type: 0,
                Parameters: [],
                Connect: null,
                ReturnsRecords: true,
                IsSystem: false,
                IsHidden: false,
                Warnings: [])
        };
        var tables = new[]
        {
            new AccessTableReference("tickets", false, null, null, AccessLinkKind.Local, null, false, true),
            new AccessTableReference("clientes", true, "CLIENTES", @"DATABASE=\\x\y.mdb", AccessLinkKind.AccessMdb, @"\\x\y.mdb", true, false)
        };

        var resolved = AccessDirectReferenceResolver.Resolve(form, [], queries, tables);
        Assert.Contains(resolved.Queries, item => item.Name == "q_ventas");
        Assert.DoesNotContain(resolved.Queries, item => item.Name == "q_nested");
        Assert.Contains(resolved.Tables, item => item is { Name: "tickets", IsLinked: false });
        Assert.Contains(resolved.Tables, item => item is { Name: "clientes", IsLinked: true, IsUnc: true });
        Assert.Contains(resolved.Pending, item => item.Contains("q_nested", StringComparison.OrdinalIgnoreCase));
    }
}
