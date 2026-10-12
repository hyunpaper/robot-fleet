using System.Text.Json;
using RobotFleet.Shared.Messaging;

namespace RobotFleet.Simulator.Events;

/// {directory}/{mapId}-{runId}.jsonl, 이벤트당 1행 {eventId,batchId,tick,eventType,category,key}. 202를 받은 배치만. 10 §5
public sealed class SendLog : IDisposable
{
    private readonly FileStream _file;
    private readonly object _gate = new();

    public SendLog(string directory, string mapId, string runId)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{mapId}-{runId}.jsonl");
        _file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public string Path { get; }

    /// 202를 받은 배치를 append + flush
    public void Append(EventBatch batch)
    {
        var buffer = new MemoryStream();
        for (var i = 0; i < batch.Events.Count; i++)
        {
            var e = batch.Events[i];
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("eventId", $"{batch.BatchId}/{i}");
                w.WriteString("batchId", batch.BatchId);
                w.WriteNumber("tick", batch.Tick);
                w.WriteString("eventType", e.EventType);
                w.WriteString("category", EventCategories.Of(e.EventType).ToString().ToLowerInvariant());
                w.WriteString("key", RoutingKeys.Of(e.EventType, e.Payload, batch.MapId));
                w.WriteEndObject();
            }

            buffer.Write("\n"u8);
        }

        lock (_gate)
        {
            buffer.Position = 0;
            buffer.CopyTo(_file);
            _file.Flush(flushToDisk: false);
        }
    }

    public void Dispose()
    {
        lock (_gate) _file.Dispose();
    }
}
