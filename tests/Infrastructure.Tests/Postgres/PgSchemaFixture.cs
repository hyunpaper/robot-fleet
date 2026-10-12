// 10 §13 S2: 실행별 스키마를 만들고 지우는 L2 공용 장치
using Npgsql;
using RobotFleet.TestSupport;

namespace RobotFleet.Infrastructure.Tests.Postgres;

internal sealed class PgSchemaFixture : IAsyncDisposable
{
    private readonly NpgsqlDataSource admin;

    private PgSchemaFixture(NpgsqlDataSource admin, NpgsqlDataSource scoped, string schema)
    {
        this.admin = admin;
        DataSource = scoped;
        Schema = schema;
    }

    public NpgsqlDataSource DataSource { get; }

    public string Schema { get; }

    public static string MigrationsDirectory => Path.Combine(AppContext.BaseDirectory, "migrations");

    public static async Task<PgSchemaFixture> CreateAsync()
    {
        var schema = TestRun.New().PgSchema;
        var admin = NpgsqlDataSource.Create(TestEnv.Pg!);
        await using (var cmd = admin.CreateCommand($"CREATE SCHEMA \"{schema}\""))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(TestEnv.Pg!) { SearchPath = schema };
        return new PgSchemaFixture(admin, NpgsqlDataSource.Create(builder.ConnectionString), schema);
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await using (var cmd = admin.CreateCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await admin.DisposeAsync();
    }
}
