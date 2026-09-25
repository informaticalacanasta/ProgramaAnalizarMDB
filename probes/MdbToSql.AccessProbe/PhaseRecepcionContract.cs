using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MdbToSql.AccessApplication;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessProbe;

internal static class PhaseRecepcionContract
{
    public static int Run(string origenFolder)
    {
        Console.WriteLine("PROBE — contrato de datos de recepción TPV");
        Console.WriteLine(origenFolder);
        Console.WriteLine();

        try
        {
            var jsonPath = ProbeResults.JsonPath("recepcion-data-contract.json");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            var result = new AccessRecepcionContractProbe()
                .RunAsync(
                    origenFolder,
                    ProbeResults.JsonPath("ventas-analysis.json"),
                    ProbeResults.JsonPath("ventas-functional-analysis.json"),
                    jsonPath)
                .GetAwaiter()
                .GetResult();
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
            Console.WriteLine(Format(result));
            return result.Errors.Count == 0 && result.OriginalImportarIntact && result.Tables.Count > 0
                ? 0
                : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine("ERROR: " + exception);
            return 2;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string Format(AccessRecepcionDataContractResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("API: " + result.ApiUsed);
        builder.AppendLine();
        builder.AppendLine("MDB EN ORIGENMDB");
        foreach (var mdb in result.OrigenMdbs)
        {
            builder.AppendLine(
                "- " + mdb.FileName +
                " size=" + mdb.Integrity.Size +
                " local=" + mdb.LocalTables +
                " vinculadas=" + mdb.LinkedTables +
                " esquemas locales=" + mdb.LocalSchemas.Count +
                " intacto=" + YesNo(mdb.OriginalIntegrityVerified));
            builder.AppendLine("    " + mdb.SameVersionNote);
            foreach (var schema in mdb.LocalSchemas)
            {
                builder.AppendLine(
                    "    LOCAL " + schema.Name +
                    " campos=" + schema.Fields.Count +
                    " índices=" + schema.Indexes.Count +
                    " [" + string.Join(", ", schema.Fields.Select(item => item.Name)) + "]");
            }
        }

        var verified = result.Tables.Where(item => item.SchemaVerified).ToList();
        var pending = result.Tables.Where(item => !item.SchemaVerified).ToList();
        builder.AppendLine();
        builder.AppendLine("TABLAS CON ESQUEMA VERIFICADO (" + verified.Count + ")");
        foreach (var table in verified)
        {
            builder.AppendLine(
                "- " + table.Name +
                " @ " + (table.SchemaSourceMdb ?? "?") +
                (table.SameVersionUncertain ? " (versión incierta)" : string.Empty) +
                " campos=" + string.Join(", ", table.VerifiedFields.Select(item => item.Name)));
            builder.AppendLine("    Usada por: " + string.Join(", ", table.UsedBy));
        }

        builder.AppendLine();
        builder.AppendLine("TABLAS/QUERIES EXTERNAS O SIN ESQUEMA LOCAL (" + pending.Count + ")");
        foreach (var table in pending)
        {
            builder.AppendLine(
                "- " + table.Name +
                " [" + table.Kind + "] " +
                (table.OriginDatabase ?? "") +
                " Link=" + table.LinkKind +
                (table.IsUnc ? " UNC" : string.Empty));
            if (table.FieldsMentioned.Count > 0)
            {
                var names = table.FieldsMentioned
                    .Select(item => item.FieldName ?? ("Fields(" + item.Ordinal + ")=?"))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                builder.AppendLine("    Campos VBA (no verificados): " + string.Join(", ", names));
            }
        }

        builder.AppendLine();
        builder.AppendLine("FORMATOS TPV");
        foreach (var format in result.TpvFormats)
        {
            builder.AppendLine(
                "- " + format.TypeCode + " " + format.Pattern + " → " + format.ProcessedBy);
            builder.AppendLine("    Auxiliares: " + string.Join("; ", format.AuxiliaryFiles));
            builder.AppendLine("    Pendiente: " + string.Join(" ", format.Pending));
        }

        builder.AppendLine();
        builder.AppendLine("COMPORTAMIENTO LEGADO");
        foreach (var item in result.LegacyBehavior)
        {
            builder.AppendLine("- " + item);
        }

        builder.AppendLine();
        builder.AppendLine("POSIBLES DEFECTOS (no reproducir en .NET)");
        foreach (var item in result.PossibleDefects)
        {
            builder.AppendLine("- " + item);
        }

        builder.AppendLine();
        builder.AppendLine("DECISIONES PENDIENTES");
        foreach (var item in result.PendingDecisions)
        {
            builder.AppendLine("- " + item);
        }

        builder.AppendLine();
        builder.AppendLine("MATERIAL QUE FALTA");
        foreach (var item in result.MissingForReconstruction)
        {
            builder.AppendLine("- " + item);
        }

        builder.AppendLine();
        builder.AppendLine("JSON: " + result.JsonPath);
        builder.AppendLine("IMPORTAR intacto: " + YesNo(result.OriginalImportarIntact));
        foreach (var error in result.Errors)
        {
            builder.AppendLine("ERROR: " + error);
        }

        return builder.ToString();
    }

    private static string YesNo(bool value) => value ? "sí" : "no";
}
