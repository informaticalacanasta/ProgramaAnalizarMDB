using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MdbToSql.AccessApplication;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessProbe;

internal static class PhaseMenuVba
{
    public static int Run(string mdbPath)
    {
        Console.WriteLine("PROBE — VBA del formulario MENU");
        Console.WriteLine(mdbPath);
        Console.WriteLine();

        try
        {
            var jsonPath = ProbeResults.JsonPath("menu-vba.json");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            var result = new AccessMenuVbaProbe()
                .RunAsync(mdbPath, jsonPath)
                .GetAwaiter()
                .GetResult();
            File.WriteAllText(
                jsonPath,
                JsonSerializer.Serialize(result, JsonOptions),
                Encoding.UTF8);
            Console.WriteLine(Format(result));
            return result.OriginalIntegrityVerified
                && result.Errors.Count == 0
                && result.Source is not null
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

    private static string Format(AccessMenuVbaProbeResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Formulario: {result.FormName}");
        builder.AppendLine($"API: {result.ApiUsed}");
        builder.AppendLine($"HasModule: {FormatFlag(result.HasModule)}");
        builder.AppendLine($"Módulo: {result.ModuleName ?? "(null)"}");
        builder.AppendLine($"Líneas: {result.LineCount?.ToString() ?? "(null)"}");
        builder.AppendLine($"VBA leído: {(result.Source is null ? "no" : "sí")}");
        builder.AppendLine($"Abrió Design View: {(result.OpenedInDesignView ? "sí" : "no")}");
        builder.AppendLine($"Cerró acSaveNo: {(result.ClosedCleanly ? "sí" : "no")}");
        builder.AppendLine();
        builder.AppendLine("PROCEDIMIENTOS");
        if (result.Procedures.Count == 0)
        {
            builder.AppendLine("(ninguno)");
        }
        else
        {
            foreach (var procedure in result.Procedures)
            {
                builder.AppendLine(
                    $"- {procedure.Kind} {procedure.Name} (líneas {procedure.StartLine}-{procedure.EndLine})");
            }
        }

        builder.AppendLine();
        builder.AppendLine("BINDINGS");
        foreach (var binding in result.Bindings)
        {
            var status = binding.Resolved
                ? binding.ResolvedProcedure
                : "NO RESUELTO [" + string.Join(", ", binding.CandidateProcedures) + "]";
            builder.AppendLine($"{binding.ObjectName}.{binding.EventName} = {binding.BindingExpression} → {status}");
        }

        builder.AppendLine();
        builder.AppendLine("REFERENCIAS INMEDIATAS");
        if (result.References.Count == 0)
        {
            builder.AppendLine("(ninguna)");
        }
        else
        {
            foreach (var reference in result.References)
            {
                builder.AppendLine(
                    $"- L{reference.Line} {reference.Kind} {(reference.Dynamic ? "(dinámico) " : string.Empty)}" +
                    $"{reference.Target ?? ""}");
                builder.AppendLine($"    {reference.Evidence}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("JSON: " + result.JsonPath);
        builder.AppendLine($"Original intacto: {(result.OriginalIntegrityVerified ? "sí" : "NO")}");
        builder.AppendLine($"Temporales eliminados: {(result.TempsDeleted ? "sí" : "NO")}");
        builder.AppendLine($"Sin Access propios pendientes: {(result.NoNewAccessProcesses ? "sí" : "NO")}");
        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Avisos:");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine("- " + warning);
            }
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Errores:");
            foreach (var error in result.Errors)
            {
                builder.AppendLine("- " + error);
            }
        }

        return builder.ToString();
    }

    private static string FormatFlag(bool? value)
    {
        return value is null ? "(null)" : value.Value ? "true" : "false";
    }
}

internal static class ProbeResults
{
    public static string JsonPath(string fileName)
    {
        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var directory = start; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MdbToSql.slnx")))
            {
                return Path.Combine(directory.FullName, "results", fileName);
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "results", fileName);
    }
}
