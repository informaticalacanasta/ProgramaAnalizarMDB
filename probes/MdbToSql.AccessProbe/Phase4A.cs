using System.Text;
using MdbToSql.AccessApplication;
using MdbToSql.Core.Analysis;
using MdbToSql.Core.Models;

namespace MdbToSql.AccessProbe;

internal static class Phase4A
{
    public static int Run(string mdbPath)
    {
        Console.WriteLine("FASE 4A — un solo informe Access en Design View");
        Console.WriteLine(mdbPath);
        Console.WriteLine();

        try
        {
            var result = new AccessSingleReportProbe().RunAsync(mdbPath).GetAwaiter().GetResult();
            Console.WriteLine(Format(result));
            return result.OriginalIntegrityVerified && result.Errors.Count == 0 && result.Report?.Error is null
                ? 0
                : 1;
        }
        catch (Exception exception)
        {
            Console.WriteLine("ERROR: " + exception);
            return 2;
        }
    }

    private static string Format(AccessSingleReportProbeResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("INFORMES ENUMERADOS (DAO, sin abrir):");
        foreach (var name in result.ReportNames)
        {
            builder.AppendLine("- " + name);
        }

        builder.AppendLine();
        builder.AppendLine("Regla de selección:");
        builder.AppendLine(result.SelectionRule);
        builder.AppendLine();
        builder.AppendLine("Informe elegido:");
        builder.AppendLine(string.IsNullOrWhiteSpace(result.ChosenReport) ? "(ninguno)" : result.ChosenReport);
        builder.AppendLine();

        if (result.Report is null)
        {
            builder.AppendLine("No se abrió ningún informe.");
        }
        else
        {
            var report = result.Report;
            builder.AppendLine($"Abrió Design View oculto: {(report.OpenedInDesignView ? "sí" : "no")}");
            builder.AppendLine($"Cerró acSaveNo: {(report.ClosedCleanly ? "sí" : "no")}");
            builder.AppendLine($"Caption: {report.Caption ?? "(null)"}");
            builder.AppendLine($"RecordSource: {report.RecordSource ?? "(null)"}");
            builder.AppendLine($"Filter: {report.Filter ?? "(null)"}");
            builder.AppendLine($"FilterOn: {FormatFlag(report.FilterOn)}");
            builder.AppendLine($"OrderBy: {report.OrderBy ?? "(null)"}");
            builder.AppendLine($"OrderByOn: {FormatFlag(report.OrderByOn)}");
            builder.AppendLine($"HasModule: {FormatFlag(report.HasModule)}");
            builder.AppendLine($"Width: {report.Width?.ToString() ?? "(null)"}");
            builder.AppendLine($"Controles: {report.Controls.Count}");
            var byType = report.Controls
                .GroupBy(control => control.ControlTypeName)
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Key}={group.Count()}");
            builder.AppendLine("Por tipo: " + (report.Controls.Count == 0 ? "(ninguno)" : string.Join(", ", byType)));
            builder.AppendLine();
            builder.AppendLine("SECCIONES");
            if (report.Sections.Count == 0)
            {
                builder.AppendLine("(ninguna leída)");
            }
            else
            {
                foreach (var section in report.Sections)
                {
                    builder.AppendLine(
                        $"- {section.Name} ({section.Kind}" +
                        $"{(section.GroupLevel is null ? string.Empty : " nivel " + section.GroupLevel)}) " +
                        $"Height={section.Height?.ToString() ?? "null"} " +
                        $"Visible={FormatFlag(section.Visible)} " +
                        $"Controls={section.ControlCount}");
                    foreach (var binding in section.Events)
                    {
                        builder.AppendLine($"    {binding.EventName} = {binding.Expression}");
                    }
                }
            }

            builder.AppendLine();
            builder.AppendLine("CONTROLES REPRESENTATIVOS");
            foreach (var control in report.Controls.Take(12))
            {
                builder.AppendLine(control.Name);
                builder.AppendLine($"  Type: {control.ControlTypeName} ({control.RawControlType})");
                builder.AppendLine($"  Caption: {control.Caption ?? "(null)"}");
                builder.AppendLine($"  ControlSource: {control.ControlSource ?? "(null)"}");
                builder.AppendLine($"  Format: {control.Format ?? "(null)"}");
                builder.AppendLine(
                    $"  Left={control.Left} Top={control.Top} Width={control.Width} Height={control.Height}");
                builder.AppendLine($"  Section: {control.Section ?? "(null)"}");
            }

            var subReports = report.Controls
                .Where(control => control.ControlType == AccessControlTypeKind.SubForm)
                .ToList();
            builder.AppendLine();
            builder.AppendLine("SUBINFORMES (ControlType 112, sin abrir el objeto interno)");
            if (subReports.Count == 0)
            {
                builder.AppendLine("(ninguno)");
            }
            else
            {
                foreach (var sub in subReports)
                {
                    builder.AppendLine(sub.Name);
                    builder.AppendLine($"  SourceObject: {sub.SourceObject ?? "(null)"}");
                    builder.AppendLine($"  LinkMasterFields: {sub.LinkMasterFields ?? "(null)"}");
                    builder.AppendLine($"  LinkChildFields: {sub.LinkChildFields ?? "(null)"}");
                }
            }

            builder.AppendLine();
            builder.AppendLine("EVENTOS DEL INFORME");
            if (report.Events.Count == 0)
            {
                builder.AppendLine("(ninguno)");
            }
            else
            {
                foreach (var binding in report.Events)
                {
                    builder.AppendLine($"{binding.EventName} = {binding.Expression}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Original intacto: {(result.OriginalIntegrityVerified ? "sí" : "NO")}");
        builder.AppendLine($"Hash antes: {result.OriginalBefore.Sha256}");
        builder.AppendLine($"Hash después: {result.OriginalAfter.Sha256}");
        builder.AppendLine($"Tamaño antes/después: {result.OriginalBefore.Size} / {result.OriginalAfter.Size}");
        builder.AppendLine(
            $"LastWriteUtc antes/después: {result.OriginalBefore.LastWriteUtc:O} / {result.OriginalAfter.LastWriteUtc:O}");
        builder.AppendLine($"Temporales eliminados: {(result.TempsDeleted ? "sí" : "NO")}");
        builder.AppendLine($"Sin procesos Access propios pendientes: {(result.NoNewAccessProcesses ? "sí" : "NO")}");
        builder.AppendLine();
        builder.AppendLine(
            "Vínculos externos: no se hicieron accesos intencionales a UNC, GDB ni TXT. " +
            "No se midió actividad de red ni de sistema de archivos fuera del MDB copiado.");
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
