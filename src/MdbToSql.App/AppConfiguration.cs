using Microsoft.Extensions.Configuration;
using MdbToSql.Core.Models;

namespace MdbToSql.App;

public static class AppConfiguration
{
    public static ImportSettings Load(string? basePath = null)
    {
        var path = basePath ?? AppContext.BaseDirectory;
        var configuration = new ConfigurationBuilder()
            .SetBasePath(path)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var settings = new ImportSettings();
        var sql = configuration.GetConnectionString("SqlServer");
        if (!string.IsNullOrWhiteSpace(sql))
        {
            settings.SqlServerConnectionString = sql;
        }

        var section = configuration.GetSection("Import");
        settings.SourceDirectory = section["SourceDirectory"] ?? settings.SourceDirectory;
        settings.DestinationDatabase = section["DestinationDatabase"] ?? settings.DestinationDatabase;
        if (int.TryParse(section["BatchSize"], out var batchSize) && batchSize > 0)
        {
            settings.BatchSize = batchSize;
        }

        if (int.TryParse(section["CommandTimeoutSeconds"], out var timeout) && timeout > 0)
        {
            settings.CommandTimeoutSeconds = timeout;
        }

        return settings;
    }
}
