using System.Text.Json;
using System.Text.Json.Serialization;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessApplication;

public sealed class AccessRecepcionContractProbe
{
    public const string ApiUsed =
        "ventas-functional-analysis.json + DAO TableDefs/Fields locales en copias ORIGENMDB (sin abrir Connect/UNC/Text; sin ejecutar)";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<AccessRecepcionDataContractResult> RunAsync(
        string origenFolder,
        string ventasAnalysisPath,
        string ventasFunctionalPath,
        string jsonPath,
        CancellationToken cancellationToken = default)
    {
        return StaRunner.Run(
            () => Run(origenFolder, ventasAnalysisPath, ventasFunctionalPath, jsonPath, cancellationToken),
            cancellationToken);
    }

    private static AccessRecepcionDataContractResult Run(
        string origenFolder,
        string ventasAnalysisPath,
        string ventasFunctionalPath,
        string jsonPath,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        if (!Directory.Exists(origenFolder))
        {
            errors.Add("No existe la carpeta ORIGENMDB: " + origenFolder);
            return Empty(jsonPath, errors);
        }

        if (!File.Exists(ventasAnalysisPath) || !File.Exists(ventasFunctionalPath))
        {
            errors.Add("Faltan results/ventas-analysis.json o ventas-functional-analysis.json.");
            return Empty(jsonPath, errors);
        }

        AccessVentasAnalysisResult? ventas;
        AccessImportarFunctionalResult? functional;
        try
        {
            ventas = JsonSerializer.Deserialize<AccessVentasAnalysisResult>(
                File.ReadAllText(ventasAnalysisPath), JsonOptions);
            functional = JsonSerializer.Deserialize<AccessImportarFunctionalResult>(
                File.ReadAllText(ventasFunctionalPath), JsonOptions);
        }
        catch (Exception exception)
        {
            errors.Add("No se deserializaron los JSON de ventas: " + exception.Message);
            return Empty(jsonPath, errors);
        }

        if (ventas is null
            || string.IsNullOrWhiteSpace(ventas.Source)
            || functional is null
            || functional.Procedures.Count == 0)
        {
            errors.Add("Los JSON de ventas no contienen VBA o procedimientos.");
            return Empty(jsonPath, errors);
        }

        var inventories = new List<AccessOrigenMdbInventory>();
        AccessDaoCatalogResult? importarCatalog = null;
        FileIntegritySnapshot? importarIntegrity = null;
        var importarIntact = true;
        foreach (var file in Directory.GetFiles(origenFolder, "*.mdb").OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var catalog = AccessDaoCatalogProbe.Load(file, cancellationToken);
            warnings.AddRange(catalog.Warnings.Select(item => Path.GetFileName(file) + ": " + item));
            errors.AddRange(catalog.Errors.Select(item => Path.GetFileName(file) + ": " + item));
            var name = Path.GetFileName(file);
            var note = name.Equals("IMPORTAR.mdb", StringComparison.OrdinalIgnoreCase)
                ? "Frontend analizado. Las TableDefs vinculadas no se abrieron."
                : "Copia física en ORIGENMDB. No se demuestra que coincida con c:\\programas\\... ni con \\\\supervisores\\... usados en runtime.";
            inventories.Add(new AccessOrigenMdbInventory(
                name,
                Path.GetFullPath(file),
                catalog.OriginalAfter,
                catalog.OriginalIntegrityVerified,
                catalog.TempsDeleted,
                catalog.Tables.Count(item => !item.IsLinked),
                catalog.Tables.Count(item => item.IsLinked),
                catalog.Tables,
                catalog.LocalSchemas,
                note,
                catalog.Warnings,
                catalog.Errors));
            if (name.Equals("IMPORTAR.mdb", StringComparison.OrdinalIgnoreCase))
            {
                importarCatalog = catalog;
                importarIntegrity = catalog.OriginalAfter;
                importarIntact = catalog.OriginalIntegrityVerified;
            }

            if (name.Equals("zetas.mdb", StringComparison.OrdinalIgnoreCase)
                && catalog.Queries.Any(item => item.Name.Equals("arque", StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add("ORIGENMDB\\zetas.mdb contiene QueryDef 'arque' (definición leída; no ejecutada; versión incierta).");
            }
        }

        var analysis = AccessRecepcionContractAnalyzer.Analyze(
            ventas.Source,
            functional.Procedures,
            functional.QueryDefs,
            importarCatalog?.Tables ?? [],
            inventories);
        return new AccessRecepcionDataContractResult(
            inventories,
            analysis.Tables,
            analysis.TpvFormats,
            analysis.LegacyBehavior,
            analysis.PossibleDefects,
            analysis.PendingDecisions,
            analysis.MissingForReconstruction,
            ApiUsed,
            jsonPath,
            importarIntact,
            importarIntegrity,
            warnings,
            errors);
    }

    private static AccessRecepcionDataContractResult Empty(string jsonPath, List<string> errors)
    {
        return new AccessRecepcionDataContractResult(
            [],
            [],
            AccessTpvFormatCatalog.Document(),
            [],
            [],
            [],
            [],
            ApiUsed,
            jsonPath,
            false,
            null,
            [],
            errors);
    }
}
