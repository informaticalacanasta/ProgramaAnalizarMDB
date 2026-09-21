using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MdbToSql.AccessApplication;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessProbe;

internal static class PhaseTerminalVba
{
    private static readonly Regex CredentialLine = new(
        @"\b(clave|password|pwd|passwd|contrase[nñ]a)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Quoted = new("\"[^\"]*\"", RegexOptions.Compiled);

    public static int Run(string mdbPath)
    {
        Console.WriteLine("PROBE — localizar y analizar procedimiento terminal");
        Console.WriteLine(mdbPath);
        Console.WriteLine();

        try
        {
            var jsonPath = ProbeResults.JsonPath("terminal-vba.json");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            var result = new AccessTerminalVbaProbe()
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
                && result.Definitions.Count == 1
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

    private static string Format(AccessTerminalVbaProbeResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("API: " + result.ApiUsed);
        builder.AppendLine(
            "Módulos buscados (" + result.ModulesSearched.Count + "): " +
            (result.ModulesSearched.Count == 0 ? "(ninguno)" : string.Join(", ", result.ModulesSearched)));
        builder.AppendLine("Declaraciones encontradas: " + result.Definitions.Count);
        builder.AppendLine("Cerró acSaveNo: " + (result.ClosedCleanly ? "sí" : "no"));
        builder.AppendLine();

        if (result.Definitions.Count == 0)
        {
            builder.AppendLine("UBICACIÓN");
            builder.AppendLine(
                "No hay declaración de procedimiento 'terminal' en los módulos estándar inventariados.");
            builder.AppendLine("No se abrieron Forms ni Reports para ampliar la búsqueda.");
        }
        else
        {
            foreach (var definition in result.Definitions)
            {
                builder.AppendLine("UBICACIÓN");
                builder.AppendLine(
                    "Módulo " + definition.ModuleName + ", " + definition.Kind + " " + definition.Name +
                    " (líneas " + definition.StartLine + "-" + definition.EndLine + ")");
                builder.AppendLine("Firma: " + (definition.Signature ?? "(no leída)"));
                builder.AppendLine();
                builder.AppendLine("COMPORTAMIENTO DEMOSTRADO POR EL CÓDIGO");
                foreach (var line in ExplainDemonstrated(definition, result.References))
                {
                    builder.AppendLine("- " + line);
                }

                builder.AppendLine();
                builder.AppendLine("INFERENCIAS");
                foreach (var line in ExplainInferences(definition, result.References))
                {
                    builder.AppendLine("- " + line);
                }

                builder.AppendLine();
                builder.AppendLine("DEPENDENCIAS PENDIENTES (no seguidas)");
                var pending = PendingCalls(result.References);
                var pendingPaths = result.References
                    .Where(item => item.Kind is "ExternalPath" or "Shell")
                    .Select(item => "L" + item.Line + " " + item.Kind + " " + (item.Target ?? "") + " (no se abrió ni ejecutó)")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (pending.Count == 0 && pendingPaths.Count == 0)
                {
                    builder.AppendLine("- ninguna llamada a otro procedimiento inventariada");
                }
                else
                {
                    foreach (var call in pending)
                    {
                        builder.AppendLine("- " + call);
                    }

                    foreach (var path in pendingPaths)
                    {
                        builder.AppendLine("- " + path);
                    }
                }

                builder.AppendLine();
                builder.AppendLine("RELACIÓN CON MENU Y OPERADOR");
                builder.AppendLine(
                    "- MENU llama a terminal (Form_Open y Comando8); terminal no está en Form_MENU.");
                if (result.References.Any(item =>
                        item.Target is not null
                        && item.Target.Equals("OPERADOR", StringComparison.OrdinalIgnoreCase)))
                {
                    builder.AppendLine(
                        "- El código de terminal lee y/o escribe OPERADOR; no se afirma autenticación salvo las asignaciones y lecturas mostradas.");
                }
                else
                {
                    builder.AppendLine(
                        "- Este procedimiento no menciona OPERADOR. MENU usa OPERADOR después de Call terminal; esa relación queda como inferencia pendiente.");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("REFERENCIAS DETECTADAS");
        if (result.References.Count == 0)
        {
            builder.AppendLine("(ninguna)");
        }
        else
        {
            foreach (var reference in result.References)
            {
                builder.AppendLine(
                    "- L" + reference.Line + " " + reference.Kind +
                    (reference.Dynamic ? " (dinámico)" : string.Empty) +
                    (string.IsNullOrEmpty(reference.Target) ? string.Empty : " " + reference.Target));
                builder.AppendLine("    " + Redact(reference.Evidence));
            }
        }

        builder.AppendLine();
        builder.AppendLine("JSON: " + result.JsonPath);
        builder.AppendLine(
            "Original intacto: " + YesNo(result.OriginalIntegrityVerified) +
            " hash=" + result.OriginalAfter.Sha256 +
            " size=" + result.OriginalAfter.Size +
            " LastWriteTimeUtc=" + result.OriginalAfter.LastWriteUtc.ToString("O"));
        builder.AppendLine("Temporales eliminados: " + YesNo(result.TempsDeleted));
        builder.AppendLine("Sin Access propios pendientes: " + YesNo(result.NoNewAccessProcesses));
        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Avisos:");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine("- " + Redact(warning));
            }
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Errores:");
            foreach (var error in result.Errors)
            {
                builder.AppendLine("- " + Redact(error));
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> ExplainDemonstrated(
        AccessProcedureDefinition definition,
        IReadOnlyList<AccessVbaReference> references)
    {
        var lines = new List<string>();
        var signature = definition.Signature ?? string.Empty;
        var hasParams = Regex.IsMatch(signature, @"\([^)]*\S[^)]*\)");
        if (definition.Kind.Equals("Function", StringComparison.OrdinalIgnoreCase)
            || definition.Kind.StartsWith("Property", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(
                "Es " + definition.Kind + (hasParams ? " con parámetros en la firma." : " sin parámetros visibles en la firma.") +
                " Puede devolver un valor (no se ejecutó).");
        }
        else
        {
            lines.Add(
                "Es " + definition.Kind +
                (hasParams ? " con parámetros en la firma." : " sin parámetros.") +
                " No declara valor de retorno.");
        }

        AppendKind(lines, references, "GlobalWrite", "Asigna OPERADOR");
        AppendKind(lines, references, "GlobalRead", "Lee OPERADOR");
        AppendKind(lines, references, "UserPrompt", "Pide dato al usuario (InputBox)");
        AppendKind(lines, references, "UserMessage", "Muestra mensaje (MsgBox)");
        AppendKind(lines, references, "Shell", "Lanza un proceso (Shell)");
        AppendKind(lines, references, "ErrorHandler", "Maneja error (On Error)");
        AppendKind(lines, references, "Dao", "Usa DAO (CurrentDb/OpenDatabase/OpenRecordset)");
        AppendKind(lines, references, "DomainFunction", "Usa función de dominio (DLookup/DCount/...)");
        AppendKind(lines, references, "FileIo", "Abre, escribe o cierra archivo");
        AppendKind(lines, references, "ExternalPath", "Menciona ruta");
        AppendKind(lines, references, "DoCmd.OpenForm", "DoCmd.OpenForm");
        AppendKind(lines, references, "DoCmd.OpenQuery", "DoCmd.OpenQuery");
        AppendKind(lines, references, "DoCmd.RunSQL", "DoCmd.RunSQL");
        AppendKind(lines, references, "CurrentDb.Execute", "CurrentDb.Execute");

        if (HasControlFlow(definition.ProcedureSource, @"\bIf\b"))
        {
            lines.Add("Tiene condiciones If (ver cuerpo en JSON).");
        }

        if (HasControlFlow(
            definition.ProcedureSource,
            @"\bWhile\b|\bWend\b|\bFor\s+Each\b|\bDo\s+(While|Until)\b|\bLoop\b|\bFor\s+[A-Za-z_][A-Za-z0-9_]*\s*="))
        {
            lines.Add("Tiene bucles (ver cuerpo en JSON).");
        }

        if (!references.Any(item => item.Kind == "ErrorHandler"))
        {
            lines.Add("No hay On Error en este procedimiento.");
        }

        if (definition.Kind.Equals("Function", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(
                definition.ProcedureSource,
                @"\b" + Regex.Escape(definition.Name) + @"\s*=",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            lines.Add("La Function no asigna su valor de retorno.");
        }

        return lines;
    }

    private static IReadOnlyList<string> ExplainInferences(
        AccessProcedureDefinition definition,
        IReadOnlyList<AccessVbaReference> references)
    {
        var lines = new List<string>();
        var writesOperador = references.Any(item => item.Kind == "GlobalWrite");
        var prompts = references.Any(item => item.Kind == "UserPrompt");
        var shells = references.Any(item => item.Kind == "Shell");
        var files = references.Any(item => item.Kind == "FileIo");
        if (writesOperador && prompts)
        {
            lines.Add(
                "Puede estar rellenando OPERADOR con lo que teclee el usuario; eso no demuestra por sí solo autenticación ni validación de identidad.");
        }
        else if (writesOperador && shells && files)
        {
            lines.Add(
                "OPERADOR se rellena desde un archivo tras un Shell; el nombre COMPUTERNAME sugiere el nombre de equipo, no un login. No se abrió el .exe ni el archivo.");
        }
        else if (writesOperador)
        {
            lines.Add("Modifica OPERADOR; el origen del valor hay que leerlo en el cuerpo, no se asume login.");
        }
        else
        {
            lines.Add("No se afirma que terminal autentique al usuario: el código de este procedimiento no se ejecutó y no se infiere login sin asignación demostrada.");
        }

        if (definition.ModuleDeclarations is not null
            && definition.ModuleDeclarations.Contains("OPERADOR", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("OPERADOR aparece en declaraciones del mismo módulo; su alcance global/módulo queda confirmable en ese encabezado.");
        }

        lines.Add("No se analizó ventas ni el resto de formularios.");
        return lines;
    }

    private static void AppendKind(
        List<string> lines,
        IReadOnlyList<AccessVbaReference> references,
        string kind,
        string label)
    {
        var matches = references.Where(item => item.Kind == kind).ToList();
        if (matches.Count == 0)
        {
            return;
        }

        lines.Add(
            label + " en " +
            string.Join(", ", matches.Select(item => "L" + item.Line)));
    }

    private static IReadOnlyList<string> PendingCalls(IReadOnlyList<AccessVbaReference> references)
    {
        return references
            .Where(item =>
                (item.Kind is "Call" or "SameModule")
                && !string.Equals(item.Target, "terminal", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.Target))
            .Select(item => "L" + item.Line + " " + item.Kind + " " + item.Target)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasControlFlow(string source, string pattern)
    {
        return Regex.IsMatch(source, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Redact(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (CredentialLine.IsMatch(lines[index]))
            {
                lines[index] = Quoted.Replace(lines[index], "\"[omitida]\"");
            }
        }

        return string.Join("\n", lines);
    }

    private static string YesNo(bool value)
    {
        return value ? "sí" : "NO";
    }
}
