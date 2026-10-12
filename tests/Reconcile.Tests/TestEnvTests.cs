using FluentAssertions;
using RobotFleet.TestSupport;

namespace RobotFleet.Reconcile.Tests;

// K-04
public sealed class TestEnvTests : IDisposable
{
    private const string Name = "FLEET_TEST_PG";
    private readonly string dir = Directory.CreateTempSubdirectory("testenv").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static Func<string, string?> Env(string? value) => _ => value;

    private void Write(string file, string value) =>
        File.WriteAllText(Path.Combine(dir, file), $$"""{ "{{Name}}": "{{value}}" }""");

    [Fact]
    public void 환경_변수가_Local_파일을_이긴다()
    {
        Write("appsettings.Test.json", "base");
        Write("appsettings.Test.Local.json", "local");

        TestEnv.Resolve(Name, Env("env"), dir).Should().Be("env");
    }

    [Fact]
    public void Local이_Test_json을_이긴다()
    {
        Write("appsettings.Test.json", "base");
        Write("appsettings.Test.Local.json", "local");

        TestEnv.Resolve(Name, Env(null), dir).Should().Be("local");
    }

    [Fact]
    public void Local_파일이_없으면_Test_json()
    {
        Write("appsettings.Test.json", "base");

        TestEnv.Resolve(Name, Env(null), dir).Should().Be("base");
    }

    [Fact]
    public void 빈_문자열과_공백은_null()
    {
        Write("appsettings.Test.json", "");
        TestEnv.Resolve(Name, Env(""), dir).Should().BeNull();
        TestEnv.Resolve(Name, Env("  "), dir).Should().BeNull();
    }

    [Fact]
    public void 폴더가_없으면_환경_변수만()
    {
        TestEnv.Resolve(Name, Env("env"), null).Should().Be("env");
        TestEnv.Resolve(Name, Env(null), null).Should().BeNull();
        TestEnv.Resolve(Name, Env(null), Path.Combine(dir, "없음")).Should().BeNull();
    }

    [Fact]
    public void 파일이_하나도_없는_폴더는_null()
    {
        TestEnv.Resolve(Name, Env(null), dir).Should().BeNull();
    }
}
