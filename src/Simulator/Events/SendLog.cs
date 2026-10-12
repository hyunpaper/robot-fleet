using RobotFleet.Shared.Messaging;

namespace RobotFleet.Simulator.Events;

/// {directory}/{mapId}-{runId}.jsonl, 이벤트당 1행 {eventId,batchId,tick,eventType,category,key}. 10 §5
public sealed class SendLog : IDisposable
{
    public SendLog(string directory, string mapId, string runId)
    {
        Path = System.IO.Path.Combine(directory, $"{mapId}-{runId}.jsonl");
    }

    public string Path { get; }

    /// 202를 받은 배치를 append + flush
    public void Append(EventBatch batch) => throw new NotImplementedException();

    public void Dispose()
    {
    }
}
