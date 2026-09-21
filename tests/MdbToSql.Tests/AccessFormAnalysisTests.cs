using System.Text.Json;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.Tests;

public sealed class AccessFormClassifierTests
{
    [Theory]
    [InlineData(100, AccessControlTypeKind.Label, "Label")]
    [InlineData(109, AccessControlTypeKind.TextBox, "TextBox")]
    [InlineData(104, AccessControlTypeKind.CommandButton, "CommandButton")]
    [InlineData(111, AccessControlTypeKind.ComboBox, "ComboBox")]
    [InlineData(110, AccessControlTypeKind.ListBox, "ListBox")]
    [InlineData(106, AccessControlTypeKind.CheckBox, "CheckBox")]
    [InlineData(105, AccessControlTypeKind.OptionButton, "OptionButton")]
    [InlineData(122, AccessControlTypeKind.ToggleButton, "ToggleButton")]
    [InlineData(107, AccessControlTypeKind.OptionGroup, "OptionGroup")]
    [InlineData(112, AccessControlTypeKind.SubForm, "SubForm")]
    [InlineData(123, AccessControlTypeKind.TabControl, "TabControl")]
    [InlineData(124, AccessControlTypeKind.Page, "Page")]
    [InlineData(103, AccessControlTypeKind.Image, "Image")]
    [InlineData(108, AccessControlTypeKind.BoundObjectFrame, "BoundObjectFrame")]
    [InlineData(114, AccessControlTypeKind.UnboundObjectFrame, "UnboundObjectFrame")]
    [InlineData(102, AccessControlTypeKind.Line, "Line")]
    [InlineData(101, AccessControlTypeKind.Rectangle, "Rectangle")]
    public void ControlType_KnownValues(int raw, AccessControlTypeKind kind, string name)
    {
        var classified = AccessControlTypeClassifier.Classify(raw);
        Assert.Equal(kind, classified.Kind);
        Assert.Equal(name, classified.Name);
    }

    [Fact]
    public void ControlType_UnknownKeepsRaw()
    {
        var classified = AccessControlTypeClassifier.Classify(999);
        Assert.Equal(AccessControlTypeKind.Unknown, classified.Kind);
        Assert.Equal("Unknown", classified.Name);
        var control = new AccessControlAnalysis(
            "x",
            classified.Kind,
            classified.Name,
            999,
            ParentName: null,
            Section: null,
            Left: null,
            Top: null,
            Width: null,
            Height: null,
            Visible: null,
            Enabled: null,
            Locked: null,
            TabIndex: null,
            TabStop: null,
            Caption: null,
            ControlSource: null,
            IsExpression: false,
            DefaultValue: null,
            Format: null,
            DecimalPlaces: null,
            InputMask: null,
            ValidationRule: null,
            ValidationText: null,
            StatusBarText: null,
            Tag: null,
            RowSource: null,
            RowSourceType: null,
            RowSourceKind: null,
            BoundColumn: null,
            ColumnCount: null,
            ColumnWidths: null,
            ColumnHeads: null,
            LimitToList: null,
            ListRows: null,
            SourceObject: null,
            LinkMasterFields: null,
            LinkChildFields: null,
            AttachedControl: null,
            PageIndex: null,
            Events: [],
            Warnings: []);
        Assert.Equal(999, control.RawControlType);
        Assert.Null(control.Caption);
        Assert.Null(control.ControlSource);
    }

    [Theory]
    [InlineData(null, AccessEventBindingKind.None)]
    [InlineData("", AccessEventBindingKind.None)]
    [InlineData("[Event Procedure]", AccessEventBindingKind.EventProcedure)]
    [InlineData("=RecalcularTotal()", AccessEventBindingKind.Expression)]
    [InlineData("MiMacro", AccessEventBindingKind.Macro)]
    public void EventBinding_Classifies(string? expression, AccessEventBindingKind kind)
    {
        Assert.Equal(kind, AccessEventBindingClassifier.Classify(expression));
    }

    [Fact]
    public void RecordSource_ClassifiesTableQuerySqlAndEmpty()
    {
        var tables = new[] { "Clientes", "Ventas" };
        var queries = new[] { "qryVentas", "qryClientes" };
        Assert.Equal(AccessRecordSourceKind.Empty, AccessRecordSourceClassifier.Classify(null, tables, queries));
        Assert.Equal(AccessRecordSourceKind.Table, AccessRecordSourceClassifier.Classify("Clientes", tables, queries));
        Assert.Equal(AccessRecordSourceKind.SavedQuery, AccessRecordSourceClassifier.Classify("qryVentas", tables, queries));
        Assert.Equal(
            AccessRecordSourceKind.SqlText,
            AccessRecordSourceClassifier.Classify("SELECT * FROM Clientes", tables, queries));
        Assert.Equal(AccessRecordSourceKind.Unknown, AccessRecordSourceClassifier.Classify("NoExiste", tables, queries));
    }

