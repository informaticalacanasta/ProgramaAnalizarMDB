using System.Text;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessApplicationAnalysisPrinter
{
    public static string Format(AccessApplicationAnalysis analysis)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ANÁLISIS BASE");
        builder.AppendLine();
        builder.AppendLine("Archivo:");
        builder.AppendLine(analysis.MdbName);
        builder.AppendLine();
        builder.AppendLine("Jet:");
        builder.AppendLine(analysis.JetVersion ?? "(desconocido)");
        builder.AppendLine();
        builder.AppendLine("Startup:");
        builder.AppendLine($"AutoExec: {(analysis.Startup.AutoExecExists ? "sí" : "no")}");
        builder.AppendLine($"StartupForm: {analysis.Startup.StartupForm ?? "(ausente)"}");
        builder.AppendLine(
            "AllowBypassKey original: " + FormatNullable(analysis.Startup.AllowBypassKey, absent: "ausente"));
        builder.AppendLine();
        builder.AppendLine("Tablas:");
        builder.AppendLine($"Locales: {analysis.Tables.Count(table => !table.IsLinked)}");
        builder.AppendLine($"Vinculadas: {analysis.LinkedTables.Count}");
        builder.AppendLine();
        builder.AppendLine("Queries:");
        builder.AppendLine(analysis.Queries.Count.ToString());
        builder.AppendLine();
        builder.AppendLine("Relations:");
        builder.AppendLine(analysis.Relations.Count.ToString());
        builder.AppendLine();
        builder.AppendLine("Forms:");
        builder.AppendLine(analysis.Forms.Count.ToString());
        builder.AppendLine();
        builder.AppendLine("Reports:");
        builder.AppendLine(analysis.Reports.Count.ToString());
        builder.AppendLine();
        builder.AppendLine("Modules:");
        builder.AppendLine(analysis.Modules.Count.ToString());
        builder.AppendLine();
        builder.AppendLine("Macros:");
        builder.AppendLine(analysis.Macros.Count.ToString());
        builder.AppendLine();
        builder.AppendLine("Forms cargados durante análisis:");
        builder.AppendLine(analysis.LoadedForms.ToString());
        builder.AppendLine();
        builder.AppendLine("Reports cargados:");
        builder.AppendLine(analysis.LoadedReports.ToString());
        builder.AppendLine();
        builder.AppendLine("Original intacto:");
        builder.AppendLine(analysis.OriginalIntegrityVerified ? "sí" : "NO");
        AppendFormSummary(builder, analysis);
        if (analysis.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Avisos:");
            foreach (var warning in analysis.Warnings)
            {
                builder.AppendLine("- " + warning);
            }
        }

        if (analysis.Errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Errores:");
            foreach (var error in analysis.Errors)
            {
                builder.AppendLine("- " + error);
            }
        }

        return builder.ToString();
    }

    public static string FormatFormDetail(AccessFormAnalysis form)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"FORM: {form.Name}");
        builder.AppendLine("==================================");
        builder.AppendLine();
        builder.AppendLine("Caption:");
        builder.AppendLine(string.IsNullOrWhiteSpace(form.Caption) ? "(empty)" : form.Caption);
        builder.AppendLine();
        builder.AppendLine("RecordSource:");
        builder.AppendLine(string.IsNullOrWhiteSpace(form.RecordSource) ? "(empty)" : form.RecordSource);
        builder.AppendLine($"RecordSourceKind: {form.RecordSourceKind}");
        builder.AppendLine();
        builder.AppendLine("HasModule:");
        builder.AppendLine(form.HasModule is null ? "(desconocido)" : form.HasModule.Value ? "true" : "false");
        builder.AppendLine();
        builder.AppendLine("OpenedInDesignView:");
        builder.AppendLine(form.OpenedInDesignView ? "true" : "false");
        builder.AppendLine("ClosedCleanly:");
        builder.AppendLine(form.ClosedCleanly ? "true" : "false");
        builder.AppendLine();
        builder.AppendLine("Controls:");
        builder.AppendLine(form.Controls.Count.ToString());
        builder.AppendLine();
        builder.AppendLine("CommandButtons:");
        builder.AppendLine(form.Controls.Count(control => control.ControlType == AccessControlTypeKind.CommandButton).ToString());
        builder.AppendLine();
        builder.AppendLine("SubForms:");
        builder.AppendLine(form.Controls.Count(control => control.ControlType == AccessControlTypeKind.SubForm).ToString());
        builder.AppendLine();
        builder.AppendLine("Events:");
        if (form.Events.Count == 0)
        {
            builder.AppendLine("(ninguno)");
        }
        else
        {
            foreach (var binding in form.Events)
            {
                builder.AppendLine($"{binding.EventName} = {binding.Expression}");
            }
        }

        var buttons = form.Controls
            .Where(control => control.ControlType == AccessControlTypeKind.CommandButton)
            .ToList();
        builder.AppendLine();
        builder.AppendLine("BUTTONS");
        builder.AppendLine("----------------------------------");
        builder.AppendLine();
        if (buttons.Count == 0)
        {
            builder.AppendLine("(ninguno)");
        }
        else
        {
            foreach (var button in buttons)
            {
                builder.AppendLine(button.Name);
                builder.AppendLine($"  Caption: {button.Caption ?? "(empty)"}");
                var onClick = button.Events.FirstOrDefault(item =>
                    string.Equals(item.EventName, "OnClick", StringComparison.OrdinalIgnoreCase));
                builder.AppendLine($"  OnClick: {onClick?.Expression ?? "(none)"}");
                builder.AppendLine($"  Enabled: {FormatFlag(button.Enabled)}");
                builder.AppendLine($"  Visible: {FormatFlag(button.Visible)}");
                if (!string.IsNullOrWhiteSpace(button.Tag))
                {
                    builder.AppendLine($"  Tag: {button.Tag}");
                }

                builder.AppendLine();
            }
        }

        var subForms = form.Controls
            .Where(control => control.ControlType == AccessControlTypeKind.SubForm)
            .ToList();
        builder.AppendLine("SUBFORMS");
        builder.AppendLine("----------------------------------");
        builder.AppendLine();
        if (subForms.Count == 0)
        {
            builder.AppendLine("(ninguno)");
        }
        else
        {
            foreach (var subForm in subForms)
            {
                builder.AppendLine(subForm.Name);
                builder.AppendLine($"  SourceObject: {subForm.SourceObject ?? "(empty)"}");
                builder.AppendLine($"  LinkMasterFields: {subForm.LinkMasterFields ?? "(empty)"}");
                builder.AppendLine($"  LinkChildFields: {subForm.LinkChildFields ?? "(empty)"}");
                builder.AppendLine();
            }
        }

        builder.AppendLine("DEPENDENCIES");
        builder.AppendLine("----------------------------------");
        builder.AppendLine();
        if (form.Dependencies.Count == 0)
        {
            builder.AppendLine("(ninguna)");
        }
        else
        {
            foreach (var dependency in form.Dependencies)
            {
                builder.AppendLine($"{dependency.SourceObject} → {dependency.RelationKind} → {dependency.TargetObject}");
                builder.AppendLine($"  Evidence: {dependency.Evidence}");
            }
        }

        if (form.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Avisos:");
            foreach (var warning in form.Warnings)
            {
                builder.AppendLine("- " + warning);
            }
        }

        if (form.Error is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Error:");
            builder.AppendLine(form.Error);
        }

        return builder.ToString();
    }

    private static void AppendFormSummary(StringBuilder builder, AccessApplicationAnalysis analysis)
    {
        if (analysis.Forms.Count == 0
            || analysis.Forms.All(form => !form.OpenedInDesignView && form.Error is null && form.Controls.Count == 0))
        {
            return;
        }

        var stats = AccessFormStatistics.From(analysis.Forms);
        builder.AppendLine();
        builder.AppendLine("FORMULARIOS");
        builder.AppendLine("================================");
        builder.AppendLine($"Analizados: {stats.Total}");
        builder.AppendLine($"OK: {stats.Ok}");
        builder.AppendLine($"Con avisos: {stats.WithWarnings}");
        builder.AppendLine($"Fallidos: {stats.Failed}");
        builder.AppendLine($"Controles: {stats.Controls}");
        builder.AppendLine($"CommandButtons: {stats.CommandButtons}");
        builder.AppendLine($"SubForms: {stats.SubForms}");
        builder.AppendLine($"ComboBox: {stats.ComboBoxes}");
        builder.AppendLine($"ListBox: {stats.ListBoxes}");
        builder.AppendLine($"Event Procedure: {stats.EventProcedures}");
        builder.AppendLine($"Macros en eventos: {stats.EventMacros}");
        builder.AppendLine($"Expresiones en eventos: {stats.EventExpressions}");
        builder.AppendLine($"Dependencias RecordSource: {stats.RecordSourceDependencies}");
        builder.AppendLine($"Dependencias RowSource: {stats.RowSourceDependencies}");
        builder.AppendLine($"Dependencias SubForm: {stats.SubFormDependencies}");
        if (stats.ControlsByType.Count > 0)
        {
            builder.AppendLine(
                "Por tipo: " + string.Join(
                    ", ",
                    stats.ControlsByType.OrderByDescending(item => item.Value)
                        .Select(item => $"{item.Key}={item.Value}")));
        }

        builder.AppendLine();
        foreach (var form in analysis.Forms.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(form.Name);
            builder.AppendLine($"  Controls: {form.Controls.Count}");
            builder.AppendLine(
                $"  Buttons: {form.Controls.Count(control => control.ControlType == AccessControlTypeKind.CommandButton)}");
            builder.AppendLine($"  Events: {form.Events.Count}");
            builder.AppendLine(
                $"  RecordSource: {(string.IsNullOrWhiteSpace(form.RecordSource) ? "-" : form.RecordSource)}");
            var subForms = form.Controls.Count(control => control.ControlType == AccessControlTypeKind.SubForm);
            if (subForms > 0)
            {
                builder.AppendLine($"  SubForms: {subForms}");
            }
            if (form.Error is not null)
            {
                builder.AppendLine($"  Error: {form.Error}");
            }
        }

        var menu = analysis.Forms.FirstOrDefault(form =>
            string.Equals(form.Name, "MENU", StringComparison.OrdinalIgnoreCase));
        if (menu is not null)
        {
            builder.AppendLine();
            builder.Append(FormatFormDetail(menu));
        }

        var complex = analysis.Forms
            .OrderByDescending(form => form.Controls.Count)
            .FirstOrDefault();
        if (complex is not null
            && !string.Equals(complex.Name, "MENU", StringComparison.OrdinalIgnoreCase)
            && complex.Controls.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("FORMULARIO MÁS COMPLEJO");
            builder.AppendLine($"{complex.Name} ({complex.Controls.Count} controles)");
        }
    }

    private static string FormatFlag(bool? value)
    {
        return value is null ? "(n/a)" : value.Value ? "true" : "false";
    }

    private static string FormatNullable(bool? value, string absent)
    {
        return value is null ? absent : value.Value ? "true" : "false";
    }
}
