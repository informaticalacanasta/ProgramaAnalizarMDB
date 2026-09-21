using System.Text.Json;
using System.Text.Json.Serialization;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication;

public sealed class AccessVentasFunctionalProbe
{
    public const string ApiUsed =
        "results/ventas-analysis.json + DAO QueryDef.SQL anidadas (definición; sin Access.Application; sin ejecutar VBA/SQL/archivos)";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<AccessImportarFunctionalResult> RunAsync(
        string mdbPath,
        string ventasAnalysisPath,
        string jsonPath,
        CancellationToken cancellationToken = default)
    {
        return StaRunner.Run(
            () => Run(mdbPath, ventasAnalysisPath, jsonPath, cancellationToken),
            cancellationToken);
    }

    private static AccessImportarFunctionalResult Run(
        string mdbPath,
        string ventasAnalysisPath,
        string jsonPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var errors = new List<string>();
        if (!File.Exists(ventasAnalysisPath))
        {
            errors.Add("Falta " + ventasAnalysisPath + ". Ejecutar --ventas antes de este análisis.");
            return Empty(jsonPath, errors);
        }

        AccessVentasAnalysisResult? ventas;
        try
        {
            ventas = JsonSerializer.Deserialize<AccessVentasAnalysisResult>(
                File.ReadAllText(ventasAnalysisPath),
                JsonOptions);
        }
        catch (Exception exception)
        {
            errors.Add("No se leyó ventas-analysis.json: " + exception.Message);
            return Empty(jsonPath, errors);
        }

        if (string.IsNullOrWhiteSpace(ventas?.Source))
        {
            errors.Add("ventas-analysis.json no contiene el VBA de Form_ventas.");
            return Empty(jsonPath, errors);
        }

        var catalog = AccessDaoCatalogProbe.Load(mdbPath, cancellationToken);
        warnings.AddRange(catalog.Warnings);
        errors.AddRange(catalog.Errors);
        var analysis = AccessImportarFunctionalAnalyzer.Analyze(
            ventas.Source,
            catalog.Queries,
            catalog.Tables);
        warnings.AddRange(analysis.Pending.Where(item => item.Contains("SQL dinámico", StringComparison.Ordinal)));
        return new AccessImportarFunctionalResult(
            "Form_ventas",
            analysis.EntryProcedure,
            analysis.RequestedProcedures,
            analysis.CallGraph,
            analysis.WalkOrder,
            analysis.Procedures,
            analysis.QueryDefs,
            analysis.Tables,
            analysis.FileProcessing,
            analysis.ProcessFlow,
            analysis.Pending,
            ApiUsed,
            catalog.OriginalBefore,
            catalog.OriginalAfter,
            catalog.OriginalIntegrityVerified,
            catalog.TempsDeleted,
            catalog.NoNewAccessProcesses,
            jsonPath,
            warnings,
            errors);
    }

    private static AccessImportarFunctionalResult Empty(string jsonPath, List<string> errors)
    {
        return new AccessImportarFunctionalResult(
            "Form_ventas",
            AccessImportarFunctionalAnalyzer.Entry,
            AccessImportarFunctionalAnalyzer.RequestedProcedures,
            [],
            [],
            [],
            [],
            [],
            new AccessFileProcessingDoc("", [], [], [], [], []),
            [],
            [],
            ApiUsed,
            null,
            null,
            false,
            true,
            true,
            jsonPath,
            [],
            errors);
    }
}
