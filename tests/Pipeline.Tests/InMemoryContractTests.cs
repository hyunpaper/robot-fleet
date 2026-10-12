using RobotFleet.Infrastructure.InMemory;
using RobotFleet.Shared.Pipeline;
using RobotFleet.TestSupport.Contracts;

namespace RobotFleet.Pipeline.Tests;

// 계약 기반 클래스의 L1(InMemory) 실행. 같은 메서드를 Infrastructure.Tests가 실제 서버로 돌린다 (10 §12)

public sealed class InMemoryIdemStoreContractTests : IdemStoreContract
{
    protected override Task<IIdemStore> CreateAsync() => Task.FromResult<IIdemStore>(new InMemoryIdemStore(Time));

    [Fact] public Task I01_신규_키는_BEGIN() => New_key_begins();
    [Fact] public Task I02_done_키는_DONE() => Completed_key_is_done();
    [Fact] public Task I03_5초_미만_pending은_BUSY() => Pending_under_5s_is_busy();
    [Fact] public Task I04_5초_이상_pending은_TAKEOVER() => Pending_over_5s_is_taken_over();
    [Fact] public Task I08_Abort는_내_pending만_지운다() => Abort_deletes_only_own_pending();
    [Fact] public Task Complete는_키가_있을_때만() => Complete_without_key_does_nothing();
}

public sealed class InMemoryRosterContractTests : RosterContract
{
    protected override Task<IPartitionRoster> CreateAsync() => Task.FromResult<IPartitionRoster>(new InMemoryPartitionRoster(Time));

    [Fact] public Task R02_순위는_id_순() => Members_are_sorted_by_id_not_heartbeat();
    [Fact] public Task R06_30초_침묵은_제외() => Silent_member_expires_after_30s();
    [Fact] public Task 탈퇴() => Leave_removes_member();
}

public sealed class InMemoryEventStoreContractTests : EventStoreContract
{
    private readonly InMemoryEventStore _store = new();

    protected override Task<IEventStore> CreateAsync() => Task.FromResult<IEventStore>(_store);

    protected override Task<(long MoveMs, long WaitMs, long TaskCount)> ReadMetricsAsync(string robotId, DateTimeOffset minute)
    {
        var m = _store.Metrics(Map, robotId, minute);
        return Task.FromResult((m.MoveMs, m.WaitMs, m.TaskCount));
    }

    protected override Task<(short Status, string? RobotId)?> ReadTaskAsync(string taskId) =>
        Task.FromResult(_store.TaskHistory(taskId) is { } h ? ((short, string?)?)(h.Status, h.RobotId) : null);

    [Fact] public Task W10_telemetry_중복은_집계_1회() => Duplicate_telemetry_is_counted_once_in_metrics();
    [Fact] public Task W11_TaskCompleted_중복은_task_count_1회() => Duplicate_task_completed_counts_once();
    [Fact] public Task W12_task_history_단조() => Task_history_is_monotonic();
    [Fact] public Task 위반_영수증과_상태() => Status_receipts_keep_invalid_and_state_follows_valid_only();
    [Fact] public Task conflict_중복() => Duplicate_conflict_is_ignored();
}

public sealed class InMemoryBrokerContractTests : BrokerContract
{
    protected override Task<(IEventPublisher Publisher, IPartitionSubscriber Subscriber, string Prefix)> CreateAsync()
    {
        var broker = new InMemoryBroker();
        return Task.FromResult<(IEventPublisher, IPartitionSubscriber, string)>((broker, broker, ""));
    }

    [Fact] public Task C04_같은_키는_한_큐에_순서대로() => Same_key_lands_in_one_queue_in_order();
    [Fact] public Task C04_키가_많으면_파티션에_퍼진다() => Many_keys_spread_over_partitions();
    [Fact] public Task C04_prefetch와_multiple_ack() => Prefetch_limits_unacked_and_multiple_ack_releases();
    [Fact] public Task C04_SAC_채널_닫기_반환() => Single_active_consumer_hands_over_unacked_on_close();
    [Fact] public Task C04_reject는_DLQ() => Reject_goes_to_dlq();
    [Fact] public Task C04_invalid_큐_발행() => Publish_to_invalid_queue_is_confirmed();
}
