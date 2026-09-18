using Microsoft.Extensions.Logging;
using MdbToSql.Access;
using MdbToSql.Core.Cli;
using MdbToSql.Core.Import;
using MdbToSql.Core.Mapping;
using MdbToSql.Core.Models;
using MdbToSql.Core.Reporting;
using MdbToSql.SqlServer;

namespace MdbToSql.App;

public sealed class ApplicationHost
{
    private readonly ImportSettings _settings;
    private readonly ILoggerFactory _loggerFactory;

    public ApplicationHost(ImportSettings settings, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _loggerFactory = loggerFactory;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var typeMapper = new AccessToSqlTypeMapper();
        var scanner = new AccessDatabaseScanner();
        var accessFactory = new AccessDatabaseFactory();
        var interaction = new ConsoleImportInteraction();

        SqlDatabaseInstaller? installer = null;
        SqlSchemaService? schema = null;
        SqlBulkImporter? bulk = null;
        ImportHistoryService? history = null;

        installer = new SqlDatabaseInstaller(
            _settings.SqlServerConnectionString,
            _settings.DestinationDatabase,
            _settings.CommandTimeoutSeconds,
            _loggerFactory.CreateLogger<SqlDatabaseInstaller>());

        if (_settings.Mode == ImportMode.Import
            || _settings.Mode == ImportMode.Analyze)
        {
            var destination = installer.GetDestinationConnectionString();
            schema = new SqlSchemaService(
                destination,
                typeMapper,
                _settings.CommandTimeoutSeconds);
            bulk = new SqlBulkImporter(destination, schema, _settings);
            history = new ImportHistoryService(destination, _settings.CommandTimeoutSeconds);
        }

        var coordinator = new ImportCoordinator(
            scanner,
            accessFactory,
            typeMapper,
            _settings,
            interaction,
            installer,
            schema,
            bulk,
            history);

        var result = _settings.Mode == ImportMode.Import
            ? await coordinator.ImportAsync(cancellationToken)
            : await coordinator.AnalyzeAsync(cancellationToken);

        Console.WriteLine();
        Console.Write(SummaryPrinter.Build(result));
        return result.IsSuccessful ? 0 : 2;
    }
}

internal sealed class ConsoleImportInteraction : IImportInteraction
{
    public void Inform(string message)
    {
        Console.WriteLine(message);
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        });

        try
        {
            var defaults = AppConfiguration.Load();
            ParseResult parsed;
            try
            {
                parsed = CommandLineParser.Parse(args, defaults);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }

            if (parsed.ShowHelp)
            {
                Console.WriteLine(CommandLineParser.HelpText());
                return 0;
            }

            if (parsed.Error is not null || parsed.Settings is null)
            {
                Console.Error.WriteLine(parsed.Error ?? "Argumentos no válidos.");
                Console.WriteLine();
                Console.WriteLine(CommandLineParser.HelpText());
                return 1;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
                Console.WriteLine("Cancelando... se eliminará la staging incompleta y se conservará la tabla original.");
            };

            var host = new ApplicationHost(parsed.Settings, loggerFactory);
            return await host.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Importación cancelada.");
            return 3;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("MdbToSql")
                .LogCritical(exception, "La aplicación no pudo ejecutarse.");
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
