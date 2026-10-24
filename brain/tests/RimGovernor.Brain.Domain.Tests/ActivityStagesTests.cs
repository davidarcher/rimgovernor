using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using RimGovernor.Brain.Analyzers;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ActivityStagesTests
{
    [Theory]
    [InlineData("Generation", "P.CompareActivityPlans();", true)]
    [InlineData("Binding", "P.CompareActivityPlans();", true)]
    [InlineData("Binding", "System.Action a = P.CompareActivityPlans;", true)]
    [InlineData("Generation", "P.CompareActivityOutcomes();", true)]
    [InlineData("Generation", "RimGovernor.Brain.Domain.PawnActivities a = new();", true)]
    [InlineData("Binding", "RimGovernor.Brain.Domain.PawnActivities a = new();", false)]
    [InlineData("Generation", "Helper.Choose();", true)]
    [InlineData("Binding", "RimGovernor.Brain.Policy.Methods.Evaluation.Selection.Compare();", true)]
    public async Task StageDependenciesAreCheckedSemantically(string stage, string body, bool rejected)
    {
        var source = $$"""
            using P = RimGovernor.Brain.Policy.PolicyFunctions;
            using RimGovernor.Brain.Policy;
            namespace RimGovernor.Brain.Policy { public static class PolicyFunctions { public static void CompareActivityPlans() {} public static void CompareActivityOutcomes() {} }
                public static class Helper { public static void Choose() {} } }
            namespace RimGovernor.Brain.Domain { public class PawnActivities {} }
            namespace RimGovernor.Brain.Policy.Methods.Evaluation { internal class Selection { internal static void Compare() {} } }
            namespace RimGovernor.Brain.Policy.Methods.{{stage}} { internal class Stage { void Run() { {{body}} } } }
            """;
        await Check(source, rejected);
    }

    [Theory]
    [InlineData("CompareActivityPlans();")]
    [InlineData("int Rank() => 4 / 2; Rank();")]
    [InlineData("RimGovernor.Brain.Domain.PawnActivities a = new();")]
    [InlineData("ForecastActivityOutcomeComparison a = new();")]
    [InlineData("ForecastActivityOutcomeComparison a = null!; var b = a with { }; ")]
    public async Task LifecycleCannotReintroduceLocalRankingOrDirectOrders(string body)
    {
        await Check($$"""
            namespace RimGovernor.Brain.Domain { public class PawnActivities {} }
            namespace RimGovernor.Brain.Policy { public record ForecastActivityOutcomeComparison; public static class ForecastCoupledSelection {
                public static void CompareActivityPlans() {} public static void CompareActivityOutcomes() {}
                public static void StartCoupledActivity() { {{body}} }
            } }
            """, true);
    }

    [Theory]
    [InlineData("StartCoupledActivity", "ForecastCoupledSelection")]
    [InlineData("ContinueCoordinatedActivity", "CoordinatedActivitiesRules")]
    public async Task CoordinatedEntrypointsCannotConstructActionsOrLocalScores(string entry, string type)
    {
        await Check($$"""
            namespace RimGovernor.Brain.Domain { public class PawnActivities {} }
            namespace RimGovernor.Brain.Policy { public static class {{type}} {
                public static void {{entry}}() { RimGovernor.Brain.Domain.PawnActivities a = new(); int score = 4 / 2; }
            } }
            """, true);
    }

    [Fact]
    public async Task LifecycleCanUseTheSharedOutcomeComparator()
    {
        await Check("""
            namespace RimGovernor.Brain.Policy { public static class ForecastCoupledSelection {
                public static void CompareActivityOutcomes() {}
                public static void StartCoupledActivity() { CompareActivityOutcomes(); }
            } }
            """, false);
    }

    private static async Task Check(string source, bool rejected)
    {
        var compilation = CSharpCompilation.Create("StageProbe", [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new ActivityStagesAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(rejected, diagnostics.Any(d => d.Id == "RGARCH010"));
    }
}
