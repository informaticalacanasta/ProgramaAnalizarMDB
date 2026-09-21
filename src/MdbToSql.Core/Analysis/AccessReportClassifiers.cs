using MdbToSql.Core.Models;

namespace MdbToSql.Core.Analysis;

public static class AccessReportProbeSelector
{
    public const string Rule =
        "Enumerar informes DAO sin abrirlos. Excluir nombres que contienen 'sub' (posible subinforme). " +
        "Si no queda ninguno, usar la lista completa. Elegir el nombre más corto; " +
        "empate por orden alfabético ordinal sin distinción de mayúsculas.";

    public static string Choose(IReadOnlyList<string> reportNames)
    {
        if (reportNames.Count == 0)
        {
            throw new InvalidOperationException("No hay informes que seleccionar.");
        }

        var candidates = reportNames
            .Where(name => name.IndexOf("sub", StringComparison.OrdinalIgnoreCase) < 0)
            .ToList();
        if (candidates.Count == 0)
        {
            candidates = reportNames.ToList();
        }

        return candidates
            .OrderBy(name => name.Length)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .First();
    }
}

public static class AccessReportSectionClassifier
{
    public static AccessReportSectionKind Classify(int? rawType)
    {
        return rawType switch
        {
            0 => AccessReportSectionKind.Detail,
            1 => AccessReportSectionKind.ReportHeader,
            2 => AccessReportSectionKind.ReportFooter,
            3 => AccessReportSectionKind.PageHeader,
            4 => AccessReportSectionKind.PageFooter,
            { } value when value >= 5 && (value - 5) % 2 == 0 => AccessReportSectionKind.GroupHeader,
            { } value when value >= 6 && (value - 6) % 2 == 0 => AccessReportSectionKind.GroupFooter,
            _ => AccessReportSectionKind.Unknown
        };
    }

    public static int? GroupLevel(int? rawType)
    {
        if (rawType is null || rawType < 5)
        {
            return null;
        }

        return ((rawType.Value - 5) / 2) + 1;
    }

    public static string Name(int? rawType)
    {
        var kind = Classify(rawType);
        var level = GroupLevel(rawType);
        return kind is AccessReportSectionKind.GroupHeader or AccessReportSectionKind.GroupFooter
            ? $"{kind}{level}"
            : kind.ToString();
    }

    public static string? MapControlSection(string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return section;
        }

        if (string.Equals(section, "FormHeader", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(AccessReportSectionKind.ReportHeader);
        }

        if (string.Equals(section, "FormFooter", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(AccessReportSectionKind.ReportFooter);
        }

        if (int.TryParse(section, out var raw))
        {
            return Name(raw);
        }

        return section;
    }
}
