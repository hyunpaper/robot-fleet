// 10 §8, §13
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace RobotFleet.Infrastructure.Postgres.Migrations;

public sealed class Migrator(NpgsqlDataSource dataSource, string migrationsDirectory, ILogger<Migrator> logger)
{
    /// advisory lock(hashtext('fleet:migrations')) 안에서 schema_migrations에 없는 파일을 이름 순으로,
    /// 파일마다 트랜잭션 1개. 적용된 파일의 SHA-256이 다르면 MigrationChecksumException. 반환 = 이번에 적용한 버전
    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct)
    {
        var files = Directory.GetFiles(migrationsDirectory, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await ExecuteAsync(conn, "SELECT pg_advisory_lock(hashtext('fleet:migrations'))", ct);
        var applied = new List<string>();
        try
        {
            await ExecuteAsync(
                conn,
                "CREATE TABLE IF NOT EXISTS schema_migrations (" +
                "version text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())",
                ct);

            var history = new Dictionary<string, string>();
            await using (var cmd = new NpgsqlCommand("SELECT version, checksum FROM schema_migrations", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    history[reader.GetString(0)] = reader.GetString(1);
                }
            }

            foreach (var file in files)
            {
                var version = Path.GetFileNameWithoutExtension(file);
                var sql = await File.ReadAllTextAsync(file, ct);
                var checksum = Checksum(sql);
                if (history.TryGetValue(version, out var stored))
                {
                    if (stored != checksum)
                    {
                        throw new MigrationChecksumException(version);
                    }

                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(sql, conn, tx))
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await using (var cmd = new NpgsqlCommand(
                    "INSERT INTO schema_migrations (version, checksum) VALUES (@v, @c)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("v", version);
                    cmd.Parameters.AddWithValue("c", checksum);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                applied.Add(version);
                logger.LogInformation("마이그레이션 적용: {Version}", version);
            }
        }
        finally
        {
            await ExecuteAsync(conn, "SELECT pg_advisory_unlock(hashtext('fleet:migrations'))", CancellationToken.None);
        }

        return applied;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // 줄바꿈 차이(CRLF/LF)로 체크섬이 달라지지 않게 정규화
    private static string Checksum(string sql)
    {
        var normalized = sql.Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}

public sealed class MigrationChecksumException(string version)
    : Exception($"적용된 마이그레이션이 변경됨: {version}")
{
    public string Version { get; } = version;
}
