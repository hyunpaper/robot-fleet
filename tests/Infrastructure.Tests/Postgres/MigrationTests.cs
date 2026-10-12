// 10 §12 D-01, D-02
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RobotFleet.Infrastructure.Postgres.Migrations;
using RobotFleet.TestSupport;

namespace RobotFleet.Infrastructure.Tests.Postgres;

[Trait("Category", "Integration")]
public sealed class MigrationTests
{
    private static readonly string[] AllVersions = ["001_schema", "002_logs_and_state", "003_partitions"];

    [IntegrationFact("FLEET_TEST_PG")]
    [Trait("Category", "Integration")]
    public async Task D01_전부_적용_재기동은_무동작_체크섬_변경은_실패()
    {
        await using var pg = await PgSchemaFixture.CreateAsync();
        var dir = CopyMigrations();
        try
        {
            var migrator = new Migrator(pg.DataSource, dir, NullLogger<Migrator>.Instance);

            (await migrator.ApplyAsync(CancellationToken.None)).Should().Equal(AllVersions);
            (await migrator.ApplyAsync(CancellationToken.None)).Should().BeEmpty();

            await File.AppendAllTextAsync(Path.Combine(dir, "001_schema.sql"), "\n-- 변경\n");
            var act = () => migrator.ApplyAsync(CancellationToken.None);
            (await act.Should().ThrowAsync<MigrationChecksumException>()).Which.Version.Should().Be("001_schema");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [IntegrationFact("FLEET_TEST_PG")]
    [Trait("Category", "Integration")]
    public async Task D02_동시_기동해도_한_번만_적용()
    {
        await using var pg = await PgSchemaFixture.CreateAsync();
        var a = new Migrator(pg.DataSource, PgSchemaFixture.MigrationsDirectory, NullLogger<Migrator>.Instance);
        var b = new Migrator(pg.DataSource, PgSchemaFixture.MigrationsDirectory, NullLogger<Migrator>.Instance);

        var results = await Task.WhenAll(a.ApplyAsync(CancellationToken.None), b.ApplyAsync(CancellationToken.None));

        results.SelectMany(r => r).Should().BeEquivalentTo(AllVersions);
        (await pg.ScalarAsync("SELECT count(*) FROM schema_migrations")).Should().Be(AllVersions.Length);
    }

    private static string CopyMigrations()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fleet-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(PgSchemaFixture.MigrationsDirectory, "*.sql"))
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }

        return dir;
    }
}
