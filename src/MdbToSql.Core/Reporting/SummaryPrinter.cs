using System.Globalization;
using System.Text;
using MdbToSql.Core.Models;

namespace MdbToSql.Core.Reporting;

public static class SummaryPrinter
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("es-ES");

    public static string FormatNumber(long value)
    {
        return value.ToString("N0", DisplayCulture);
    }

    public static string Build(PipelineResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("================================================");
        builder.AppendLine("RESUMEN");
        builder.AppendLine("================================================");
        builder.AppendLine($"MDB encontrados:        {FormatNumber(result.MdbFound)}");
        builder.AppendLine($"MDB procesados:         {FormatNumber(result.MdbProcessed)}");
        if (result.MdbFailed > 0)
        {
            builder.AppendLine($"MDB con error:          {FormatNumber(result.MdbFailed)}");
        }

        builder.AppendLine($"Tablas encontradas:     {FormatNumber(result.TablesFound)}");
        if (result.Mode == ImportMode.Import)
        {
            builder.AppendLine($"Tablas importadas:      {FormatNumber(result.TablesImported)}");
            builder.AppendLine($"Tablas omitidas:        {FormatNumber(result.TablesSkipped)}");
            builder.AppendLine($"Tablas con error:       {FormatNumber(result.TablesWithError)}");
            builder.AppendLine($"Registros importados:   {FormatNumber(result.RecordsImported)}");
        }
        else
        {
            builder.AppendLine($"Tablas con error:       {FormatNumber(result.TablesWithError)}");
            builder.AppendLine($"Colisiones de nombre:   {FormatNumber(result.Collisions.Count)}");
        }

        builder.AppendLine("================================================");
        return builder.ToString();
    }

    public static string FormatCollisions(IReadOnlyList<TableCollision> collisions)
    {
        if (collisions.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("COLISIONES DE NOMBRE");
        builder.AppendLine("No se mezclan filas ni se sobrescribe automáticamente.");
        foreach (var collision in collisions)
        {
            builder.AppendLine($"  {collision.TableName}:");
            foreach (var mdb in collision.MdbNames)
            {
                builder.AppendLine($"    - {mdb}");
            }
        }

        return builder.ToString();
    }
}
