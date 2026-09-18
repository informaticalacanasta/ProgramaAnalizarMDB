using MdbToSql.Core.Cli;
using MdbToSql.Core.Data;
using MdbToSql.Core.Exceptions;
using MdbToSql.Core.Import;
using MdbToSql.Core.Mapping;
using MdbToSql.Core.Models;
using MdbToSql.Core.Planning;
using MdbToSql.Core.Schema;
using MdbToSql.Core.Utilities;
using MdbToSql.Access;

namespace MdbToSql.Tests;

public sealed class IdentifierTests
{
    [Fact]
    public void QuoteIdentifier_EscapesClosingBracket()
    {
        Assert.Equal("[VENTAS 2024]]final]", SqlIdentifier.Quote("VENTAS 2024]final"));
        Assert.Equal("[VENTAS 2024]]final]", AccessIdentifier.Quote("VENTAS 2024]final"));
    }

    [Fact]
    public void DatabaseName_AcceptsAlphanumeric()
    {
        DatabaseNameValidator.EnsureValid("TPVONEMDB");
        DatabaseNameValidator.EnsureValid("OtraBD_1");
    }

    [Fact]
    public void DatabaseName_RejectsInjection()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseNameValidator.EnsureValid("TPV]; DROP DATABASE master;--"));
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseNameValidator.EnsureValid("TPV ONE"));
    }

    [Fact]
    public void StagingTableName_IncludesToken()
    {
        var name = StagingNames.StagingTable("articulos", "abc123");
        Assert.Equal("articulos__sabc123", name);
        Assert.NotEqual("articulos_staging", name);
    }

    [Fact]
    public void CanonicalIndexName_IsPrefixedByTable()
    {
        Assert.Equal("PK_articulos", StagingNames.CanonicalIndexName("articulos", "PrimaryKey", true));
        Assert.Equal("clientes_Nombre", StagingNames.CanonicalIndexName("clientes", "Nombre", false));
    }
}

public sealed class TypeMapperTests
{
    private readonly AccessToSqlTypeMapper _mapper = new();

    [Theory]
    [InlineData(17, AccessTypeKind.Byte, "tinyint")]
    [InlineData(2, AccessTypeKind.Integer, "smallint")]
    [InlineData(3, AccessTypeKind.Long, "int")]
    [InlineData(4, AccessTypeKind.Single, "real")]
    [InlineData(5, AccessTypeKind.Double, "float")]
    [InlineData(6, AccessTypeKind.Currency, "decimal(19,4)")]
    [InlineData(11, AccessTypeKind.Boolean, "bit")]
    [InlineData(7, AccessTypeKind.DateTime, "datetime2")]
    [InlineData(72, AccessTypeKind.Guid, "uniqueidentifier")]
    [InlineData(203, AccessTypeKind.Memo, "nvarchar(max)")]
    [InlineData(205, AccessTypeKind.LongBinary, "varbinary(max)")]
    public void ClassifierAndMapper_MapKnownOleDbTypes(
        int providerType,
        AccessTypeKind kind,
        string sql)
    {
        Assert.True(AccessTypeClassifier.TryClassify(providerType, 50, false, out var classified));
        Assert.Equal(kind, classified);
        var column = Column("c", classified, providerType, 50);
        Assert.Equal(sql, _mapper.Map(column).ToSql());
    }

    [Fact]
    public void Text_UsesDeclaredSize()
    {
        Assert.True(AccessTypeClassifier.TryClassify(202, 40, false, out var kind));
        Assert.Equal(AccessTypeKind.Text, kind);
        Assert.Equal("nvarchar(40)", _mapper.Map(Column("nombre", kind, 202, 40)).ToSql());
    }

    [Fact]
    public void UnknownType_ThrowsWithContext()
    {
        var exception = Assert.Throws<UnsupportedAccessTypeException>(() =>
            AccessTypeClassifier.ClassifyOrThrow(
                @"C:\tienda.mdb",
                "articulos",
                "FOTO",
                999,
                10,
                false,
                "WeirdType"));
        Assert.Contains("tienda.mdb", exception.Message, StringComparison.Ordinal);
        Assert.Contains("articulos", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FOTO", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WeirdType", exception.Message, StringComparison.Ordinal);
    }

    internal static AccessColumnSchema Column(
        string name,
        AccessTypeKind kind,
        int providerType,
        int? size = null,
        bool nullable = true,
        bool autoIncrement = false)
    {
        return new(name, kind, kind.ToString(), providerType, size, null, null, 0, nullable, autoIncrement);
    }
}

public sealed class PlanningTests
{
    [Fact]
    public void Deduplication_SkipsOnlyPreviousSuccess()
    {
        Assert.True(ImportDeduplicationPolicy.ShouldSkipAlreadyImported(true));
        Assert.False(ImportDeduplicationPolicy.ShouldSkipAlreadyImported(false));
        Assert.False(ImportDeduplicationPolicy.ShouldSkipAlreadyImported(true, forceImport: true));
    }

