using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.RabbitMq;

/// TopologySpec.Build(prefix)를 exchange → 큐 → 바인딩 순으로 선언. 인자 불일치 · 플러그인 없음은 예외. 10 §3
public sealed class TopologyDeclarer : IStartupStep
{
    private readonly RabbitConnectionProvider _connections;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<TopologyDeclarer> _logger;

    public TopologyDeclarer(RabbitConnectionProvider connections, RabbitMqOptions options, ILogger<TopologyDeclarer> logger)
    {
        _connections = connections;
        _options = options;
        _logger = logger;
    }

    public string Name => ReadinessComponents.RabbitTopology;

    public Task RunAsync(CancellationToken ct) => throw new NotImplementedException($"{_connections}{_options}{_logger}");
}
