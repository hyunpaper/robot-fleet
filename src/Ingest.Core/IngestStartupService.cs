using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Ingest;

/// 등록된 IStartupStep(토폴로지 선언)을 성공할 때까지 2초 간격으로 재시도하고 ReadinessState에 반영
public sealed class IngestStartupService : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<IStartupStep> _steps;
    private readonly ReadinessState _readiness;
    private readonly ILogger<IngestStartupService> _logger;

    public IngestStartupService(IEnumerable<IStartupStep> steps, ReadinessState readiness, ILogger<IngestStartupService> logger)
    {
        _steps = steps.ToList();
        _readiness = readiness;
        _logger = logger;
        foreach (var step in _steps)
            _readiness.Require(step.Name);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var step in _steps)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await step.RunAsync(stoppingToken);
                    _readiness.Set(step.Name, true);
                    _logger.LogInformation("기동 단계 {Step} 완료", step.Name);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "기동 단계 {Step} 실패, {Seconds}초 후 재시도", step.Name, RetryInterval.TotalSeconds);
                    await Task.Delay(RetryInterval, stoppingToken);
                }
            }
        }
    }
}