    [Fact]
    public void RowCountValidator_RequiresExactMatch()
    {
        Assert.True(RowCountValidator.Matches(132, 132));
        Assert.False(RowCountValidator.Matches(100, 99));
    }

    [Fact]
    public void CollisionDetector_FindsSameTableInDifferentMdbs()
    {
        var collisions = CollisionDetector.FindTableNameCollisions(
        [
            ("tienda01.mdb", "articulos"),
            ("tienda02.mdb", "articulos"),
            ("tienda01.mdb", "clientes")
        ]);

        var collision = Assert.Single(collisions);
        Assert.Equal("articulos", collision.TableName);
        Assert.Equal(["tienda01.mdb", "tienda02.mdb"], collision.MdbNames);
    }

    [Fact]
    public void ExistingTablePolicy_IsConservativeAcrossSources()
    {
        Assert.Equal(
            ExistingTableDecision.CreateNew,
            ExistingTablePolicy.Decide(false, "a.mdb", "H1", null, false));
        Assert.Equal(
            ExistingTableDecision.SkipAlreadyImported,
            ExistingTablePolicy.Decide(
                true,
                "a.mdb",
                "H1",
                new PreviousSuccessfulImport("a.mdb", "a.mdb", "H1"),
                false));
        Assert.Equal(
            ExistingTableDecision.ReplaceFromSameSource,
            ExistingTablePolicy.Decide(
                true,
                "a.mdb",
                "H2",
                new PreviousSuccessfulImport("a.mdb", "a.mdb", "H1"),
                false));
        Assert.Equal(
            ExistingTableDecision.CollisionDifferentSource,
            ExistingTablePolicy.Decide(
                true,
                "b.mdb",
                "H2",
                new PreviousSuccessfulImport("a.mdb", "a.mdb", "H1"),
                false));
        Assert.Equal(
            ExistingTableDecision.CollisionUnknownOrigin,
            ExistingTablePolicy.Decide(true, "a.mdb", "H1", null, false));
    }

    [Fact]
    public async Task SafeReplacement_DoesNotSwapWhenRowCountMismatches()
    {
        var schema = new AccessTableSchema("USUARIOS", [], [], 100);
        var ops = new FakeReplacementOperations("USUARIOS", 100)
        {
            StagingImportCount = 99
        };

        await Assert.ThrowsAsync<DataImportException>(() =>
            new SafeReplacementWorkflow().ReplaceOrCreateAsync(
                ops,
                schema,
                "USUARIOS",
                "__stg",
                "__old"));

        Assert.Equal(100, ops.Tables["USUARIOS"]);
        Assert.False(ops.Tables.ContainsKey("__stg"));
    }

    [Fact]
    public async Task SafeReplacement_ReplaceLeavesOriginalWhenSwapFails()
    {
        var schema = new AccessTableSchema("USUARIOS", [], [], 120);
        var ops = new FakeReplacementOperations("USUARIOS", 100)
        {
            StagingImportCount = 120,
            ThrowOnSwap = true
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SafeReplacementWorkflow().ReplaceOrCreateAsync(
                ops,
                schema,
                "USUARIOS",
                "__stg",
                "__old"));

        Assert.Equal(100, ops.Tables["USUARIOS"]);
        Assert.False(ops.Tables.ContainsKey("__stg"));
    }

    [Fact]
    public async Task SafeReplacement_ReplaceSwapsCompleteNewContents()
    {
        var schema = new AccessTableSchema("USUARIOS", [], [], 120);
        var ops = new FakeReplacementOperations("USUARIOS", 100)
        {
            StagingImportCount = 120
        };

        var imported = await new SafeReplacementWorkflow().ReplaceOrCreateAsync(
            ops,
            schema,
            "USUARIOS",
            "__stg",
            "__old");

        Assert.Equal(120, imported);
        Assert.Equal(120, ops.Tables["USUARIOS"]);
        Assert.False(ops.Tables.ContainsKey("__old"));
        Assert.False(ops.Tables.ContainsKey("__stg"));
    }

    [Fact]
    public void ProtectedTables_AreCentralized()
    {
        Assert.True(ProtectedInfrastructureTables.Contains("MdbImportRun"));
        Assert.True(ProtectedInfrastructureTables.Contains("SchemaMigrations"));
        Assert.False(ProtectedInfrastructureTables.Contains("articulos"));
    }

