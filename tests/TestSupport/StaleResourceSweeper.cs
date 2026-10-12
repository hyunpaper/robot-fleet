using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using StackExchange.Redis;

namespace RobotFleet.TestSupport;

public static class StaleResourceSweeper
{
    private static readonly TimeSpan Age = TimeSpan.FromHours(1);

    public static async Task<int> SweepRedisAsync(IConnectionMultiplexer redis, DateTimeOffset now, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var removed = 0;
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            await foreach (var key in server.KeysAsync(pattern: "t*:*").WithCancellation(ct))
            {
                if (TestRun.IsStale(key.ToString(), now, Age) && await db.KeyDeleteAsync(key))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    public static async Task<int> SweepPostgresAsync(NpgsqlDataSource pg, DateTimeOffset now, CancellationToken ct)
    {
        var schemas = new List<string>();
        await using (var list = pg.CreateCommand("SELECT nspname FROM pg_namespace WHERE nspname LIKE 't\\_t%'"))
        await using (var reader = await list.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                schemas.Add(reader.GetString(0));
            }
        }

        var removed = 0;
        foreach (var schema in schemas.Where(s => TestRun.IsStale(s, now, Age)))
        {
            await using var drop = pg.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await drop.ExecuteNonQueryAsync(ct);
            removed++;
        }

        return removed;
    }

    /// FLEET_TEST_RABBITMQ_MGMT(관리 API URL)가 있을 때만. 없으면 0 반환
    public static async Task<int> SweepRabbitAsync(string? managementUrl, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(managementUrl))
        {
            return 0;
        }

        var uri = new Uri(managementUrl.TrimEnd('/') + "/");
        using var http = new HttpClient { BaseAddress = new Uri(uri.GetLeftPart(UriPartial.Path)) };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo)));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credential);
        }

        var removed = 0;
        foreach (var kind in new[] { "queues", "exchanges" })
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"api/{kind}", ct));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? string.Empty;
                var vhost = item.GetProperty("vhost").GetString() ?? "/";
                if (!TestRun.IsStale(name, now, Age))
                {
                    continue;
                }

                var url = $"api/{kind}/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}";
                using var response = await http.DeleteAsync(url, ct);
                if (response.IsSuccessStatusCode)
                {
                    removed++;
                }
            }
        }

        return removed;
    }
}
