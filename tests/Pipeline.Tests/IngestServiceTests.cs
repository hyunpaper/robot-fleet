using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RobotFleet.Infrastructure.InMemory;
using RobotFleet.Ingest;
using RobotFleet.Shared.Messaging;
using static RobotFleet.Pipeline.Tests.TestBatches;

namespace RobotFleet.Pipeline.Tests;

/// I-01~I-16 (10 §12). 08 §9 상태 전이 표는 행마다 하나
public sealed class IngestServiceTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-12T00:00:00Z"));
    private readonly InMemoryIdemStore _idem;
    private readonly InMemoryBroker _broker = new();
    private readonly IngestService _svc;

    public IngestServiceTests()
    {
        _idem = new InMemoryIdemStore(_time);
        _svc = new IngestService(_idem, _broker, _time, NullLogger<IngestService>.Instance);
    }

    private long QueuedTotal() =>
        Enumerable.Range(0, TopologySpec.Partitions).Sum(p =>
            _broker.Depth(TopologySpec.QueueName(EventCategory.Task, p)) + _broker.Depth(TopologySpec.QueueName(EventCategory.Conflict, p))
            + _broker.Depth(TopologySpec.QueueName(EventCategory.Telemetry, p)));

    [Fact]
    public async Task I01_신규_배치는_발행하고_done()
    {
        var batch = Batch(1, Created("T-1"), Telemetry("R-1"));
        var resp = await _svc.HandleAsync(Body(batch), default);
        resp.StatusCode.Should().Be(202);
        resp.Body.Accepted.Should().Be(2);
        _idem.ValueOf(batch.BatchId).Should().Be("done");
        QueuedTotal().Should().Be(2);
    }

    [Fact]
    public async Task I02_done_배치는_202_중복_발행하지_않음()
    {
        var batch = Batch(1, Created("T-1"));
        await _svc.HandleAsync(Body(batch), default);
        var again = await _svc.HandleAsync(Body(batch), default);
        again.StatusCode.Should().Be(202);
        again.Body.Duplicate.Should().BeTrue();
        QueuedTotal().Should().Be(1);
    }

    [Fact]
    public async Task I03_다른_인스턴스_pending_5초_미만은_503()
    {
        var batch = Batch(1, Created("T-1"));
        await _idem.TryBeginAsync(batch.BatchId, default);   // 다른 인스턴스가 발행 중
        var resp = await _svc.HandleAsync(Body(batch), default);
        resp.StatusCode.Should().Be(503);
        resp.RetryAfterSeconds.Should().Be(1);
        QueuedTotal().Should().Be(0);
    }

    [Fact]
    public async Task I04_고아_pending은_재점유해_발행()
    {
        var batch = Batch(1, Created("T-1"));
        await _idem.TryBeginAsync(batch.BatchId, default);
        _time.Advance(TimeSpan.FromSeconds(5));
        var resp = await _svc.HandleAsync(Body(batch), default);
        resp.StatusCode.Should().Be(202);
        _idem.ValueOf(batch.BatchId).Should().Be("done");
    }

    [Fact]
    public async Task I05_task_conflict_전부_확인이면_done()
    {
        var batch = Batch(1, Created("T-1"), Conflict(), Status("R-1", 0, 1, "task_assigned"));
        (await _svc.HandleAsync(Body(batch), default)).StatusCode.Should().Be(202);
        _idem.ValueOf(batch.BatchId).Should().Be("done");
    }

    [Fact]
    public async Task I06_task_실패면_내_pending만_지우고_503()
    {
        var batch = Batch(1, Created("T-1"));
        _broker.InjectPublishFault(PublishFault.Nack, 1, EventCategory.Task);
        var resp = await _svc.HandleAsync(Body(batch), default);
        resp.StatusCode.Should().Be(503);
        _idem.ValueOf(batch.BatchId).Should().BeNull();
        (await _svc.HandleAsync(Body(batch), default)).StatusCode.Should().Be(202);   // 재전송은 1단계부터 통과
    }

    [Fact]
    public async Task I07_멱등_저장소_불통이면_건너뛰고_202()
    {
        var batch = Batch(1, Created("T-1"));
        _idem.FailNext(1);
        var resp = await _svc.HandleAsync(Body(batch), default);
        resp.StatusCode.Should().Be(202);
        QueuedTotal().Should().Be(1);
    }

    [Fact]
    public async Task I08_재점유_뒤_원래_인스턴스의_Abort는_남의_pending을_지우지_않는다()
    {
        var batch = Batch(1, Created("T-1"));
        var original = await _idem.TryBeginAsync(batch.BatchId, default);
        _time.Advance(TimeSpan.FromSeconds(6));
        await _idem.TryBeginAsync(batch.BatchId, default);                     // 다른 인스턴스가 재점유
        await _idem.AbortAsync(batch.BatchId, original.PendingSinceMs, default);
        _idem.ValueOf(batch.BatchId).Should().StartWith("pending:");
    }

    [Fact]
    public async Task I09_telemetry_nack은_202_drop_집계()
    {
        var batch = Batch(1, Created("T-1"), Telemetry("R-1"), Telemetry("R-2"));
        _broker.InjectPublishFault(PublishFault.Nack, 1, EventCategory.Telemetry);
        var resp = await _svc.HandleAsync(Body(batch), default);
        resp.StatusCode.Should().Be(202);
        resp.Body.TelemetryDropped.Should().Be(1);
    }

    [Fact]
    public async Task I10_telemetry_확인_타임아웃도_202()
    {
        var batch = Batch(1, Telemetry("R-1"));
        _broker.InjectPublishFault(PublishFault.ConfirmTimeout, 1, EventCategory.Telemetry);
        var resp = await _svc.HandleAsync(Body(batch), default);
        resp.StatusCode.Should().Be(202);
        resp.Body.TelemetryDropped.Should().Be(1);
    }

    [Fact]
    public async Task I11_task_nack은_503_재전송하면_202()
    {
        var batch = Batch(1, Created("T-1"), Created("T-2"));
        _broker.InjectPublishFault(PublishFault.Nack, 1, EventCategory.Task);
        (await _svc.HandleAsync(Body(batch), default)).StatusCode.Should().Be(503);
        (await _svc.HandleAsync(Body(batch), default)).StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task I12_conflict_확인_타임아웃은_503()
    {
        var batch = Batch(1, Conflict());
        _broker.InjectPublishFault(PublishFault.ConfirmTimeout, 1, EventCategory.Conflict);
        (await _svc.HandleAsync(Body(batch), default)).StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task I13_채널_끊김은_503_재전송시_앞부분은_같은_eventId_중복()
    {
        var batch = Batch(1, Created("T-1"), Created("T-1"), Created("T-1"));
        _broker.InjectPublishFault(PublishFault.ConfirmTimeout, 1, EventCategory.Task);   // 1번째: 들어갔지만 확인 못 받음
        _broker.InjectPublishFault(PublishFault.ChannelClosed, 1, EventCategory.Task);    // 2번째부터 끊김
        (await _svc.HandleAsync(Body(batch), default)).StatusCode.Should().Be(503);
        (await _svc.HandleAsync(Body(batch), default)).StatusCode.Should().Be(202);

        var queue = _broker.QueueFor(EventCategory.Task, "T-1");
        _broker.Messages(queue).Select(m => m.MessageId).Should().Equal(
            $"{batch.BatchId}/0", $"{batch.BatchId}/0", $"{batch.BatchId}/1", $"{batch.BatchId}/2");
    }

    [Fact]
    public async Task I14_큐_가득_reject_publish는_503()
    {
        var batch = Batch(1, Created("T-1"));
        _broker.SetMaxLengthBytes(_broker.QueueFor(EventCategory.Task, "T-1"), 10);
        (await _svc.HandleAsync(Body(batch), default)).StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task I15_큐_순서는_배열_순서()
    {
        var batch = Batch(1, Status("R-1", 0, 1, "task_assigned"), Telemetry("R-1"), Status("R-1", 1, 2, "conflict_wait"));
        await _svc.HandleAsync(Body(batch), default);
        var queue = _broker.QueueFor(EventCategory.Task, "R-1");
        _broker.Messages(queue).Select(m => m.MessageId).Should().Equal($"{batch.BatchId}/0", $"{batch.BatchId}/2");
        var envelope = FleetJson.Deserialize<EventEnvelope>(_broker.Messages(queue)[0].Body.Span);
        envelope.TickMs.Should().Be(100);   // 배치 봉투의 tickMs를 항목으로 복사
    }

    public static TheoryData<string, Func<EventBatch, byte[]>> Invalids => new()
    {
        { "schemaVersion", b => Body(b with { SchemaVersion = 2 }) },
        { "UUIDv7", b => Body(b with { BatchId = Guid.NewGuid().ToString() }) },
        { "1~1000", b => Body(b with { Events = [] }) },
        { "1~1000", b => Body(b with { Events = Enumerable.Repeat(Telemetry("R-1"), 1001).ToList() }) },
        { "eventType", b => Body(b with { Events = [new EventItem("Teleport", JsonDocument.Parse("{}").RootElement)] }) },
        { "robotId", b => Body(b with { Events = [new EventItem(EventType.RobotTelemetry, JsonDocument.Parse("""{"x":1}""").RootElement)] }) },
        { "tickMs", b => Body(b with { TickMs = 0 }) },
        { "JSON", _ => "{not json"u8.ToArray() },
    };

    [Theory]
    [MemberData(nameof(Invalids))]
    public async Task I16_검증_실패는_400(string reason, Func<EventBatch, byte[]> body)
    {
        var resp = await _svc.HandleAsync(body(Batch(1, Telemetry("R-1"))), default);
        resp.StatusCode.Should().Be(400);
        resp.Body.Error.Should().Contain(reason);
        QueuedTotal().Should().Be(0);
    }
}