    [Fact]
    public void UniqueIndexNames_AreSuffixedForStaging()
    {
        var schema = new AccessTableSchema(
            "articulos",
            [TypeMapperTests.Column("id", AccessTypeKind.Long, 3, 4, false)],
            [new("PrimaryKey", true, true, [new("id", 1, false)])]);
        var sql = new SqlDdlBuilder(new AccessToSqlTypeMapper())
            .BuildCreateIndexSql(schema, "articulos__sabc", "abc");

        Assert.Contains("PK_articulos__abc", sql[0], StringComparison.Ordinal);
        Assert.DoesNotContain("CONSTRAINT [PK_articulos] ", sql[0], StringComparison.Ordinal);
    }

    private sealed class FakeReplacementOperations : IReplacementOperations
    {
        public Dictionary<string, long> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ThrowOnSwap { get; set; }
        public long StagingImportCount { get; set; } = 120;

        public FakeReplacementOperations(string existingName, long existingRows)
        {
            Tables[existingName] = existingRows;
        }

        public Task CreateEmptyTableAsync(
            string tableName,
            AccessTableSchema schema,
            CancellationToken cancellationToken)
        {
            Tables[tableName] = 0;
            return Task.CompletedTask;
        }

        public Task<long> CopyDataAsync(string tableName, CancellationToken cancellationToken)
        {
            Tables[tableName] = StagingImportCount;
            return Task.FromResult(StagingImportCount);
        }

        public Task CreateIndexesAsync(
            string tableName,
            AccessTableSchema schema,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<long> CountAsync(string tableName, CancellationToken cancellationToken)
        {
            return Task.FromResult(Tables[tableName]);
        }

        public Task<bool> ExistsAsync(string tableName, CancellationToken cancellationToken)
        {
            return Task.FromResult(Tables.ContainsKey(tableName));
        }

        public Task SwapAtomicAsync(
            string destinationTableName,
            string stagingTableName,
            string? backupTableName,
            CancellationToken cancellationToken)
        {
            if (ThrowOnSwap)
            {
                throw new InvalidOperationException("swap failed");
            }

            if (backupTableName is not null)
            {
                Tables[backupTableName] = Tables[destinationTableName];
                Tables.Remove(destinationTableName);
            }

            Tables[destinationTableName] = Tables[stagingTableName];
            Tables.Remove(stagingTableName);
            return Task.CompletedTask;
        }

        public Task DropIfExistsAsync(string tableName, CancellationToken cancellationToken)
        {
            Tables.Remove(tableName);
            return Task.CompletedTask;
        }
    }
}

public sealed class CommandLineTests
{
    [Fact]
    public void DefaultsToAnalyze()
    {
        var parsed = CommandLineParser.Parse([], new ImportSettings());
        Assert.Null(parsed.Error);
        Assert.Equal(ImportMode.Analyze, parsed.Settings!.Mode);
    }

    [Fact]
    public void ParsesImportSourceAndDatabase()
    {
        var parsed = CommandLineParser.Parse(
            ["--import", "--source", @"C:\Otra", "--database", "OtraBD"],
            new ImportSettings());
        Assert.Null(parsed.Error);
        Assert.Equal(ImportMode.Import, parsed.Settings!.Mode);
        Assert.Equal(@"C:\Otra", parsed.Settings.SourceDirectory);
        Assert.Equal("OtraBD", parsed.Settings.DestinationDatabase);
    }

    [Fact]
    public void RejectsBothModes()
    {
        var parsed = CommandLineParser.Parse(["--analyze", "--import"], new ImportSettings());
        Assert.Contains("solo --analyze o --import", parsed.Error, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class BooleanAndBoundedReaderTests
{
    [Theory]
    [InlineData((short)-1, true)]
    [InlineData((short)0, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void BooleanNormalizer_AcceptsJetShapes(object raw, bool expected)
    {
        Assert.Equal(expected, BooleanValueNormalizer.Normalize(raw));
    }

    [Fact]
    public void BooleanNormalizer_PreservesNull()
    {
        Assert.Equal(DBNull.Value, BooleanValueNormalizer.Normalize(DBNull.Value));
        Assert.Equal(DBNull.Value, BooleanValueNormalizer.Normalize(null));
    }

    [Fact]
    public void BoundedReader_ReadsAtMostMaxRows()
    {
        using var inner = new SequenceReader(6);
        using var first = new BoundedDbDataReader(inner, 2);
        Assert.True(first.Read());
        Assert.True(first.Read());
        Assert.False(first.Read());
        Assert.Equal(2, first.RowsReturned);
        Assert.False(first.SourceExhausted);
    }

    [Fact]
    public void SystemTables_AreFiltered()
    {
        Assert.True(AccessSchemaReader.IsSystemTable("MSysObjects"));
        Assert.True(AccessSchemaReader.IsSystemTable("~TMPCLP123"));
        Assert.False(AccessSchemaReader.IsSystemTable("articulos"));
    }
}
