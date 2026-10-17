using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RimGovernor.Tools.Workflow.Commands;

namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>
/// The acceptance cases' banned-API rule (#3397): the cases' own <c>BannedSymbols.txt</c> under the analyzer they run
/// flags a wall-clock wait and a by-key reply read, and the gate fails a use a suppression kept from the build.
/// </summary>
public sealed class BannedApiTests : IDisposable
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "BannedApi");

    private readonly string temp = Directory.CreateTempSubdirectory("banned-api-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    [Fact]
    public async Task FlagsAWallClockWaitAndAReplyReadByKey()
    {
        var source = """
            using System.Text.Json.Nodes;
            using System.Threading;
            using System.Threading.Tasks;

            public static class Poll
            {
                public static async Task<int> Shown(JsonNode view, CancellationToken ct)
                {
                    await Task.Delay(500, ct);
                    Thread.Sleep(10);
                    var request = new JsonObject { ["identity"] = 1 };
                    return (int)view["shown"]! + (int)view[0]! + request.Count;
                }
            }
            """;
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create(
            "RimGovernor.Brain.Acceptance.Cases",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), cancellationToken: TestContext.Current.CancellationToken)],
            platform.Select(r => MetadataReference.CreateFromFile(r)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var loader = new Loader();
        var analyzers = Directory.GetFiles(Dir, "*.dll")
            .SelectMany(f => new AnalyzerFileReference(f, loader).GetAnalyzers(LanguageNames.CSharp))
            .ToImmutableArray();
        var banned = new BannedSymbols(Path.Combine(Dir, "BannedSymbols.txt"));

        var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions([banned]), null, concurrentAnalysis: false, logAnalyzerExecutionTime: false);
        var found = await compilation.WithAnalyzers(analyzers, options)
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Task.Delay(int, CancellationToken)", "Thread.Sleep(int)", "JsonNode.this[string]", "JsonNode.this[string]", "JsonNode.this[int]"],
            found.Where(d => d.Id == "RS0030").OrderBy(d => d.Location.SourceSpan.Start).Select(d => d.GetMessage().Split('\'')[1]));
    }

    [Fact]
    public void FailsAUseASuppressionKeptFromTheBuild()
    {
        var cases = Path.Combine(temp, "Cases");
        Directory.CreateDirectory(cases);
        var sarif = Path.Combine(temp, "banned-api.sarif");
        File.WriteAllText(sarif, Log(cases, ("RS0030", "Food/Pragma.cs"), ("CA1822", "Food/Ported.cs")));

        // Ported.cs only has another rule's finding; Pragma.cs uses a banned API a suppression kept from failing the build.
        var problem = Assert.Single(BannedApiUses.Check(cases, sarif));
        Assert.StartsWith("1 files use a banned API under a suppression", problem, StringComparison.Ordinal);
        Assert.EndsWith("\n  Food/Pragma.cs", problem, StringComparison.Ordinal);

        File.WriteAllText(sarif, Log(cases, ("CA1822", "Food/Ported.cs")));
        Assert.Empty(BannedApiUses.Check(cases, sarif));
    }

    private static string Log(string cases, params (string Rule, string File)[] results) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            runs = new[]
            {
                new
                {
                    results = results.Select(r => new
                    {
                        ruleId = r.Rule,
                        level = "note",
                        locations = new[] { new { physicalLocation = new { artifactLocation = new { uri = new Uri(Path.Combine(cases, r.File)).AbsoluteUri } } } },
                    }),
                },
            },
        });

    private sealed class BannedSymbols(string path) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(File.ReadAllText(path));
    }

    private sealed class Loader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }
}
