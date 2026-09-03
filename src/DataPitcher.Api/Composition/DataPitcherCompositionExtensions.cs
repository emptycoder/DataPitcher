using DataPitcher.Api.Contracts;
using DataPitcher.Core.Connections;
using DataPitcher.Infrastructure.Checkpoints;
using DataPitcher.Infrastructure.Connections;
using DataPitcher.Infrastructure.Events;
using DataPitcher.Infrastructure.Leasing;
using DataPitcher.Infrastructure.Migrations;
using DataPitcher.Infrastructure.Persistence;
using DataPitcher.Infrastructure.Plans;
using DataPitcher.Infrastructure.Schema;
using DataPitcher.Infrastructure.Selections;
using DataPitcher.Infrastructure.Storage;
using DataPitcher.Infrastructure.Time;
using DataPitcher.Infrastructure.Worker;
using DataPitcher.Providers.PostgreSql;
using DataPitcher.Providers.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DataPitcher.Api.Composition;

public static class DataPitcherCompositionExtensions
{
    public static IServiceCollection AddDataPitcherComposition(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var controlDatabasePath =
            configuration["ControlDatabase:Path"]
            ?? throw new InvalidOperationException("ControlDatabase:Path must be configured.");
        var secretsRoot =
            configuration["Secrets:Root"] ?? throw new InvalidOperationException("Secrets:Root must be configured.");
        var workerLeaseTtl = configuration.GetValue("Worker:LeaseTtl", TimeSpan.FromMinutes(1));
        var workerPollInterval = configuration.GetValue("Worker:PollInterval", TimeSpan.FromSeconds(1));

        services.AddSingleton(new ControlDatabase($"Data Source={controlDatabasePath}"));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretReferenceResolver>(_ => new SecretReferenceResolver(secretsRoot));
        services.AddSingleton<ISecretWriter>(_ => new FileSecretStore(secretsRoot));

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
        services.AddSingleton<ISealingProvider, SqlServerSealingProvider>();
        services.AddSingleton<ISealingProvider, PostgreSqlSealingProvider>();
        services.AddSingleton<PlanSealingService>();
        services.AddSingleton<ConnectionHealthService>();
        services.AddSingleton<IJobControl>(provider => provider.GetRequiredService<JobStore>());
        services.AddSingleton<IControlCheckpointMirror, CheckpointMirrorStore>();
        services.AddSingleton<ITransferConnectionRevalidator>(provider =>
            provider.GetRequiredService<ConnectionHealthService>()
        );
        services.AddSingleton<IWorkerFaults, NoOpWorkerFaults>();
        services.AddSingleton<IWorkerDelay, ClockWorkerDelay>();
        services.AddSingleton<IJobRunCatalog, PlanJobRunCatalog>();
        services.AddSingleton<IRunSessionProvider, SqlServerRunSessions>();
        services.AddSingleton<IRunSessionProvider, PostgreSqlRunSessions>();
        services.AddSingleton<ProviderRunSessionRouter>();
        services.AddSingleton<ITransferReadSessionFactory>(provider =>
            provider.GetRequiredService<ProviderRunSessionRouter>()
        );
        services.AddSingleton<ITargetRunSessionFactory>(provider =>
            provider.GetRequiredService<ProviderRunSessionRouter>()
        );
        services.AddSingleton<LeaseStore>();
        services.AddSingleton<LeaseRenewer>();
        services.AddSingleton<RecoveryCoordinator>();

        services.AddSingleton<IConnectionProvider, PostgreSqlConnectionProvider>();
        services.AddSingleton<IConnectionProvider, SqlServerConnectionProvider>();
        services.AddSingleton<IConnectionProviderRegistry, ConnectionProviderRegistry>();

        services.AddHostedService<SchemaScanWorker>();
        services.AddHostedService(provider => new JobWorker(
            provider.GetRequiredService<IJobControl>(),
            provider.GetRequiredService<IJobRunCatalog>(),
            provider.GetRequiredService<ITransferConnectionRevalidator>(),
            provider.GetRequiredService<ITargetRunSessionFactory>(),
            provider.GetRequiredService<ITransferReadSessionFactory>(),
            provider.GetRequiredService<RecoveryCoordinator>(),
            provider.GetRequiredService<LeaseRenewer>(),
            provider.GetRequiredService<IControlCheckpointMirror>(),
            provider.GetRequiredService<IJobEventWriter>(),
            provider.GetRequiredService<IWorkerFaults>(),
            provider.GetRequiredService<IWorkerDelay>(),
            provider.GetRequiredService<IClock>(),
            Environment.MachineName + "-" + Environment.ProcessId,
            workerLeaseTtl,
            workerPollInterval
        ));

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
