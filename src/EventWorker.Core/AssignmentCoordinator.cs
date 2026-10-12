using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.EventWorker;

/// 명부 기반 파티션 배정 · 재배정(새 몫 구독 → 옛 몫 취소). 10 §7
public sealed class AssignmentCoordinator
{
    private readonly IPartitionRoster _roster;
    private readonly IPartitionSubscriber _subscriber;
    private readonly EventHandlers _handlers;
    private readonly WorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AssignmentCoordinator> _logger;

    public AssignmentCoordinator(IPartitionRoster roster, IPartitionSubscriber subscriber, EventHandlers handlers,
                                 WorkerOptions options, TimeProvider time, ILogger<AssignmentCoordinator> logger)
    {
        _roster = roster;
        _subscriber = subscriber;
        _handlers = handlers;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// 현재 소유한 파티션 번호(0~15)
    public IReadOnlyCollection<int> OwnedPartitions =>
        throw new NotImplementedException($"{_roster}{_subscriber}{_handlers}{_options}{_time}{_logger}");

    /// 취소될 때까지 heartbeat · 재계산 · 구독 전환. 종료 시 모든 구독 해제 + 명부 탈퇴
    public Task RunAsync(CancellationToken ct) => throw new NotImplementedException();
}
