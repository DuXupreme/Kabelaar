using System.Diagnostics;
using System.Reflection;
using Kabelaar.Core;
using Xunit;
using Xunit.Abstractions;

namespace Kabelaar.Core.Tests;

public sealed class ValidationPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void MeasureActualAcceptanceFixtureValidationAndReports()
    {
        var project = DemoFactory.CreateAcceptancePerformance();
        Assert.Empty(ProductionValidator.Validate(project));
        _ = HarnessReports.Wires(project); _ = HarnessReports.Bom(project); _ = HarnessReports.Pins(project);
        var validation = new List<double>();
        var reports = new List<double>();
        var shapes = new List<double>();
        var shapeMethod = typeof(ProductionValidator).GetMethod("ValidateShape", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (var i = 0; i < 9; i++)
        {
            var watch = Stopwatch.StartNew();
            var issues = ProductionValidator.Validate(project);
            watch.Stop();
            Assert.Empty(issues);
            validation.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
            _ = HarnessReports.Wires(project); _ = HarnessReports.Bom(project); _ = HarnessReports.Pins(project);
            watch.Stop(); reports.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
            shapeMethod.Invoke(null, [project, new List<ValidationIssue>()]);
            watch.Stop(); shapes.Add(watch.Elapsed.TotalMilliseconds);
        }
        validation.Sort(); reports.Sort(); shapes.Sort();
        output.WriteLine($"100 connectors / 1,000 wires: validator median {validation[4]:F2} ms (shape traversal {shapes[4]:F2} ms); all three reports median {reports[4]:F2} ms.");
    }
}
