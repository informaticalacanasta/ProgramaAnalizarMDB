using MdbToSql.Core.Models;

namespace MdbToSql.Core.Cli;

public sealed record ParseResult(
    ImportSettings? Settings,
    bool ShowHelp,
    string? Error);

public static class CommandLineParser
{
    public static ParseResult Parse(string[] args, ImportSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(defaults);

        var settings = Clone(defaults);
        var modeSpecified = false;

        try
        {
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument)
                {
                    case "-h":
                    case "--help":
                        return new(null, true, null);
                    case "--analyze":
                        EnsureSingleMode(modeSpecified);
                        settings.Mode = ImportMode.Analyze;
                        modeSpecified = true;
                        break;
                    case "--import":
                        EnsureSingleMode(modeSpecified);
                        settings.Mode = ImportMode.Import;
                        modeSpecified = true;
                        break;
                    case "--source":
                        settings.SourceDirectory = ReadValue(args, ref index);
                        break;
                    case "--database":
                        settings.DestinationDatabase = ReadValue(args, ref index);
                        break;
                    case "--force":
                        settings.ForceImport = true;
                        break;
                    case "--batch-size":
                        settings.BatchSize = ParsePositiveInt(ReadValue(args, ref index), "--batch-size");
                        break;
                    case "--timeout":
                        settings.CommandTimeoutSeconds = ParsePositiveInt(
                            ReadValue(args, ref index),
                            "--timeout");
                        break;
                    default:
                        return new(
                            null,
                            false,
                            $"Argumento desconocido: {argument}. Use --help.");
                }
            }

            if (settings.BatchSize <= 0 || settings.CommandTimeoutSeconds <= 0)
            {
                return new(null, false, "BatchSize y timeout deben ser positivos.");
            }

            if (string.IsNullOrWhiteSpace(settings.SourceDirectory))
            {
                return new(null, false, "SourceDirectory no puede estar vacío.");
            }

            if (string.IsNullOrWhiteSpace(settings.DestinationDatabase))
            {
                return new(null, false, "DestinationDatabase no puede estar vacío.");
            }

            return new(settings, false, null);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return new(null, false, exception.Message);
        }
    }

    public static string HelpText()
    {
        return """
            MdbToSql — importación directa Microsoft Access (.mdb) → SQL Server

            Uso:
              MdbToSql.exe --analyze
              MdbToSql.exe --import
              MdbToSql.exe --analyze --source "C:\OtraCarpeta"
              MdbToSql.exe --import --database "OtraBD"

            Opciones:
              --analyze              Analiza los MDB sin modificar SQL (predeterminado)
              --import               Importa a SQL Server
              --source <ruta>        Carpeta de origen de archivos .mdb
              --database <nombre>    Base de datos destino
              --force                Reimporta tablas aunque el hash coincida
              --batch-size <n>       Tamaño de lote de SqlBulkCopy
              --timeout <segundos>   Timeout de comandos SQL
              --help                 Muestra esta ayuda
            """;
    }

    private static void EnsureSingleMode(bool modeSpecified)
    {
        if (modeSpecified)
        {
            throw new ArgumentException("Indique solo --analyze o --import.");
        }
    }

    private static string ReadValue(string[] args, ref int index)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Falta el valor para {args[index - 1]}.");
        }

        return args[index];
    }

    private static int ParsePositiveInt(string value, string name)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{name} debe ser un entero positivo.");
        }

        return parsed;
    }

    private static ImportSettings Clone(ImportSettings source)
    {
        return new ImportSettings
        {
            SqlServerConnectionString = source.SqlServerConnectionString,
            SourceDirectory = source.SourceDirectory,
            DestinationDatabase = source.DestinationDatabase,
            BatchSize = source.BatchSize,
            CommandTimeoutSeconds = source.CommandTimeoutSeconds,
            ForceImport = source.ForceImport,
            Mode = source.Mode
        };
    }
}
