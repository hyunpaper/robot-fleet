namespace RobotFleet.Shared.Pipeline;

/// Bypass = 저장소 불통(fail-open)
public enum IdemBeginStatus { Begin, Takeover, Done, Busy, Bypass }

public readonly record struct IdemBegin(IdemBeginStatus Status, long PendingSinceMs);

public interface IIdemStore
{
    Task<IdemBegin> TryBeginAsync(string batchId, CancellationToken ct);

    Task CompleteAsync(string batchId, CancellationToken ct);

    /// 내 pending일 때만 삭제
    Task AbortAsync(string batchId, long pendingSinceMs, CancellationToken ct);
}
