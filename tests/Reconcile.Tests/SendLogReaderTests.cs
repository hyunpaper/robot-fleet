using FluentAssertions;

namespace RobotFleet.Reconcile.Tests;

// K-01
public sealed class SendLogReaderTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("sendlog").FullName;

    private const string Row1 = """{"eventId":"b1/0","batchId":"b1","tick":5,"eventType":"TaskCreated","category":"task","key":"m1"}""";
    private const string Row2 = """{"eventId":"b1/1","batchId":"b1","tick":5,"eventType":"RobotTelemetry","category":"telemetry","key":"r1"}""";
    private const string Row3 = """{"eventId":"b2/0","batchId":"b2","tick":6,"eventType":"ConflictDetected","category":"conflict","key":"m1"}""";

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void 세_줄_파일은_세_개로_읽고_빈_줄은_무시한다()
    {
        var path = Path.Combine(dir, "factory-a-20261013-143022-r200.jsonl");
        File.WriteAllLines(path, [Row1, string.Empty, Row2, "  ", Row3]);

        var events = SendLogReader.Read(path).ToList();

        events.Should().HaveCount(3);
        events[0].Should().Be(new SentEvent("b1/0", "b1", 5, "TaskCreated", "task", "m1", "20261013-143022-r200"));
        events[1].Key.Should().Be("r1");
        events[2].Category.Should().Be("conflict");
    }

    [Fact]
    public void 줄의_runId가_파일명보다_우선이고_둘_다_없으면_예외()
    {
        var withRun = Path.Combine(dir, "a.jsonl");
        File.WriteAllLines(withRun, [Row1.Replace("\"batchId\"", "\"runId\":\"20261013-150000-r50\",\"batchId\"")]);
        SendLogReader.Read(withRun).Single().RunId.Should().Be("20261013-150000-r50");

        var none = Path.Combine(dir, "b.jsonl");
        File.WriteAllLines(none, [Row1]);
        var act = () => SendLogReader.Read(none).ToList();
        act.Should().Throw<InvalidDataException>().WithMessage("*runId*");
    }

    [Theory]
    [InlineData("factory-a-20261013-143022-r200.jsonl", "20261013-143022-r200")]
    [InlineData("/x/m-1-20261013-000000-r1.jsonl", "20261013-000000-r1")]
    [InlineData("a.jsonl", null)]
    [InlineData("factory-a-20261013143022.jsonl", null)]
    public void 파일명에서_runId를_읽는다(string file, string? expected) =>
        SendLogReader.RunIdOfFileName(file).Should().Be(expected);

    [Fact]
    public void 디렉터리면_jsonl_전부를_읽는다()
    {
        File.WriteAllLines(Path.Combine(dir, "a-20261013-143022-r200.jsonl"), [Row1]);
        File.WriteAllLines(Path.Combine(dir, "b-20261013-143022-r200.jsonl"), [Row2, Row3]);
        File.WriteAllText(Path.Combine(dir, "c.txt"), "무시");

        SendLogReader.Read(dir).Should().HaveCount(3);
    }

    [Fact]
    public void 깨진_줄은_줄_번호를_담은_예외()
    {
        var path = Path.Combine(dir, "bad-20261013-143022-r200.jsonl");
        File.WriteAllLines(path, [Row1, "{깨짐", Row3]);

        var act = () => SendLogReader.Read(path).ToList();

        act.Should().Throw<InvalidDataException>().WithMessage("*2번째 줄*");
    }
}
