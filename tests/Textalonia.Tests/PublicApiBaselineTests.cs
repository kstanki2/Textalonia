using Textalonia.Baselines;
using Xunit;

namespace Textalonia.Tests;

public class PublicApiBaselineTests
{
    [Fact]
    public void Public_and_protected_surface_matches_preview_baseline()
    {
        var actual = PublicApi.Capture();
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "public-api.txt")).Replace("\r\n", "\n");
        if (expected != actual)
        {
            var output = Environment.GetEnvironmentVariable("TEXTALONIA_FAILURE_DIR") ?? Path.Combine("artifacts", "fixture-failures");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "public-api.actual.txt"), actual);
        }
        Assert.Equal(expected, actual);
    }
}
