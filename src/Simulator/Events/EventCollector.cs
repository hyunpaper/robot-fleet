using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Messaging;

namespace RobotFleet.Simulator.Events;

/// 틱 안의 발생을 받아 03 순서로 배치를 확정한다. 10 §1 "배치 안 순서":
/// ① TaskCreated ② 로봇별(robotId 오름차순) Conflict → Status → Task* ③ telemetry(robotId 오름차순).
/// SimulationStarted는 run 첫 배치의 맨 앞, SimulationEnded는 마지막 배치의 맨 뒤(03).
/// 1000건을 넘으면 순서를 유지한 채 자른다(두 번째 배치의 batchId가 더 크다)
public sealed class EventCollector
{
    private readonly string _mapId;
    private readonly string _runId;
    private readonly int _tickMs;
    private readonly UuidV7 _ids;
    private readonly int _maxPerBatch;
    private readonly List<EventItem> _created = [];
    private readonly List<EventItem> _head = [];
    private readonly List<EventItem> _tail = [];
    private readonly SortedDictionary<string, List<(int Rank, EventItem Item)>> _byRobot = new(StringComparer.Ordinal);
    private long _tick;
    private DateTimeOffset _at;

    public EventCollector(string mapId, string runId, int tickMs, TimeProvider time, int maxPerBatch = FleetSchema.MaxEventsPerBatch)
    {
        _mapId = mapId;
        _runId = runId;
        _tickMs = tickMs;
        _ids = new UuidV7(time);
        _maxPerBatch = maxPerBatch;
    }

    public void Begin(long tick, DateTimeOffset at)
    {
        _tick = tick;
        _at = at;
        _created.Clear();
        _byRobot.Clear();
        _head.Clear();
        _tail.Clear();
    }

    /// 이번 틱 배치의 맨 앞(run 첫 틱에 부른다)
    public void SimulationStarted(SimulationStartedPayload p) => _head.Add(Item(EventType.SimulationStarted, p));

    /// 이번 틱 배치의 맨 뒤(run 종료 시 부른다)
    public void SimulationEnded(SimulationEndedPayload p) => _tail.Add(Item(EventType.SimulationEnded, p));

    public void TaskCreated(TransportTask t) =>
        _created.Add(Item(EventType.TaskCreated, new TaskCreatedPayload(t.TaskId, (int)t.Type,
            new CellDto(t.Origin.X, t.Origin.Y), new CellDto(t.Destination.X, t.Destination.Y), t.Priority)));

    public void Conflict(string robotId, ConflictDetectedPayload p) => Add(robotId, 0, Item(EventType.ConflictDetected, p));

    /// DeadlockDetected(13 §7). 충돌과 같은 묶음(rank 0). 본문은 2단계 S2
    public void Deadlock(string robotId, DeadlockDetectedPayload p) => throw new NotImplementedException();

    /// PathReplanned(13 §7). rank 0. 본문은 2단계 S2
    public void Replan(string robotId, PathReplannedPayload p) => throw new NotImplementedException();

    public void Status(string robotId, RobotStatus from, RobotStatus to, string reason)
    {
        if (!StatusTransitions.IsAllowed(from, to, reason))
            throw new InvalidOperationException($"{robotId}: 03 전이 표에 없는 전이 {from} → {to} ({reason})");
        Add(robotId, 1, Item(EventType.RobotStatusChanged, new RobotStatusChangedPayload(robotId, (int)from, (int)to, reason)));
    }

    public void TaskEvent<T>(string robotId, string eventType, T payload) => Add(robotId, 2, Item(eventType, payload));

    /// telemetry를 붙여 배치를 확정한다
    public IReadOnlyList<EventBatch> Complete(IEnumerable<RobotTelemetryPayload> telemetry)
    {
        var all = new List<EventItem>(_head);
        all.AddRange(_created);
        foreach (var events in _byRobot.Values)
            all.AddRange(events.OrderBy(e => e.Rank).Select(e => e.Item));   // OrderBy는 안정 정렬
        all.AddRange(telemetry.OrderBy(t => t.RobotId, StringComparer.Ordinal).Select(t => Item(EventType.RobotTelemetry, t)));
        all.AddRange(_tail);

        var batches = new List<EventBatch>();
        for (var i = 0; i < all.Count; i += _maxPerBatch)
        {
            var part = all.GetRange(i, Math.Min(_maxPerBatch, all.Count - i));
            batches.Add(new EventBatch(FleetSchema.Version, _ids.Next(), _mapId, _runId, _tick, _tickMs, _at, part));
        }

        return batches;
    }

    private void Add(string robotId, int rank, EventItem item)
    {
        if (!_byRobot.TryGetValue(robotId, out var list))
            _byRobot[robotId] = list = [];
        list.Add((rank, item));
    }

    private static EventItem Item<T>(string type, T payload) => new(type, FleetJson.ToElement(payload));
}
