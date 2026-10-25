using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using RimGovernor.Brain.Analyzers;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LayerGateTests
{
    [Theory]
    [InlineData("Executor", "Store", false)]
    [InlineData("PlayerApi", "Runtime", false)]
    [InlineData("Store", "GameAccess", true)]
    [InlineData("Store", "Bridge", true)]
    [InlineData("Store", "HttpChannel", true)]
    [InlineData("Policy", "Runtime", true)]
    [InlineData("Executor", "Runtime", true)]
    [InlineData("Runtime", "PlayerApi", true)]
    [InlineData("Store", "Policy", false)]
    public async Task JournalAndReportConsumersRespectRemainingBoundaries(string consumer, string dependency, bool rejected)
    {
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var core = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var referenced = CSharpCompilation.Create("RimGovernor.Brain." + dependency,
            [CSharpSyntaxTree.ParseText("public sealed class Dependency {}", cancellationToken: TestContext.Current.CancellationToken)], [core], options);
        using var bytes = new MemoryStream();
        var emitted = referenced.Emit(bytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var compilation = CSharpCompilation.Create("RimGovernor.Brain." + consumer,
            [CSharpSyntaxTree.ParseText("public sealed class Consumer { public Dependency Value = new(); }", cancellationToken: TestContext.Current.CancellationToken)],
            [core, MetadataReference.CreateFromImage(bytes.ToArray())], options);
        Assert.DoesNotContain(compilation.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Severity == DiagnosticSeverity.Error);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new LayersAnalyzer()),
                new CompilationWithAnalyzersOptions(new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
                    onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(rejected, diagnostics.Any(d => d.Id == "RGARCH001"));
        if (rejected)
            Assert.Contains(diagnostics, d => d.GetMessage().Contains(consumer + "->" + dependency, StringComparison.Ordinal));
    }
}
