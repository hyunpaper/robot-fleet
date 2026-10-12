using RobotFleet.Shared.Map;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Simulator.Fleet;

/// 로봇 N대 루프: 생성 → 할당 → StepAllAsync → 생애주기 → 이벤트 확정 → 송신 버퍼. 10 §1
public sealed class FleetSimulation
{
    private readonly GridMap _map;
    private readonly FleetOptions _options;
    private readonly IEventSink? _sink;
    private readonly ITickDelay _delay;
    private readonly ConsoleRenderer? _renderer;

    /// sink가 null이면 이벤트를 만들지 않는다
    public FleetSimulation(GridMap map, FleetOptions options, IEventSink? sink, ITickDelay delay, ConsoleRenderer? renderer)
    {
        _map = map;
        _options = options;
        _sink = sink;
        _delay = delay;
        _renderer = renderer;
    }

    public Task<FleetRunResult> RunAsync(CancellationToken ct) =>
        throw new NotImplementedException($"{_map}{_options}{_sink}{_delay}{_renderer}");
}
