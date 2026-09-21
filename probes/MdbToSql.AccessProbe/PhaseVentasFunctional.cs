using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MdbToSql.AccessApplication;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessProbe;

internal static class PhaseVentasFunctional
{
    private static readonly Regex CredentialLine = new(
        @"\b(clave|password|pwd|passwd|contrase[nñ]a)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static int Run(string mdbPath)
    {
        Console.WriteLine("PROBE — análisis funcional de Importar_Click");
        Console.WriteLine(mdbPath);
        Console.WriteLine();

        try
        {
            var ventasJson = ProbeResults.JsonPath("ventas-analysis.json");
            var jsonPath = ProbeResults.JsonPath("ventas-functional-analysis.json");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            var result = new AccessVentasFunctionalProbe()
                .RunAsync(mdbPath, ventasJson, jsonPath)
                .GetAwaiter()
                .GetResult();
            File.WriteAllText(
                jsonPath,
                JsonSerializer.Serialize(result, JsonOptions),
                Encoding.UTF8);
            Console.WriteLine(Format(result));
            return result.OriginalIntegrityVerified
                && result.Errors.Count == 0
                && result.Procedures.Count > 0
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

    private static string Format(AccessImportarFunctionalResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("API: " + result.ApiUsed);
        builder.AppendLine("Módulo: " + result.ModuleName);
        builder.AppendLine("Entrada: " + result.EntryProcedure);
        builder.AppendLine("Procedimientos documentados: " + result.Procedures.Count);
        builder.AppendLine("QueryDefs (incluye anidadas): " + result.QueryDefs.Count);
        builder.AppendLine("Tablas tocadas: " + result.Tables.Count);
        builder.AppendLine();

        builder.AppendLine("ORDEN DEL GRAFO (ciclos no repetidos)");
        builder.AppendLine(string.Join(" → ", result.WalkOrder));
        builder.AppendLine();
        builder.AppendLine("ARISTAS");
        foreach (var edge in result.CallGraph)
        {
            builder.AppendLine(
                "- " + edge.From + " → " + edge.To + " L" + edge.Line + (edge.Cycle ? " CICLO" : string.Empty));
        }

        builder.AppendLine();
        builder.AppendLine("PROCESO COMPLETO");
        foreach (var step in result.ProcessFlow)
        {
            builder.AppendLine("- " + step);
        }

        builder.AppendLine();
        builder.AppendLine("REGISTRO EN fichero");
        builder.AppendLine("- Cuándo: " + result.FileProcessing.WhenRegistered);
        builder.AppendLine("- Antes:");
        foreach (var item in result.FileProcessing.BeforeRegistration)
        {
            builder.AppendLine("    * " + item);
        }

        builder.AppendLine("- Después:");
        foreach (var item in result.FileProcessing.AfterRegistration)
        {
            builder.AppendLine("    * " + item);
        }

        builder.AppendLine("- Si falla una operación intermedia:");
        foreach (var item in result.FileProcessing.IfIntermediateFails)
        {
            builder.AppendLine("    * " + item);
        }

        builder.AppendLine("- Qué evita reprocesar:");
        foreach (var item in result.FileProcessing.WhatPreventsReprocess)
        {
            builder.AppendLine("    * " + item);
        }

        builder.AppendLine("- Qué no evita reprocesar:");
        foreach (var item in result.FileProcessing.WhatDoesNotPreventReprocess)
        {
            builder.AppendLine("    * " + item);
        }

        builder.AppendLine();
        builder.AppendLine("PROCEDIMIENTOS");
        foreach (var procedure in result.Procedures)
        {
            builder.AppendLine();
            builder.AppendLine(
                procedure.Name + " (" + procedure.Kind + " L" + procedure.StartLine + "-" + procedure.EndLine + ")");
            builder.AppendLine("  Finalidad: " + procedure.Purpose);
            WriteList(builder, "Entradas", procedure.Inputs);
            WriteList(builder, "Condiciones", procedure.Conditions.Take(12).ToList());
            WriteList(builder, "Tablas leídas", procedure.TablesRead);
            WriteList(builder, "Tablas escritas", procedure.TablesWritten);
            WriteList(builder, "Consultas", procedure.Queries);
            WriteList(builder, "Transformaciones", procedure.Transformations.Take(8).ToList());
            WriteList(builder, "Archivos", procedure.Files.Take(12).ToList());
            WriteList(builder, "Errores", procedure.ErrorHandling);
            WriteList(builder, "Llamadas", procedure.InternalCalls);
            builder.AppendLine("  Evidencia:");
            foreach (var evidence in procedure.Evidence.Take(12))
            {
                builder.AppendLine("    L" + evidence.Line + " " + evidence.Kind + " " + Trim(Redact(evidence.Evidence), 160));
            }
        }

        builder.AppendLine();
        builder.AppendLine("QUERYDEFS (definición; no ejecutadas)");
        foreach (var query in result.QueryDefs)
        {
            builder.AppendLine(
                "- " + query.Name + " Type=" + query.TypeName +
                " From=" + string.Join(", ", query.UsedFrom));
            if (query.TablesInSql.Count > 0)
            {
                builder.AppendLine("    Tablas SQL: " + string.Join(", ", query.TablesInSql));
            }

            if (query.NestedQueriesPending.Count > 0)
            {
                builder.AppendLine("    Anidadas: " + string.Join(", ", query.NestedQueriesPending));
            }

            if (query.UnknownIdentifiers.Count > 0)
            {
                builder.AppendLine("    Identificadores pendientes: " + string.Join(", ", query.UnknownIdentifiers));
            }

            builder.AppendLine("    SQL: " + Trim(Redact(query.Sql ?? "(vacío)"), 220));
        }

        builder.AppendLine();
        builder.AppendLine("DEPENDENCIAS NO RESUELTAS");
        if (result.Pending.Count == 0)
        {
            builder.AppendLine("(ninguna)");
        }
        else
        {
            foreach (var item in result.Pending)
            {
                builder.AppendLine("- " + Redact(item));
            }
        }

        builder.AppendLine();
        builder.AppendLine("JSON: " + result.JsonPath);
        builder.AppendLine(
            "Original intacto: " + YesNo(result.OriginalIntegrityVerified) +
            (result.OriginalAfter is null
                ? string.Empty
                : " hash=" + result.OriginalAfter.Sha256 +
                  " size=" + result.OriginalAfter.Size +
                  " LastWriteTimeUtc=" + result.OriginalAfter.LastWriteUtc.ToString("O")));
        builder.AppendLine("Temporales eliminados: " + YesNo(result.TempsDeleted));
        builder.AppendLine("Sin Access propios pendientes: " + YesNo(result.NoNewAccessProcesses));
        foreach (var warning in result.Warnings)
        {
            builder.AppendLine("WARNING: " + Redact(warning));
        }

        foreach (var error in result.Errors)
        {
            builder.AppendLine("ERROR: " + Redact(error));
        }

        return builder.ToString();
    }

    private static void WriteList(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine("  " + title + ":");
        foreach (var item in items)
        {
            builder.AppendLine("    - " + Trim(Redact(item), 220));
        }
    }

    private static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value) || !CredentialLine.IsMatch(value))
        {
            return value ?? string.Empty;
        }

        return CredentialLine.Replace(value, "[redacted]");
    }

    private static string Trim(string value, int max)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ');
        return compact.Length <= max ? compact : compact[..max] + "...";
    }

    private static string YesNo(bool value) => value ? "sí" : "no";
}
