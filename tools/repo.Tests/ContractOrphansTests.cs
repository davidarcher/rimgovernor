using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RimGovernor.Tools.Workflow.Commands;

namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>
/// The contract-orphan gate over three small compiled assemblies standing in for the contracts, the brain and the
/// native mod (and a Policy assembly and its caller): what counts as a read, and the orphans it fails on.
/// </summary>
public sealed class ContractOrphansTests : IDisposable
{
    private const string ContractsSource = """
        using System;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Text.Json.Serialization.Metadata;

        namespace RimGovernor.Contracts.Wire.Observations
        {
            public sealed record HuntCensus(int Hunters, [property: JsonPropertyName("benchList")] int Benches)
            {
                public int Prey { get; init; }
                public int Spare { get; init; }
                public int Total => Hunters + Prey;
            }

            public sealed record Order(string Pawn)
            {
                public int Priority { get; init; }
            }
        }

        namespace RimGovernor.Contracts.Wire
        {
            // Stands in for the source-generated context: the same camelCase policy, resolved by reflection.
            public sealed class WireJsonContext() : JsonSerializerContext(null)
            {
                private static readonly JsonSerializerOptions Reflected =
                    new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

                public static WireJsonContext Default { get; } = new();

                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;

                public override JsonTypeInfo? GetTypeInfo(Type type) => Reflected.GetTypeInfo(type);
            }
        }
        """;

