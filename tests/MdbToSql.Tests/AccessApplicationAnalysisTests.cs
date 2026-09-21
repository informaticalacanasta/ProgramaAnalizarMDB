using MdbToSql.AccessApplication.Workspace;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Models;

namespace MdbToSql.Tests;

public sealed class AccessLinkClassifierTests
{
    [Fact]
    public void LocalTable_IsLocalKind()
    {
        var result = AccessLinkClassifier.Classify(connect: null, sourceTableName: null, isLinked: false);

        Assert.Equal(AccessLinkKind.Local, result.Kind);
        Assert.Null(result.SourcePath);
        Assert.False(result.IsUnc);
        Assert.True(result.IsLocal);
    }

    [Fact]
    public void AccessMdb_UncPath()
    {
        var connect = @"MS Access;DATABASE=\\Pedidos\Datos\tienda.mdb";
        var result = AccessLinkClassifier.Classify(connect, "CLIENTES", isLinked: true);

        Assert.Equal(AccessLinkKind.AccessMdb, result.Kind);
        Assert.Equal(@"\\Pedidos\Datos\tienda.mdb", result.SourcePath);
        Assert.True(result.IsUnc);
        Assert.False(result.IsLocal);
    }

    [Fact]
    public void AccessMdb_LocalPath()
    {
        var connect = @"MS Access;DATABASE=C:\TPVISION\DATOS\A1.mdb;";
        var result = AccessLinkClassifier.Classify(connect, "ARTICULOS", isLinked: true);

        Assert.Equal(AccessLinkKind.AccessMdb, result.Kind);
        Assert.Equal(@"C:\TPVISION\DATOS\A1.mdb", result.SourcePath);
        Assert.False(result.IsUnc);
        Assert.True(result.IsLocal);
    }

    [Fact]
    public void Odbc_WithoutInterBaseOrText_IsOdbc()
    {
        var connect = @"ODBC;DSN=Contabilidad;DATABASE=contab;";
        var result = AccessLinkClassifier.Classify(connect, "MOVIMIENTOS", isLinked: true);

        Assert.Equal(AccessLinkKind.Odbc, result.Kind);
        Assert.Equal("contab", result.SourcePath);
        Assert.False(result.IsUnc);
        Assert.False(result.IsLocal);
    }

    [Fact]
    public void InterBase_GdbWinsOverOdbc()
    {
        var connect = @"ODBC;DRIVER=InterBase;DATABASE=C:\TPVISION\DATOS\ipvmain.gdb;";
        var result = AccessLinkClassifier.Classify(connect, "PEDIDOS", isLinked: true);

        Assert.Equal(AccessLinkKind.InterBase, result.Kind);
        Assert.Equal(@"C:\TPVISION\DATOS\ipvmain.gdb", result.SourcePath);
        Assert.True(result.IsLocal);
        Assert.False(result.IsUnc);
    }

    [Fact]
    public void Text_TxtSource()
    {
        var connect = @"Text;DSN=A2;FMT=Delimited;DATABASE=C:\TPVISION\DATOS;";
        var result = AccessLinkClassifier.Classify(connect, "A2.TXT", isLinked: true);

        Assert.Equal(AccessLinkKind.Text, result.Kind);
        Assert.Equal(@"C:\TPVISION\DATOS", result.SourcePath);
        Assert.True(result.IsLocal);
        Assert.False(result.IsUnc);
    }

    [Fact]
    public void UnknownConnect_IsUnknown()
    {
        var result = AccessLinkClassifier.Classify("FooBar;", "X", isLinked: true);

        Assert.Equal(AccessLinkKind.Unknown, result.Kind);
        Assert.Null(result.SourcePath);
        Assert.False(result.IsUnc);
        Assert.False(result.IsLocal);
    }

    [Fact]
    public void SupervisoresUnc_IsRejectedAsFollowable_ButClassifiedOnly()
    {
        var connect = @"MS Access;DATABASE=\\Supervisores\share\otro.mdb";
        var result = AccessLinkClassifier.Classify(connect, "X", isLinked: true);

        Assert.Equal(AccessLinkKind.AccessMdb, result.Kind);
        Assert.True(result.IsUnc);
        Assert.False(result.IsLocal);
    }
}

