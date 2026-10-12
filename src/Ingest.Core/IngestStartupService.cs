using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Ingest;

/// 등록된 IStartupStep(토폴로지 선언)을 성공할 때까지 2초 간격으로 재시도하고 ReadinessState에 반영
public sealed class IngestStartupService : BackgroundService
{
    private readonly IEnumerable<IStartupStep> _steps;
    private readonly ReadinessState _readiness;
    private readonly ILogger<IngestStartupService> _logger;

    public IngestStartupService(IEnumerable<IStartupStep> steps, ReadinessState readiness, ILogger<IngestStartupService> logger)
    {
        _steps = steps;
        _readiness = readiness;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        throw new NotImplementedException($"{_steps}{_readiness}{_logger}");
}
