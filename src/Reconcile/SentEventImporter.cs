using Npgsql;
using NpgsqlTypes;

namespace RobotFleet.Reconcile;

public static class SentEventImporter
{
    /// 임시 테이블 sent(event_id, run_id, batch_id, tick, event_type, category, key)에 binary COPY. 반환 = 행 수
    public static async Task<long> ImportAsync(NpgsqlConnection conn, IEnumerable<SentEvent> events, CancellationToken ct)
    {
        await using (var create = new NpgsqlCommand(
            "CREATE TEMP TABLE sent (event_id text NOT NULL, run_id text NOT NULL, batch_id text NOT NULL, tick bigint NOT NULL, "
            + "event_type text NOT NULL, category text NOT NULL, key text NOT NULL)", conn))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY sent (event_id, run_id, batch_id, tick, event_type, category, key) FROM STDIN (FORMAT BINARY)", ct);
        long count = 0;
        foreach (var e in events)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(e.EventId, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.RunId, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.BatchId, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.Tick, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(e.EventType, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.Category, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.Key, NpgsqlDbType.Text, ct);
            count++;
        }

        await writer.CompleteAsync(ct);
        return count;
    }
}