    private readonly string dir = Directory.CreateTempSubdirectory("contract-orphans-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void FailsOnAMemberTheNativeSideWritesAndTheBrainNeverReads()
    {
        var orphans = Find(
            brain: """
                using RimGovernor.Contracts.Wire.Observations;
                public static class Planner
                {
                    public static int Plan(HuntCensus census) => census.Total;
                    public static Order Next(HuntCensus census) => new("p1") { Priority = census is { Spare: > 0 } ? 1 : 0 };
                }
                """,
            native: """
                using RimGovernor.Contracts.Wire.Observations;
                public static class Reader
                {
                    public static HuntCensus Read() => new(2, 1) { Prey = 3, Spare = 1 };
                    public static int Apply(Order order) => order.Priority + order.Pawn.Length;
                }
                """,
            out var checkedCount);

        Assert.Equal(7, checkedCount);
        // Total reads Hunters and Prey, a property pattern reads Spare, the brain writes Order for the native side
        // to read; only Benches is written and never read.
        Assert.Equal(["Contracts|Observations.HuntCensus.Benches: written by the native side, never read by the brain"], orphans);
    }

    [Fact]
    public void AnIntentRecordTheNativeSideReadsAndConstructsIsLiveButAnObservationItWritesIsNot()
    {
        var contracts = Compile("RimGovernor.Brain.Contracts", """
            namespace RimGovernor.Contracts.Wire.Operations
            {
                public sealed record Setting { public int Count { get; init; } public string Name { get; init; } = ""; public int Spare { get; init; } }
            }
            namespace RimGovernor.Contracts.Wire.Observations
            {
                public sealed record Reading { public int Value { get; init; } }
            }
            """);
        var brain = Compile("RimGovernor.Brain.Policy", """
            using RimGovernor.Contracts.Wire.Operations;
            public static class Planner
            {
                public static Setting Plan() => new() { Count = 3, Name = "bill", Spare = 1 };
            }
            """, contracts);
        var native = Compile("RimGovernor.Bridge", """
            using RimGovernor.Contracts.Wire.Observations;
            using RimGovernor.Contracts.Wire.Operations;
            public static class Reader
            {
                public static bool Verify(Setting actual) => actual.Count == new Setting { Count = 3, Name = "bill" }.Count && actual.Name.Length > 0;
                public static Reading Read() => new() { Value = 1 };
            }
            """, contracts);

        var orphans = ContractOrphans.Find(ContractOrphans.Wire, contracts, [brain], [native], out _);

        // Count and Name are intent the native side reads (its own copy's writes do not flag them); Spare is never read.
        Assert.Equal(
            [("Contracts|Observations.Reading.Value", "written by the native side, never read by the brain"), ("Contracts|Operations.Setting.Spare", "written, never read")],
            orphans);
    }

    [Fact]
    public void AnObservationRequestTheBrainSendsIsLiveWhenTheNativeSideAlsoBuildsOne()
    {
        var contracts = Compile("RimGovernor.Brain.Contracts", """
            namespace RimGovernor.Contracts.Wire.Observations
            {
                public sealed record Query { public bool Planning { get; init; } public bool Extra { get; init; } }
            }
            """);
        var brain = Compile("RimGovernor.Brain.Policy", """
            using RimGovernor.Contracts.Wire.Observations;
            public static class Asker
            {
                public static Query Ask() => new() { Planning = false };
            }
            """, contracts);
        var native = Compile("RimGovernor.Bridge", """
            using RimGovernor.Contracts.Wire.Observations;
            public static class Reader
            {
                public static Query Frame() => new() { Planning = true, Extra = true };
                public static bool Read(Query query) => query.Planning || query.Extra;
            }
            """, contracts);

        var orphans = ContractOrphans.Find(ContractOrphans.Wire, contracts, [brain], [native], out _);

        // The brain sends Planning and the native side reads it; Extra only the native side's own frame request sets.
        Assert.Equal([("Contracts|Observations.Query.Extra", "written by the native side, never read by the brain")], orphans);
    }

    [Fact]
    public void FailsOnAMemberNothingReferencesAndIgnoresEqualityAndCloning()
    {
        var orphans = Find(
            brain: """
                using RimGovernor.Contracts.Wire.Observations;
                public static class Planner
                {
                    public static bool Same(HuntCensus a, HuntCensus b) => a == b && a.GetHashCode() == b.GetHashCode() && a.ToString() == b.ToString();
                    public static int Read(HuntCensus census) => census.Hunters + census.Benches + census.Prey + census.Spare + census.Total;
                    public static HuntCensus Again(HuntCensus census) => census with { Spare = 0 };
                }
                """,
            native: """
                using RimGovernor.Contracts.Wire.Observations;
                public static class Reader
                {
                    public static HuntCensus Read() => new(2, 1) { Prey = 3, Spare = 1 };
                }
                """,
            out _);

        Assert.Equal(
            ["Contracts|Observations.Order.Pawn: not referenced outside its declaration", "Contracts|Observations.Order.Priority: not referenced outside its declaration"],
            orphans);
    }

    [Fact]
    public void FailsOnAPolicyRecordMemberNoDecisionReads()
    {
        var policy = Compile("RimGovernor.Brain.Policy", """
            using System.Collections.Generic;
            using System.Linq;
            namespace RimGovernor.Brain.Policy;

            public sealed record HuntBench(string Id, bool Usable)
            {
                public bool CookingActive { get; init; }
            }

            public interface IHuntRule
            {
                bool Allows(HuntBench bench);
            }

            internal sealed class UsableRule : IHuntRule
            {
                public bool Allows(HuntBench bench) => bench.Usable;
            }

            public static class HuntGate
            {
                public static IHuntRule Rule() => new UsableRule();
                public static IEnumerable<string> Ready(IEnumerable<HuntBench> benches)
                {
                    foreach (var bench in benches.Where(b => Rule().Allows(b)))
                    {
                        yield return bench.Id;
                    }
                }
            }
            """);
        var runtime = Compile("RimGovernor.Brain.BuildingRuntime", """
            using System.Linq;
            using RimGovernor.Brain.Policy;
            public static class Rounds
            {
                public static int Count() => HuntGate.Ready([new HuntBench("b1", true) { CookingActive = true }]).Count();
            }
            """, policy);

        var orphans = ContractOrphans.Find(ContractOrphans.PolicyRecords, policy, [runtime], [], out var checkedCount);

        // Id is read in an iterator body and Usable through an interface implementation, both reached only because
        // the brain calls Ready and Ready constructs them; CookingActive is written and never read.
        Assert.Equal(3, checkedCount);
        Assert.Equal([("Policy|HuntBench.CookingActive", "written, never read")], orphans);
    }

    [Fact]
    public void CountsAReadInALambdaOfAStaticFieldInitializerWhenTheBrainTouchesTheType()
    {
        var policy = Compile("RimGovernor.Brain.Policy", """
            using System;
            using System.Collections.Generic;
            namespace RimGovernor.Brain.Policy;

            public sealed record HuntRoom(string Id)
            {
                public int Hunters { get; init; }
                public int Spare { get; init; }
            }

            public static class HuntRules
            {
                private static readonly Lazy<IReadOnlyDictionary<string, Func<HuntRoom, int>>> Rules = new(() =>
                    new Dictionary<string, Func<HuntRoom, int>> { ["hunt"] = room => room.Hunters });

                public static int Apply(HuntRoom room) => Rules.Value["hunt"](room);
            }
            """);
        var runtime = Compile("RimGovernor.Brain.BuildingRuntime", """
            using RimGovernor.Brain.Policy;
            public static class Rounds
            {
                public static int Count() => HuntRules.Apply(new HuntRoom("r1") { Hunters = 2, Spare = 1 });
            }
            """, policy);

        var orphans = ContractOrphans.Find(ContractOrphans.PolicyRecords, policy, [runtime], [], out _);

        // Hunters is read only inside the Lazy factory's lambda, which the static constructor builds when Apply
        // touches Rules; Id and Spare are written and never read.
        Assert.Equal([("Policy|HuntRoom.Id", "written, never read"), ("Policy|HuntRoom.Spare", "written, never read")], orphans);
    }

    [Fact]
    public void CountsAMemberAnAcceptanceCaseReadsByJsonKeyAsRead()
    {
        var contracts = Compile("RimGovernor.Brain.Contracts", ContractsSource);
        var cases = Compile("RimGovernor.Brain.Acceptance.Cases", """
            using System.Text.Json.Nodes;
            public static class Census
            {
                public static int Benches(JsonNode view) => (int)view["census"]!["benchList"]! + (int)view["census"]!["benches"]!;
                public static string Why() => "no bench yet";
            }
            """);
        var native = Compile("RimGovernor.Bridge", """
            using RimGovernor.Contracts.Wire.Observations;
            public static class Reader
            {
                public static HuntCensus Read() => new(2, 1) { Prey = 3, Spare = 1 };
            }
            """, contracts);

        var keys = ContractOrphans.LiteralKeys([cases]);
        var names = ContractOrphans.WireNames(contracts);
        var readByName = names.Where(n => keys.Contains(n.Value)).Select(n => n.Key).ToHashSet();
        var orphans = ContractOrphans.Find(ContractOrphans.Wire, contracts, [cases], [native], out _, readByName);

        // A sentence is no key; the JSON name comes from the context, so "benchList" reads Benches and "benches" nothing.
        Assert.Equal(["benchList", "benches", "census"], keys.Order(StringComparer.Ordinal));
        Assert.Equal("benchList", names["Observations.HuntCensus.Benches"]);
        Assert.Equal("prey", names["Observations.HuntCensus.Prey"]);
        Assert.Equal(["Observations.HuntCensus.Benches"], readByName);
        Assert.DoesNotContain(orphans, o => o.Key == "Contracts|Observations.HuntCensus.Benches");
        Assert.Contains(orphans, o => o.Key == "Contracts|Observations.HuntCensus.Hunters");
    }

    [Fact]
    public void FailsOnAFixtureContractMemberNoAcceptanceCaseSetsOrReads()
    {
        var fixtures = Compile("RimGovernor.Fixtures.Contracts", """
            namespace RimGovernor.Fixtures.Contracts;

            public sealed record StageSpec
            {
                public bool Paused { get; init; }
                public int Seed { get; init; }
                public string Note { get; init; } = "";
            }

            public sealed record StageReply(string Site)
            {
                public string Digest { get; init; } = "";
            }
            """);
        var cases = Compile("RimGovernor.Brain.Acceptance.Cases", """
            using RimGovernor.Fixtures.Contracts;
            public static class LabStage
            {
                public static StageSpec Spec() => new() { Paused = true };
                public static string Where(StageReply reply) => reply.Site;
            }
            """, fixtures);

        var orphans = ContractOrphans.Find(ContractOrphans.Fixtures, fixtures, [cases], [], out var checkedCount);

        // The fixture build that reads the spec and writes the reply is out of sight: a case setting Paused uses it, as a
        // case reading Site does; nothing sets Seed, Note's initializer is not a case's write, and nothing reads Digest.
        Assert.Equal(5, checkedCount);
        Assert.Equal(
            [
                ("Fixtures|StageReply.Digest", "not referenced outside its declaration"),
                ("Fixtures|StageSpec.Note", "not referenced outside its declaration"),
                ("Fixtures|StageSpec.Seed", "not referenced outside its declaration"),
            ],
            orphans);
    }

    private IReadOnlyList<string> Find(string brain, string native, out int checkedCount)
    {
        var contracts = Compile("RimGovernor.Brain.Contracts", ContractsSource);
        return [.. ContractOrphans.Find(
            ContractOrphans.Wire,
            contracts,
            [Compile("RimGovernor.Brain.Policy", brain, contracts)],
            [Compile("RimGovernor.Bridge", native, contracts)],
            out checkedCount).Select(o => $"{o.Key}: {o.Reason}")];
    }

    private string Compile(string name, string source, params string[] references)
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            [.. platform.Concat(references).Select(r => MetadataReference.CreateFromFile(r))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var path = Path.Combine(dir, name + ".dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return path;
    }
}
