using Npgsql;

namespace RobotFleet.Reconcile;

// 자리표시: 본문은 2차에서 채운다
public sealed class ReconcileQueries : IReconcileQueries
{
    public Task<CategoryCount> TaskAsync(NpgsqlConnection conn, string mapId, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<CategoryCount> ConflictAsync(NpgsqlConnection conn, string mapId, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<CategoryCount> TelemetryAsync(NpgsqlConnection conn, string mapId, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<long> InvalidTransitionsAsync(NpgsqlConnection conn, string mapId, CancellationToken ct) =>
        throw new NotImplementedException();
}
