using Npgsql;

namespace RobotFleet.Reconcile;

public interface IReconcileQueries
{
    Task<CategoryCount> TaskAsync(NpgsqlConnection conn, string mapId, CancellationToken ct);

    Task<CategoryCount> ConflictAsync(NpgsqlConnection conn, string mapId, CancellationToken ct);

    Task<CategoryCount> TelemetryAsync(NpgsqlConnection conn, string mapId, CancellationToken ct);

    Task<long> InvalidTransitionsAsync(NpgsqlConnection conn, string mapId, CancellationToken ct);
}
