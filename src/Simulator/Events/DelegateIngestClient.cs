using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Simulator.Events;

/// in-process 모드: 같은 프로세스의 IngestService를 직접 부른다(PipelineHost가 delegate를 넘김)
public sealed class DelegateIngestClient : IIngestClient
{
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task<IngestResult>> _send;

    public DelegateIngestClient(Func<ReadOnlyMemory<byte>, CancellationToken, Task<IngestResult>> send)
    {
        _send = send;
    }

    public Task<IngestResult> SendAsync(EventBatch batch, ReadOnlyMemory<byte> body, CancellationToken ct) => _send(body, ct);
}
