using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.EventWorker;

/// 기동 단계(마이그레이션 → 파티션 → 토폴로지)를 등록 순서대로 실행한 뒤 AssignmentCoordinator를 돌린다. 10 §12
public sealed class WorkerService : BackgroundService
{
    private readonly IEnumerable<IStartupStep> _steps;
    private readonly AssignmentCoordinator _coordinator;
    private readonly ReadinessState _readiness;
    private readonly ILogger<WorkerService> _logger;

    public WorkerService(IEnumerable<IStartupStep> steps, AssignmentCoordinator coordinator,
                         ReadinessState readiness, ILogger<WorkerService> logger)
    {
        _steps = steps;
        _coordinator = coordinator;
        _readiness = readiness;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        throw new NotImplementedException($"{_steps}{_coordinator}{_readiness}{_logger}");
}
