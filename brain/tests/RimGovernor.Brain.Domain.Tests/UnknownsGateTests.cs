using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RimGovernor.Brain.Analyzers;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Rule 9 (RGARCH009): the counted unknown ban and its per-file baseline.</summary>
public sealed class UnknownsGateTests
{
    /// <summary>Domain's Fact, compiled from outside the probed project so it has no baseline key of its own.</summary>
    private const string FactSource = """
        namespace RimGovernor.Brain.Domain {
            public readonly record struct Fact<T> { public bool IsKnown { get; } public T? ValueOrDefault => default; }
            public static class Fact { public static Fact<T> Known<T>(T value) => default; public static Fact<T> Unknown<T>() => default; }
            public enum Shape { Round, Square }
            public abstract record Need { public sealed record Met : Need; public sealed record Unmet : Need; }
        }
        """;

    [Theory]
    [InlineData("Fact", "Fact<int> F { get; } = default; bool K => F.IsKnown;", 2)]
    [InlineData("Unknown", "Fact<int> F => Fact.Unknown<int>();", 1)]
    [InlineData("ValueOrDefault", "int V(Fact<int> f) => f.ValueOrDefault;", 1)]
    [InlineData("NullableValue", "public int? Count { get; init; }", 1)]
    [InlineData("Discard", "int S(Shape s) => s switch { Shape.Round => 1, _ => 2 };", 1)]
    [InlineData("Discard", "int S(Need n) { switch (n) { case Need.Met: return 1; default: return 2; } }", 1)]
    public async Task EachTokenIsCountedPerFile(string token, string member, int count)
    {
        var messages = await Run("", Probe(member));
        Assert.Contains(messages, m => m.Contains($"Policy/Probe.cs|{token}|{count} has no baseline line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnumMembersNamedUnknownOrUnspecifiedAreCounted()
    {
        var messages = await Run("", ("Probe.cs", "namespace RimGovernor.Brain.Policy { public enum E { Unspecified, Unknown, Ready } }"));
        Assert.Contains(messages, m => m.Contains("Policy/Probe.cs|UnknownMember|2 has no baseline line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NullableValuesAreCountedOnlyInRecordProjects()
    {
        var messages = await Run("", "RimGovernor.Brain.Store", Probe("public int? Count { get; init; }", "Store"));
        Assert.Empty(messages);
    }

    [Fact]
    public async Task ACountAtItsLineIsClean()
    {
        Assert.Empty(await Run("Policy/Probe.cs|Fact|1\nPolicy/Probe.cs|Unknown|1", Probe("Fact<int> F => Fact.Unknown<int>();")));
    }

    [Fact]
    public async Task ARisingCountFails()
    {
        var messages = await Run("Policy/Probe.cs|Unknown|1", Probe("Fact<int> F => Fact.Unknown<int>(); Fact<int> G => Fact.Unknown<int>();"));
        Assert.Contains(messages, m => m.Contains("Policy/Probe.cs|Unknown rose from 1 to 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANewFileFails()
    {
        var messages = await Run(
            "Policy/Probe.cs|Unknown|1",
            Probe("Fact<int> F => Fact.Unknown<int>();"),
            ("Other.cs", "namespace RimGovernor.Brain.Policy { using RimGovernor.Brain.Domain; class Other { Fact<int> F => Fact.Unknown<int>(); } }"));
        Assert.Contains(messages, m => m.Contains("Policy/Other.cs|Unknown|1 has no baseline line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFallingCountAsksForTheLineToBeLowered()
    {
        var messages = await Run("Policy/Probe.cs|Unknown|3", Probe("Fact<int> F => Fact.Unknown<int>(); Fact<int> G => Fact.Unknown<int>();"));
        Assert.Contains(messages, m => m.Contains("lower the line in brain/analyzers/baseline/rule9.txt to Policy/Probe.cs|Unknown|2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALineWhoseHitsAreGoneMustBeDeleted()
    {
        var messages = await Run("Policy/Probe.cs|Unknown|1", Probe("int X => 1;"));
        Assert.Contains(messages, m => m.Contains("no longer occurs, delete it from brain/analyzers/baseline/rule9.txt: Policy/Probe.cs|Unknown|1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("int S(System.DayOfWeek d) => d switch { System.DayOfWeek.Monday => 1, _ => 2 };")]
    [InlineData("int S(string s) => s switch { \"a\" => 1, _ => 2 };")]
    [InlineData("int S(string s) { switch (s) { case \"a\": return 1; default: return 2; } }")]
    [InlineData("int S(Shape s) => s switch { Shape.Round => 1, _ when s > Shape.Round => 2, Shape.Square => 3 };")]
    public async Task SwitchesOverForeignTypesAndGuardedArmsAreNotCounted(string member)
    {
        Assert.Empty(await Run("", Probe(member)));
    }

    private static (string File, string Source) Probe(string member, string project = "Policy") =>
        ("Probe.cs", $$"""
            namespace RimGovernor.Brain.{{project}} { using RimGovernor.Brain.Domain;
                public record Probe { {{member}} }
            }
            """);

    private static Task<List<string>> Run(string baseline, params (string File, string Source)[] files) =>
        Run(baseline, "RimGovernor.Brain.Policy", files);

    private static async Task<List<string>> Run(string baseline, string assembly, params (string File, string Source)[] files)
    {
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Source, options, $"C:/repo/brain/src/{assembly}/{f.File}"))
            .Append(CSharpSyntaxTree.ParseText(FactSource, options, "C:/repo/elsewhere/Fact.cs"));
        var compilation = CSharpCompilation.Create(assembly, trees,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var additional = ImmutableArray.Create<AdditionalText>(new Baseline(baseline));
        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new UnknownsAnalyzer()), new AnalyzerOptions(additional))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        Assert.All(diagnostics, d => Assert.Equal("RGARCH009", d.Id));

        // The baseline cap is checked in Domain only, so the probe never sees it.
        return diagnostics.Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }

    private sealed class Baseline(string text) : AdditionalText
    {
        public override string Path => "C:/repo/brain/analyzers/baseline/rule9.txt";

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
}
