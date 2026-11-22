using System.Globalization;
using FluentAssertions;
using RobotFleet.Agent.Llm.Prompts;
using Xunit;

namespace RobotFleet.Agent.Llm.Tests;

public class CatalogTableRendererTests
{
    private static string LoadGolden()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "catalog-table.txt"))
            .Replace("\r\n", "\n");
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    // LR-01
    [Fact]
    public void CatalogTable_Golden()
    {
        var golden = LoadGolden();

        var actual = CatalogTableRenderer.Render();
        actual.Should().Be(golden);
        actual.Should().NotContain("\r");

        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CatalogTableRenderer.Render().Should().Be(golden);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
