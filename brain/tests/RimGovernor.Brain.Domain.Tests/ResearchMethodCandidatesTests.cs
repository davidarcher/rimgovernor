using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
namespace RimGovernor.Brain.Domain.Tests;

public sealed class ResearchMethodCandidatesTests
{
    static ResearchWorker Worker(string pawn, string bench = "bench", double points = 0.5)
        => new(pawn, bench, new(5, 5), "Stonecutting", "Research", "Research", points);

    static ResearchActivityRead Read(params ResearchWorker[] workers)
        => new() { Workers = EquatableArray.From(workers), Findings = [] };

    static WorkResourceClaims Claims(string[]? pawns = null, string[]? targets = null)
        => new([], EquatableArray.From(pawns ?? []), EquatableArray.From(targets ?? []), []);

    [Fact]
    public void TwoPawnsAtOneBenchGiveTwoExclusiveHourBlocksCarryingTheirPoints()
    {
        var made = ResearchMethodCandidates.Generate(Read(Worker("a", points: 0.5), Worker("b", points: 0.25)), GameTime.TicksPerDay, Claims(), 9000);
        Assert.Equal(2, made.Roots.Length);
        Assert.Equal(["research/a", "research/b"], made.Roots.Select(r => r.ExclusiveOutcome));
        Assert.All(made.Options, o => Assert.Equal(Known((double)GameTime.TicksPerHour), o.WorkTicks));
        Assert.All(made.Options, o => Assert.Equal(["bench"], o.Equipment));
        Assert.Equal([0.5, 0.25], made.Tasks.Select(t => t.ProjectProgress!.PointsPerTick));
        Assert.All(made.Tasks, t => Assert.Equal("Stonecutting", t.ProjectProgress!.Project));
        var binding = made.Bindings[("research/a/bench", "a")];
        Assert.Equal(PawnActivityKind.Work, binding.Kind);
        Assert.Equal(("bench", "Research", "Research", 9000L), (binding.Target, binding.WorkGiver, binding.Job, binding.Deadline));
    }

    [Fact]
    public void ClaimedPawnsAndBenchesAreSkipped()
    {
        var read = Read(Worker("a"), Worker("b", "other"));
        Assert.Equal("research/b/other", Assert.Single(ResearchMethodCandidates.Generate(read, GameTime.TicksPerDay, Claims(pawns: ["a"]), 1).Roots).Id);
        Assert.Equal("research/a/bench", Assert.Single(ResearchMethodCandidates.Generate(read, GameTime.TicksPerDay, Claims(targets: ["other"]), 1).Roots).Id);
    }

    [Fact]
    public void NoSpeedAndShortHorizonAndNoProjectAreFindingsNotTasks()
    {
        var still = ResearchMethodCandidates.Generate(Read(new ResearchWorker("a", "bench", new(5, 5), "p", "Research", "Research", 0)), GameTime.TicksPerDay, Claims(), 1);
        Assert.Empty(still.Tasks);
        Assert.Contains("research_speed_none:a", still.Findings);
        var short_ = ResearchMethodCandidates.Generate(Read(Worker("a")), GameTime.TicksPerHour - 1, Claims(), 1);
        Assert.Empty(short_.Tasks);
        Assert.Contains("research_block_exceeds_horizon", short_.Findings);
        var none = ResearchMethodCandidates.Generate(new() { Workers = [], Findings = ["research_no_current_project"] }, GameTime.TicksPerDay, Claims(), 1);
        Assert.Empty(none.Tasks);
        Assert.Equal(["research_no_current_project"], none.Findings);
    }

    [Fact]
    public void TheReadRoundTripsOnTheWire()
    {
        var read = CoupledFixtures.Empty with { Research = Read(Worker("a")) };
        var back = WireJson.Deserialize<CoupledForecastRead>(WireJson.Serialize(read));
        Assert.Equal(read.Research, back!.Research);
    }
}
