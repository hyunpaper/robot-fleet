using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.InMemory;

public sealed class FakeIngestClient : IIngestClient
{
    private readonly object gate = new();
    private readonly Queue<IngestResult> script = new();
    private readonly List<(EventBatch Batch, byte[] Body)> sent = [];

    /// 응답 지연(틱 루프 비차단 검증용)
    public TimeSpan Delay { get; set; }

    public IReadOnlyList<(EventBatch Batch, byte[] Body)> Sent
    {
        get
        {
            lock (gate)
            {
                return sent.ToArray();
            }
        }
    }

    /// 순서대로 반환, 소진 후 Accepted
    public void Script(params IngestResult[] results)
    {
        lock (gate)
        {
            foreach (var result in results)
            {
                script.Enqueue(result);
            }
        }
    }

    public async Task<IngestResult> SendAsync(EventBatch batch, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        TimeSpan delay;
        lock (gate)
        {
            sent.Add((batch, body.ToArray()));
            delay = Delay;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct);
        }

        lock (gate)
        {
            return script.Count > 0 ? script.Dequeue() : new IngestResult(IngestStatus.Accepted);
        }
    }
}
