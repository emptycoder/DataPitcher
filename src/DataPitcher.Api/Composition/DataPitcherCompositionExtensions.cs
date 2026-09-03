using DataPitcher.Api.Contracts;
using DataPitcher.Core.Connections;
using DataPitcher.Infrastructure.Connections;
using DataPitcher.Infrastructure.Events;
using DataPitcher.Infrastructure.Migrations;
using DataPitcher.Infrastructure.Plans;
using DataPitcher.Infrastructure.Schema;
using DataPitcher.Infrastructure.Selections;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Providers.PostgreSql;
using DataPitcher.Providers.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DataPitcher.Api.Composition;

public static class DataPitcherCompositionExtensions
{
    public static IServiceCollection AddDataPitcherComposition(this IServiceCollection services, IConfiguration configuration)
    {
        var controlDatabasePath = configuration["ControlDatabase:Path"] ?? throw new InvalidOperationException("ControlDatabase:Path must be configured.");
        var secretsRoot = configuration["Secrets:Root"] ?? throw new InvalidOperationException("Secrets:Root must be configured.");

        services.AddSingleton(new ControlDatabase($"Data Source={controlDatabasePath}"));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretReferenceResolver>(_ => new SecretReferenceResolver(secretsRoot));

        services.AddSingleton<JobEventSignal>();
        services.AddSingleton<IJobEventSignal>(provider => provider.GetRequiredService<JobEventSignal>());
        services.AddSingleton<JobEventStore>();
        services.AddSingleton<IJobEventWriter>(provider => provider.GetRequiredService<JobEventStore>());
        services.AddSingleton<IJobEventReader>(provider => provider.GetRequiredService<JobEventStore>());

        services.AddSingleton<ConnectionProfileStore>();
        services.AddSingleton<SchemaSnapshotStore>();
        services.AddSingleton<JobStore>();
        services.AddSingleton<SelectionStore>();
        services.AddSingleton<PlanStore>();
        services.AddSingleton<ConnectionHealthService>();

        services.AddSingleton<IConnectionProvider, PostgreSqlConnectionProvider>();
        services.AddSingleton<IConnectionProvider, SqlServerConnectionProvider>();
        services.AddSingleton<IConnectionProviderRegistry, ConnectionProviderRegistry>();

        services.AddHostedService<SchemaScanWorker>();

        services.AddSingleton<IDataPitcherApplication, DataPitcherApplication>();

        return services;
    }

    public static void ApplyControlDatabaseMigrations(this IServiceProvider services)
    {
        var database = services.GetRequiredService<ControlDatabase>();
        var clock = services.GetRequiredService<IClock>();
        new ControlDatabaseMigrator(database, clock).Apply();
    }
}
