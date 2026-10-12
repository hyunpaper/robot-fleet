using System.Collections.Concurrent;

namespace RobotFleet.Shared.Pipeline;

/// /health/ready 판정. 요구 구성 요소가 전부 준비되면 Ready. 10 §11
public sealed class ReadinessState
{
    private readonly ConcurrentDictionary<string, bool> _components = new(StringComparer.Ordinal);

    /// 준비 전(false)으로 등록. 이미 있으면 그대로
    public void Require(string component) => _components.TryAdd(component, false);

    public void Set(string component, bool ready) => _components[component] = ready;

    public bool IsReady => !_components.IsEmpty && _components.Values.All(v => v);

    public IReadOnlyDictionary<string, bool> Snapshot() =>
        new SortedDictionary<string, bool>(_components, StringComparer.Ordinal);
}

public static class ReadinessComponents
{
    public const string RabbitTopology = "rabbitmq.topology";
    public const string PostgresMigrations = "postgres.migrations";
    public const string WorkerConsuming = "worker.consuming";
}
