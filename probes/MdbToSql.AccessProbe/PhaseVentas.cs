using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MdbToSql.AccessApplication;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessProbe;

internal static class PhaseVentas
{
    private static readonly Regex CredentialLine = new(
        @"\b(clave|password|pwd|passwd|contrase[nñ]a)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Quoted = new("\"[^\"]*\"", RegexOptions.Compiled);

    public static int Run(string mdbPath)
    {
        Console.WriteLine("PROBE — análisis del formulario ventas");
        Console.WriteLine(mdbPath);
        Console.WriteLine();

        try
        {
            var jsonPath = ProbeResults.JsonPath("ventas-analysis.json");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            var result = new AccessVentasAnalysisProbe()
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
                && result.Form is not null
                && result.OpenedInDesignView
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

    private static string Format(AccessVentasAnalysisResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("API: " + result.ApiUsed);
        builder.AppendLine("Formulario: " + result.FormName);
        builder.AppendLine("Abrió Design View: " + YesNo(result.OpenedInDesignView));
        builder.AppendLine("Cerró acSaveNo: " + YesNo(result.ClosedCleanly));
        builder.AppendLine("Módulo: " + (result.ModuleName ?? "(null)"));
        builder.AppendLine("Líneas VBA: " + (result.LineCount?.ToString() ?? "(null)"));
        builder.AppendLine();

        if (result.Form is not null)
        {
            builder.AppendLine(AccessApplicationAnalysisPrinter.FormatFormDetail(result.Form));
            builder.AppendLine("PROPIEDADES DE DATOS");
            builder.AppendLine("- AllowEdits: " + Flag(result.Form.AllowEdits));
            builder.AppendLine("- AllowAdditions: " + Flag(result.Form.AllowAdditions));
            builder.AppendLine("- AllowDeletions: " + Flag(result.Form.AllowDeletions));
            builder.AppendLine("- DataEntry: " + Flag(result.Form.DataEntry));
            builder.AppendLine("- Filter: " + (result.Form.Filter ?? "(vacío)"));
            builder.AppendLine();
            builder.AppendLine("CONTROLES DE ENTRADA");
            var inputs = result.Form.Controls
                .Where(control =>
                    control.ControlType is AccessControlTypeKind.TextBox
                        or AccessControlTypeKind.ComboBox
                        or AccessControlTypeKind.ListBox
                        or AccessControlTypeKind.CheckBox
                        or AccessControlTypeKind.OptionButton)
                .ToList();
            if (inputs.Count == 0)
            {
                builder.AppendLine("(ninguno clasificado como entrada)");
            }
            else
            {
                foreach (var control in inputs)
                {
                    builder.AppendLine(
                        "- " + control.Name + " " + control.ControlTypeName +
                        " ControlSource=" + (control.ControlSource ?? "(vacío)") +
                        " Locked=" + Flag(control.Locked) +
                        " Enabled=" + Flag(control.Enabled) +
                        (string.IsNullOrWhiteSpace(control.ValidationRule)
                            ? string.Empty
                            : " ValidationRule=" + control.ValidationRule));
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("PROCEDIMIENTOS DEL MÓDULO");
        if (result.Procedures.Count == 0)
        {
            builder.AppendLine("(ninguno)");
        }
        else
        {
            foreach (var procedure in result.Procedures)
            {
                builder.AppendLine(
                    "- " + procedure.Kind + " " + procedure.Name +
                    " (líneas " + procedure.StartLine + "-" + procedure.EndLine + ")");
            }
        }

        builder.AppendLine();
        builder.AppendLine("EVENTOS → PROCEDIMIENTOS");
        foreach (var binding in result.Bindings)
        {
            var status = binding.Resolved
                ? binding.ResolvedProcedure
                : "NO RESUELTO [" + string.Join(", ", binding.CandidateProcedures) + "]";
            builder.AppendLine(
                binding.ObjectName + "." + binding.EventName + " = " +
                (binding.BindingExpression ?? "(null)") + " → " + status);
        }

        builder.AppendLine();
        builder.AppendLine("QUERYDEFS DIRECTAS (definición DAO, no ejecutadas)");
        if (result.QueryDefs.Count == 0)
        {
            builder.AppendLine("(ninguna QueryDef nombrada de forma directa)");
        }
        else
        {
            foreach (var query in result.QueryDefs)
            {
                builder.AppendLine(
                    "- " + query.Name + " Type=" + query.TypeName +
                    " ReturnsRecords=" + Flag(query.ReturnsRecords) +
                    " Connect=" + (query.ConnectPresent ?? "(ninguno)"));
                builder.AppendLine("    Usada desde: " + string.Join("; ", query.UsedFrom));
                if (!string.IsNullOrWhiteSpace(query.Sql))
                {
                    builder.AppendLine("    SQL: " + Trim(Redact(query.Sql), 220));
                }

                if (query.TablesInSql.Count > 0)
                {
                    builder.AppendLine("    Tablas en SQL: " + string.Join(", ", query.TablesInSql));
                }

                if (query.NestedQueriesPending.Count > 0)
                {
                    builder.AppendLine("    QueryDefs anidadas pendientes: " + string.Join(", ", query.NestedQueriesPending));
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("TABLAS REFERENCIADAS");
        if (result.Tables.Count == 0)
        {
            builder.AppendLine("(ninguna resuelta)");
        }
        else
        {
            foreach (var table in result.Tables)
            {
                builder.AppendLine(
                    "- " + table.Name +
                    (table.IsLinked ? " vinculada" : " local") +
                    " LinkKind=" + table.LinkKind +
                    (table.IsUnc ? " UNC" : string.Empty) +
                    " SourceTable=" + (table.SourceTableName ?? "-"));
                builder.AppendLine("    Usada desde: " + string.Join("; ", table.UsedFrom));
            }
        }

        builder.AppendLine();
        builder.AppendLine("RECORRIDO FUNCIONAL DEMOSTRADO");
        foreach (var line in Explain(result))
        {
            builder.AppendLine("- " + line);
        }

        builder.AppendLine();
        builder.AppendLine("INFERENCIAS");
        builder.AppendLine("- No se deduce el negocio solo por el nombre 'ventas'.");
        builder.AppendLine("- No se ejecutó VBA, consultas ni botones; el recorrido es el del texto y las propiedades.");
        if (result.QueryDefs.Any(item => AccessQueryTypeNames.MayModifyData(item.Type)))
        {
            builder.AppendLine("- Hay QueryDefs de acción referenciadas: pretenden modificar datos si se ejecutaran.");
        }

        builder.AppendLine();
        builder.AppendLine("PENDIENTE (no seguido)");
        if (result.Pending.Count == 0)
        {
            builder.AppendLine("(nada marcado)");
        }
        else
        {
            foreach (var item in result.Pending)
            {
                builder.AppendLine("- " + Redact(item));
            }
        }

        builder.AppendLine();
        builder.AppendLine("REFERENCIAS VBA");
        foreach (var reference in result.References.Take(80))
        {
            builder.AppendLine(
                "- L" + reference.Line + " " + reference.Kind +
                (reference.Dynamic ? " (dinámico)" : string.Empty) +
                (string.IsNullOrEmpty(reference.Target) ? string.Empty : " " + reference.Target));
            builder.AppendLine("    " + Trim(Redact(reference.Evidence), 180));
        }

        if (result.References.Count > 80)
        {
            builder.AppendLine("... " + (result.References.Count - 80) + " referencias más en el JSON.");
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

    private static IReadOnlyList<string> Explain(AccessVentasAnalysisResult result)
    {
        var lines = new List<string>();
        var form = result.Form;
        if (form is null)
        {
            lines.Add("No se pudo leer el formulario; no hay recorrido demostrable.");
            return lines;
        }

        if (string.IsNullOrWhiteSpace(form.RecordSource))
        {
            lines.Add("RecordSource vacío: el formulario no está ligado a una tabla/consulta demostrada.");
        }
        else
        {
            lines.Add(
                "Origen de registros: " + form.RecordSourceKind + " '" + form.RecordSource + "'" +
                " (AllowEdits=" + Flag(form.AllowEdits) +
                ", AllowAdditions=" + Flag(form.AllowAdditions) +
                ", AllowDeletions=" + Flag(form.AllowDeletions) + ").");
        }

        var bound = form.Controls
            .Where(control =>
                !string.IsNullOrWhiteSpace(control.ControlSource) && !control.IsExpression)
            .Select(control => control.Name + "←" + control.ControlSource)
            .ToList();
        if (bound.Count > 0)
        {
            lines.Add("Campos ligados demostrados: " + string.Join(", ", bound.Take(20)) +
                (bound.Count > 20 ? " …" : string.Empty) + ".");
        }

        var rules = form.Controls
            .Where(control => !string.IsNullOrWhiteSpace(control.ValidationRule))
            .Select(control => control.Name + " ValidationRule=" + control.ValidationRule)
            .ToList();
        if (rules.Count > 0)
        {
            lines.Add("Validaciones de control: " + string.Join("; ", rules) + ".");
        }

        foreach (var binding in result.Bindings.Where(item => item.Resolved))
        {
            var procedure = result.Procedures.FirstOrDefault(item =>
                string.Equals(item.Name, binding.ResolvedProcedure, StringComparison.OrdinalIgnoreCase));
            var ops = result.References
                .Where(item =>
                    procedure is not null
                    && item.Line >= procedure.StartLine
                    && item.Line <= procedure.EndLine
                    && item.Kind is "DoCmd.OpenQuery" or "DoCmd.RunSQL" or "CurrentDb.Execute"
                        or "DoCmd.OpenForm" or "DoCmd.OpenReport" or "DoCmd.OpenTable"
                        or "UserPrompt" or "UserMessage" or "Call" or "SameModule"
                        or "QueryDefs" or "OpenRecordset" or "Shell" or "FileIo")
                .Select(item => item.Kind + " L" + item.Line +
                    (string.IsNullOrEmpty(item.Target) ? string.Empty : " " + item.Target))
                .Distinct()
                .ToList();
            lines.Add(
                "Evento " + binding.ObjectName + "." + binding.EventName +
                " → " + binding.ResolvedProcedure +
                (procedure is null
                    ? "."
                    : " (líneas " + procedure.StartLine + "-" + procedure.EndLine + ")" +
                      (ops.Count == 0 ? "." : ": " + string.Join("; ", ops) + ".")));
        }

        var unresolved = result.Bindings.Where(item => !item.Resolved).ToList();
        if (unresolved.Count > 0)
        {
            lines.Add(
                "Eventos sin procedimiento en este módulo: " +
                string.Join(", ", unresolved.Select(item => item.ObjectName + "." + item.EventName)) + ".");
        }

        foreach (var query in result.QueryDefs)
        {
            lines.Add(
                "QueryDef '" + query.Name + "' (" + query.TypeName + ") referenciada desde " +
                string.Join(", ", query.UsedFrom) +
                (AccessQueryTypeNames.MayModifyData(query.Type)
                    ? "; tipo de acción (pretende modificar datos si se ejecutara)."
                    : "; no se ejecutó."));
        }

        if (result.References.Any(item => item.Kind == "UserPrompt"))
        {
            lines.Add(
                "InputBox en " +
                string.Join(", ", result.References.Where(item => item.Kind == "UserPrompt").Select(item => "L" + item.Line)) +
                ".");
        }

        if (result.References.Any(item => item.Kind == "UserMessage"))
        {
            lines.Add(
                "MsgBox en " +
                string.Join(", ", result.References.Where(item => item.Kind == "UserMessage").Select(item => "L" + item.Line)) +
                ".");
        }

        return lines;
    }

    private static string Flag(bool? value)
    {
        return value is null ? "(n/a)" : value.Value ? "true" : "false";
    }

    private static string YesNo(bool value)
    {
        return value ? "sí" : "NO";
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

    private static string Trim(string value, int max)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ');
        return compact.Length <= max ? compact : compact[..max] + "...";
    }
}
