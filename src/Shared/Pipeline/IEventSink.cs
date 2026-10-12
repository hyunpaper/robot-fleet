using RobotFleet.Shared.Messaging;

namespace RobotFleet.Shared.Pipeline;

/// Simulator 틱 루프 → 송신 버퍼
public interface IEventSink
{
    /// 대기 없음
    bool TryWrite(EventBatch batch);

    /// false = 닫힘
    ValueTask<bool> WaitToWriteAsync(CancellationToken ct);
}
