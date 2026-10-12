using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.InMemory;

public sealed class InMemoryEventSink(int capacity = 600) : IEventSink
{
    private readonly object gate = new();
    private readonly List<EventBatch> batches = [];
    private TaskCompletionSource changed = NewSignal();
    private bool closed;

    public IReadOnlyList<EventBatch> Batches
    {
        get
        {
            lock (gate)
            {
                return batches.ToArray();
            }
        }
    }

    public bool TryWrite(EventBatch batch)
    {
        lock (gate)
        {
            if (closed || batches.Count >= capacity)
            {
                return false;
            }

            batches.Add(batch);
            return true;
        }
    }

    public async ValueTask<bool> WaitToWriteAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (closed)
                {
                    return false;
                }

                if (batches.Count < capacity)
                {
                    return true;
                }

                wait = changed.Task;
            }

            await wait.WaitAsync(ct);
        }
    }

    /// 쌓인 배치를 비워 공간을 만든다(송신 완료 흉내)
    public void Drain()
    {
        lock (gate)
        {
            batches.Clear();
            Signal();
        }
    }

    /// 이후 WaitToWriteAsync는 false
    public void Close()
    {
        lock (gate)
        {
            closed = true;
            Signal();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Signal()
    {
        changed.TrySetResult();
        changed = NewSignal();
    }
}