public sealed class AccessStartupAndModelTests
{
    [Fact]
    public void Startup_AbsentPropertiesRemainNull()
    {
        var startup = new AccessStartupAnalysis(
            AutoExecExists: false,
            StartupForm: null,
            StartupShowDbWindow: null,
            StartupShowStatusBar: null,
            AllowFullMenus: null,
            AllowBuiltinToolbars: null,
            AllowBreakIntoCode: null,
            AllowSpecialKeys: null,
            AllowBypassKey: null);

        Assert.False(startup.AutoExecExists);
        Assert.Null(startup.StartupForm);
        Assert.Null(startup.AllowBypassKey);
        Assert.Null(AccessBooleanParser.Parse(null));
        Assert.Null(AccessBooleanParser.Parse(""));
        Assert.Null(AccessBooleanParser.Parse("maybe"));
    }

    [Fact]
    public void Startup_PresentPropertiesParse()
    {
        var startup = new AccessStartupAnalysis(
            AutoExecExists: true,
            StartupForm: "MENU",
            StartupShowDbWindow: AccessBooleanParser.Parse("False"),
            StartupShowStatusBar: AccessBooleanParser.Parse("True"),
            AllowFullMenus: AccessBooleanParser.Parse("-1"),
            AllowBuiltinToolbars: AccessBooleanParser.Parse("0"),
            AllowBreakIntoCode: AccessBooleanParser.Parse("1"),
            AllowSpecialKeys: AccessBooleanParser.Parse("false"),
            AllowBypassKey: AccessBooleanParser.Parse("true"));

        Assert.True(startup.AutoExecExists);
        Assert.Equal("MENU", startup.StartupForm);
        Assert.False(startup.StartupShowDbWindow);
        Assert.True(startup.StartupShowStatusBar);
        Assert.True(startup.AllowFullMenus);
        Assert.False(startup.AllowBuiltinToolbars);
        Assert.True(startup.AllowBreakIntoCode);
        Assert.False(startup.AllowSpecialKeys);
        Assert.True(startup.AllowBypassKey);
    }

    [Fact]
    public void RelationModel_SupportsReferentialIntegrityFlags()
    {
        var relation = new AccessRelationAnalysis(
            "FK_Pedidos_Clientes",
            "Clientes",
            "Pedidos",
            [new AccessRelationField("Codigo", "Cliente")],
            Attributes: 256 + 4096,
            ReferentialIntegrity: true,
            CascadeUpdate: true,
            CascadeDelete: true);

        Assert.Equal("Clientes", relation.PrimaryTable);
        Assert.Equal("Pedidos", relation.ForeignTable);
        Assert.True(relation.ReferentialIntegrity);
        Assert.True(relation.CascadeUpdate);
        Assert.True(relation.CascadeDelete);
        Assert.Equal("Codigo", Assert.Single(relation.Fields).Name);
    }

    [Fact]
    public void Printer_FormatsBaseAnalysis()
    {
        var analysis = CreateEmptyAnalysis("IMPORTAR.mdb", verified: true);
        var text = AccessApplicationAnalysisPrinter.Format(analysis);

        Assert.Contains("ANÁLISIS BASE", text, StringComparison.Ordinal);
        Assert.Contains("IMPORTAR.mdb", text, StringComparison.Ordinal);
        Assert.Contains("StartupForm: MENU", text, StringComparison.Ordinal);
        Assert.Contains("Original intacto:", text, StringComparison.Ordinal);
        Assert.Contains("sí", text, StringComparison.Ordinal);
    }

    internal static AccessApplicationAnalysis CreateEmptyAnalysis(string name, bool verified)
    {
        var snapshot = new FileIntegritySnapshot("ABC", 10, DateTime.UnixEpoch);
        return new AccessApplicationAnalysis(
            @"C:\local\" + name,
            name,
            "ABC",
            "3.0",
            snapshot,
            snapshot,
            verified,
            new AccessStartupAnalysis(false, "MENU", null, null, null, null, null, null, null),
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            0,
            0,
            [],
            [],
            []);
    }
}