    [Fact]
    public void RowSource_RespectsValueListAndSavedQuery()
    {
        var tables = new[] { "Clientes" };
        var queries = new[] { "qryClientes" };
        Assert.Equal(
            AccessRowSourceKind.ValueList,
            AccessRecordSourceClassifier.ClassifyRowSource("A;B;C", "Value List", tables, queries));
        Assert.Equal(
            AccessRowSourceKind.SavedQuery,
            AccessRecordSourceClassifier.ClassifyRowSource("qryClientes", "Table/Query", tables, queries));
        Assert.Equal(
            AccessRowSourceKind.SqlText,
            AccessRecordSourceClassifier.ClassifyRowSource("SELECT Codigo FROM Clientes", "Table/Query", tables, queries));
        Assert.Equal(
            AccessRowSourceKind.Empty,
            AccessRecordSourceClassifier.ClassifyRowSource(" ", "Table/Query", tables, queries));
    }

    [Theory]
    [InlineData("=[Cantidad]*[Precio]", true)]
    [InlineData("=DLookup(\"x\",\"y\")", true)]
    [InlineData("CalcularImporte(", true)]
    [InlineData("Cantidad", false)]
    [InlineData(null, false)]
    public void ExpressionDetection(string? value, bool expected)
    {
        Assert.Equal(expected, AccessExpressionDetector.IsExpression(value));
    }

    [Fact]
    public void SubFormDependency_UsesSourceObjectEvidence()
    {
        var parsed = AccessSourceObjectParser.Parse("Form.frmDetalle");
        var dependency = new AccessDependency(
            "MENU",
            AccessObjectKind.Form,
            AccessDependencyKind.SourceObject,
            parsed.Name,
            parsed.Kind,
            "subDetalle.SourceObject = \"Form.frmDetalle\"");
        Assert.Equal("frmDetalle", dependency.TargetObject);
        Assert.Equal(AccessObjectKind.Form, dependency.TargetKind);
        Assert.Contains("SourceObject", dependency.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysisOrder_SimpleThenMenuThenRest()
    {
        var inventory = new[]
        {
            AccessFormAnalysis.Unanalyzed("ZETA", true, false),
            AccessFormAnalysis.Unanalyzed("MENU", true, false),
            AccessFormAnalysis.Unanalyzed("alpha", false, false)
        };
        var order = AccessFormAnalysisOrder.Resolve(inventory);
        Assert.Equal(["alpha", "MENU", "ZETA"], order);
    }

    [Fact]
    public void JsonSerializer_SerializesFormAnalysisWithoutCycles()
    {
        var form = AccessFormAnalysis.Unanalyzed("MENU", true, false) with
        {
            Caption = "Menú",
            Controls =
            [
                new AccessControlAnalysis(
                    "cmdVentas",
                    AccessControlTypeKind.CommandButton,
                    "CommandButton",
                    104,
                    "MENU",
                    "Detail",
                    1,
                    2,
                    3,
                    4,
                    true,
                    true,
                    null,
                    0,
                    true,
                    "Ventas",
                    null,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    [
                        new AccessEventBinding(
                            "cmdVentas",
                            AccessObjectKind.Control,
                            "OnClick",
                            AccessEventBindingKind.EventProcedure,
                            "[Event Procedure]")
                    ],
                    [])
            ],
            Events =
            [
                new AccessEventBinding(
                    "MENU",
                    AccessObjectKind.Form,
                    "OnLoad",
                    AccessEventBindingKind.EventProcedure,
                    "[Event Procedure]")
            ]
        };
        var analysis = AccessStartupAndModelTests.CreateEmptyAnalysis("IMPORTAR.mdb", true) with
        {
            Forms = [form]
        };

        var json = JsonSerializer.Serialize(analysis);
        Assert.Contains("\"Name\":\"MENU\"", json, StringComparison.Ordinal);
        Assert.Contains("cmdVentas", json, StringComparison.Ordinal);
        Assert.Contains("OnClick", json, StringComparison.Ordinal);
        Assert.DoesNotContain("$ref", json, StringComparison.OrdinalIgnoreCase);
    }
}
