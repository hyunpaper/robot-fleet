using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.EventWorker;

/// 기동 단계(마이그레이션 → 파티션 → 토폴로지)를 등록 순서대로 실행한 뒤 AssignmentCoordinator를 돌린다. 10 §12.
/// 기동 단계 실패는 예외 그대로 — 호스트가 멈추고 재시작 정책이 재시도한다
public sealed class WorkerService : BackgroundService
{
    private readonly IReadOnlyList<IStartupStep> _steps;
    private readonly AssignmentCoordinator _coordinator;
    private readonly ReadinessState _readiness;
    private readonly ILogger<WorkerService> _logger;

    public WorkerService(IEnumerable<IStartupStep> steps, AssignmentCoordinator coordinator,
                         ReadinessState readiness, ILogger<WorkerService> logger)
    {
        _steps = steps.ToList();
        _coordinator = coordinator;
        _readiness = readiness;
        _logger = logger;
        foreach (var step in _steps)
            _readiness.Require(step.Name);
        _readiness.Require(ReadinessComponents.WorkerConsuming);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var step in _steps)
        {
            await step.RunAsync(stoppingToken);
            _readiness.Set(step.Name, true);
            _logger.LogInformation("기동 단계 {Step} 완료", step.Name);
        }

        _readiness.Set(ReadinessComponents.WorkerConsuming, true);
        _logger.LogInformation("워커 {Worker} 소비 시작", _coordinator.WorkerId);
        await _coordinator.RunAsync(stoppingToken);
    }
}