public sealed class FileIntegrityTests
{
    [Fact]
    public void Capture_EqualsUnchangedFile()
    {
        using var directory = new TempDir();
        var path = Path.Combine(directory.Path, "sample.mdb");
        File.WriteAllText(path, "contenido");

        var before = FileIntegrity.Capture(path);
        var after = FileIntegrity.Capture(path);

        Assert.True(FileIntegrity.EqualsSnapshot(before, after));
        Assert.Equal(before.Size, after.Size);
        Assert.Equal(before.Sha256, after.Sha256, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(before.LastWriteUtc, after.LastWriteUtc);
    }

    [Fact]
    public void Capture_DetectsContentChange()
    {
        using var directory = new TempDir();
        var path = Path.Combine(directory.Path, "sample.mdb");
        File.WriteAllText(path, "antes");
        var before = FileIntegrity.Capture(path);

        File.WriteAllText(path, "despues");
        var after = FileIntegrity.Capture(path);

        Assert.False(FileIntegrity.EqualsSnapshot(before, after));
        Assert.NotEqual(before.Sha256, after.Sha256, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class AnalysisWorkspaceTests
{
    [Fact]
    public void CreateFrom_CopiesAndCleansUp()
    {
        using var directory = new TempDir();
        var original = Path.Combine(directory.Path, "original.mdb");
        File.WriteAllText(original, "mdb-bytes");

        string workspaceDir;
        using (var workspace = AnalysisWorkspace.CreateFrom(original))
        {
            workspaceDir = workspace.DirectoryPath;
            Assert.True(File.Exists(workspace.SourceCopyPath));
            Assert.True(File.Exists(workspace.AnalysisSafePath));
            Assert.Equal("source.mdb", Path.GetFileName(workspace.SourceCopyPath));
            Assert.Equal("analysis-safe.mdb", Path.GetFileName(workspace.AnalysisSafePath));
            Assert.Equal(File.ReadAllText(original), File.ReadAllText(workspace.SourceCopyPath));
            Assert.True(workspace.TryDelete(out var error), error);
            Assert.False(Directory.Exists(workspaceDir));
        }

        Assert.False(Directory.Exists(workspaceDir));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void EnsureLocal_RejectsUnc()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AnalysisWorkspace.EnsureLocal(@"\\Pedidos\Datos\IMPORTAR.mdb", "MDB original"));
        Assert.Contains("UNC", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ApplicationAnalysisCoordinatorTests
{
    [Fact]
    public async Task Analyze_RejectsUncPath()
    {
        var analyzer = new FakeAnalyzer();
        var coordinator = new ApplicationAnalysisCoordinator(analyzer);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.AnalyzeAsync(@"\\Supervisores\share\IMPORTAR.mdb"));
        Assert.Contains("UNC", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, analyzer.Calls);
    }

    [Fact]
    public async Task Analyze_RejectsMissingFile()
    {
        var analyzer = new FakeAnalyzer();
        var coordinator = new ApplicationAnalysisCoordinator(analyzer);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => coordinator.AnalyzeAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mdb")));
        Assert.Equal(0, analyzer.Calls);
    }

    [Fact]
    public async Task Analyze_DelegatesToAnalyzer()
    {
        using var directory = new TempDir();
        var path = Path.Combine(directory.Path, "app.mdb");
        File.WriteAllText(path, "x");
        var expected = AccessStartupAndModelTests.CreateEmptyAnalysis("app.mdb", verified: true);
        var analyzer = new FakeAnalyzer { Result = expected };
        var coordinator = new ApplicationAnalysisCoordinator(analyzer);

        var result = await coordinator.AnalyzeAsync(path);

        Assert.Same(expected, result);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(Path.GetFullPath(path), analyzer.LastPath);
    }

    [Fact]
    public void UnsafeStartupException_ListsLoadedObjects()
    {
        var exception = new UnsafeAccessStartupException(["MENU"], ["Listado"]);
        Assert.Contains("MENU", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Listado", exception.Message, StringComparison.Ordinal);
        Assert.Equal("MENU", Assert.Single(exception.LoadedForms));
    }

    private sealed class FakeAnalyzer : IAccessApplicationAnalyzer
    {
        public int Calls { get; private set; }

        public string? LastPath { get; private set; }

        public AccessApplicationAnalysis? Result { get; init; }

        public Task<AccessApplicationAnalysis> AnalyzeAsync(
            string mdbPath,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPath = mdbPath;
            return Task.FromResult(Result ?? AccessStartupAndModelTests.CreateEmptyAnalysis("x.mdb", true));
        }
    }
}
