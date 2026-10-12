using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using RobotFleet.Infrastructure.Postgres.Migrations;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Postgres;

public static class PostgresServiceCollectionExtensions
{
    /// NpgsqlDataSource(MaxPoolSize · CommandTimeout 적용) · PgEventStore(IEventStore) · Migrator(출력 폴더 migrations/) ·
    /// PartitionMaintainer · PostgresStartupStep(IStartupStep) 등록. ConnectionString이 비면 InvalidOperationException.
    /// 매시 파티션 유지 · DEFAULT 행 게이지는 호스트(S3)가 PartitionMaintainer를 써서 돌린다
    public static IServiceCollection AddFleetPostgres(this IServiceCollection services, Action<PostgresOptions> configure)
    {
        var options = new PostgresOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:Postgres가 비어 있다 (appsettings.Local.json 또는 ConnectionStrings__Postgres)");

        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString)
        {
            MaxPoolSize = options.MaxPoolSize,
            CommandTimeout = options.CommandTimeoutSeconds,
        };
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => NpgsqlDataSource.Create(builder.ConnectionString));
        services.AddSingleton<IEventStore, PgEventStore>();
        services.AddSingleton(sp => new Migrator(
            sp.GetRequiredService<NpgsqlDataSource>(),
            Path.Combine(AppContext.BaseDirectory, "migrations"),
            sp.GetRequiredService<ILogger<Migrator>>()));
        services.AddSingleton<PartitionMaintainer>();
        services.AddSingleton<IStartupStep, PostgresStartupStep>();
        return services;
    }
}
