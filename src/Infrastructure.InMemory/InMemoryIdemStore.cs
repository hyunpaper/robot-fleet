using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.InMemory;

/// 08 §9 상태 전이 표 그대로. orphan 5000ms, 키 만료 600s
public sealed class InMemoryIdemStore(TimeProvider time) : IIdemStore
{
    private const long OrphanMs = 5000;
    private const long TtlMs = 600_000;

    private readonly object gate = new();
    private readonly Dictionary<string, (string Value, long ExpiresMs)> entries = [];
    private int failures;

    /// 다음 count회 TryBegin = Bypass, Complete/Abort = 예외 무시(아무 일도 안 함)
    public void FailNext(int count)
    {
        lock (gate)
        {
            failures = count;
        }
    }

    /// "pending:{ms}" | "done" | null — 테스트 관찰용
    public string? ValueOf(string batchId)
    {
        lock (gate)
        {
            return Read(batchId, NowMs());
        }
    }

    public Task<IdemBegin> TryBeginAsync(string batchId, CancellationToken ct)
    {
        lock (gate)
        {
            if (TakeFailure())
            {
                return Task.FromResult(new IdemBegin(IdemBeginStatus.Bypass, 0));
            }

            var now = NowMs();
            var value = Read(batchId, now);
            if (value is null)
            {
                Write(batchId, $"pending:{now}", now);
                return Task.FromResult(new IdemBegin(IdemBeginStatus.Begin, now));
            }

            if (value == "done")
            {
                return Task.FromResult(new IdemBegin(IdemBeginStatus.Done, 0));
            }

            var since = long.Parse(value["pending:".Length..]);
            if (now - since >= OrphanMs)
            {
                Write(batchId, $"pending:{now}", now);
                return Task.FromResult(new IdemBegin(IdemBeginStatus.Takeover, now));
            }

            return Task.FromResult(new IdemBegin(IdemBeginStatus.Busy, since));
        }
    }

    /// SET done XX: 키가 있을 때만
    public Task CompleteAsync(string batchId, CancellationToken ct)
    {
        lock (gate)
        {
            var now = NowMs();
            if (!TakeFailure() && Read(batchId, now) is not null)
            {
                Write(batchId, "done", now);
            }
        }

        return Task.CompletedTask;
    }

    public Task AbortAsync(string batchId, long pendingSinceMs, CancellationToken ct)
    {
        lock (gate)
        {
            if (!TakeFailure() && Read(batchId, NowMs()) == $"pending:{pendingSinceMs}")
            {
                entries.Remove(batchId);
            }
        }

        return Task.CompletedTask;
    }

    private long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();

    private bool TakeFailure()
    {
        if (failures <= 0)
        {
            return false;
        }

        failures--;
        return true;
    }

    private string? Read(string batchId, long now)
    {
        if (!entries.TryGetValue(batchId, out var entry))
        {
            return null;
        }

        if (entry.ExpiresMs <= now)
        {
            entries.Remove(batchId);
            return null;
        }

        return entry.Value;
    }

    private void Write(string batchId, string value, long now) => entries[batchId] = (value, now + TtlMs);
}
