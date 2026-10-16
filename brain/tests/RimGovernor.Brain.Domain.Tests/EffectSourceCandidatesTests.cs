using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using WasteItem = RimGovernor.Brain.Policy.WasteItem;
using Amount = RimGovernor.Brain.Policy.Amount;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

// #3224: the effect-source generator searches from a mood shortfall through the verified card copy to build/haul/dispose
// chains. The fixtures mirror the TombStrangerParityTests scenarios (#3219); the generator itself names no def or class.
public sealed class EffectSourceCandidatesTests
{
    private const string Def = "KnowBuriedInSarcophagus";
    private const string Container = "RimWorld.Building_Sarcophagus";
    private static readonly MechanicCard Shipped = MechanicCards.ByDef[Def];
    private static readonly Dictionary<string, MechanicCard> Verified = new() { [Def] = Shipped with { Status = MechanicCardStatus.Verified } };
    private static readonly Resource Wood = new("WoodLog");

    internal static NeedsForecastPawn Pawn(string id, double mood) => new()
    {
        Pawn = id, State = "eligible", Mood = Known(mood), MoodTarget = Known(mood), MoodUnclampedTarget = Known(mood),
        LocalHour = Known(6d), Awake = Known(true), MoodRisePerTick = Known(.12 / GameTime.TicksPerHour),
        MoodFallPerTick = Known(.08 / GameTime.TicksPerHour), MoodIntervalTicks = Known(150d), MoodFreezesWhileAsleep = Known(true),
        BreakThreshold = Known(.3), ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([]),
        Memories = Known<EquatableArray<NeedMemory>>([]), Needs = [new("rest", Known(1d), Known(0d), Known(.3), Known(1d))],
    };

    private static MoodPawn Colonist(string id, params double[] stacks) => new()
    {
        Id = new PawnId(id), Mood = Known(.28),
        Thoughts = Known<EquatableArray<MoodThought>>([.. stacks.Select(o => new MoodThought(Def, o)), new MoodThought("AteWithoutTable", -3)]),
    };

    private static WasteItem Corpse(string id, CorpseOf of, bool everBuried = false) =>
        new() { Id = id, Kind = "Corpse", State = WasteState.Exposed, Eligible = true, Cell = new Cell(30, 30), CorpseOf = of, EverBuriedInSarcophagus = everBuried };

    private static WasteItem Buried(string id, CorpseOf of, string grave) =>
        new() { Id = id, Kind = "Corpse", State = WasteState.Buried, CorpseOf = of, Grave = grave };

    private static readonly EffectSourceSlot Free = new("slot/1", Container, "Sarcophagus", "WoodLog", new Cell(5, 5));

    private static EffectSourceSlot Occupied(string occupant) =>
        Free with { Occupant = occupant, Refund = Known<EquatableArray<Amount>>([new(Wood, 30)]) };

    internal static EffectSourceFacts Facts(double mood = .28, MoodPawn? colonist = null, IReadOnlyList<WasteItem>? waste = null,
        IReadOnlyList<EffectSourceSlot>? slots = null, int budget = 32) => new()
    {
        Needs = new() { Tick = 100, Pawns = [Pawn("pawn", mood)] },
        Horizon = 300,
        Colonists = Known<EquatableArray<MoodPawn>>([colonist ?? Colonist("pawn")]),
        Waste = waste ?? [Corpse("raider", CorpseOf.Stranger)],
        Slots = slots ?? [Free],
        Costs = Known<EquatableArray<WorkBuildingCost>>([new("Sarcophagus", "WoodLog", Known<EquatableArray<Amount>>([new(Wood, 60)]))]),
        Work = [new("pawn", "slot/1", Known(60d)), new("pawn", "raider", Known(40d)), new("pawn", "s1", Known(30d))],
        Routes = [new("pawn", "start", "cell/5/5", Known(10d)), new("pawn", "cell/5/5", "cell/30/30", Known(20d)),
            new("pawn", "start", "cell/30/30", Known(20d))],
        Budget = budget,
    };

    private static EffectSourceResult Generate(EffectSourceFacts facts, IReadOnlyDictionary<string, MechanicCard>? cards = null) =>
        GenerateEffectSources(facts, cards ?? Verified);

    private static string[] Kinds(EffectSourceResult result) => [.. result.Steps.Select(s => s.Action + ":" + s.Target)];

    [Fact]
    public void FreeSlotGivesBuildThenHaulWithTheCardsMemoryAndTheBuildCost()
    {
        var result = Generate(Facts());

        Assert.Equal(["Building:slot/1", "Haul:raider"], Kinds(result));
        var tasks = result.Family.Problem!.Activities;
        Assert.Equal([tasks[0].Id], tasks[1].Prerequisites);
        Assert.Equal([new ForecastActivityYield(Wood, Known(60d))], tasks[0].Inputs);
        Assert.Equal(("cell/30/30", "cell/5/5"), (tasks[1].Location, tasks[1].EndLocation));
        Assert.Equal("colonist", result.Steps[1].Constraint);
        var root = Assert.Single(result.Roots);
        Assert.Equal([tasks[1].Id], root.ActivityCompletions);
        Assert.Equal("effect/" + Def, root.ExclusiveOutcome);
        var memory = Assert.Single(root.MoodMemories);
        Assert.Equal(new ForecastMoodMemory("pawn", new NeedMemory(Def, 4, Known(8d * GameTime.TicksPerDay), false)), memory);
        Assert.False(result.Family.Truncated);
    }

    [Theory]
    [InlineData(new double[] { 4, 2 }, 1)]
    [InlineData(new double[] { 4, 2, 1, .5 }, 0)]
    public void MarginalStackFollowsTheCardXmlAndTheCapGivesNone(double[] stacks, double marginal)
    {
        var result = Generate(Facts(colonist: Colonist("pawn", stacks)));

        if (marginal == 0) { Assert.Empty(result.Roots); Assert.Null(result.Family.Problem); return; }
        Assert.Equal(marginal, Assert.Single(Assert.Single(result.Roots).MoodMemories).Memory.Offset);
    }

    [Theory]
    [InlineData(CorpseOf.Stranger, true)]
    [InlineData(CorpseOf.Animal, false)]
    [InlineData(CorpseOf.Colonist, false)]
    public void FlaggedAnimalAndColonistBodiesGiveNoCandidate(CorpseOf of, bool flagged)
    {
        var result = Generate(Facts(waste: [Corpse("raider", of, flagged)]));

        Assert.Empty(result.Roots);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public void ExtractedCardGivesNoCandidate()
    {
        var result = Generate(Facts(), new Dictionary<string, MechanicCard> { [Def] = Shipped });

        Assert.Equal(MechanicCardStatus.Extracted, Shipped.Status);
        Assert.Empty(result.Roots);
        Assert.Contains("effect_source_card_unverified:" + Def, result.Family.Findings);
    }

    [Fact]
    public void ExhaustedStrangerOnlyOccupantBelowTheCapIsDisposedForAFreshOne()
    {
        var result = Generate(Facts(colonist: Colonist("pawn", 4), waste: [Corpse("raider", CorpseOf.Stranger), Buried("old", CorpseOf.Stranger, "s1")],
            slots: [Occupied("s1")]));

        Assert.Equal(["Deconstruction:s1", "Building:slot/1", "Haul:raider"], Kinds(result));
        var tasks = result.Family.Problem!.Activities;
        Assert.Equal([new ForecastActivityYield(Wood, Known(30d))], tasks[0].Outputs);
        Assert.Equal([tasks[0].Id], tasks[1].Prerequisites);
        Assert.Equal(2, Assert.Single(Assert.Single(result.Roots).MoodMemories).Memory.Offset);
    }

    [Fact]
    public void OccupantHoldingAColonistIsBurialsAndIsNotDisposed()
    {
        var result = Generate(Facts(waste: [Corpse("raider", CorpseOf.Stranger), Buried("kin", CorpseOf.Colonist, "s1")], slots: [Occupied("s1")]));

        Assert.Empty(result.Roots);
    }

    [Fact]
    public void NoShortfallOrUnreadThoughtsGiveNone()
    {
        Assert.Empty(Generate(Facts(mood: .9)).Roots);
        var unread = Generate(Facts(colonist: new MoodPawn { Id = new PawnId("pawn"), Mood = Known(.28), Thoughts = Unknown<EquatableArray<MoodThought>>() }));
        Assert.Empty(unread.Roots);
        Assert.Contains("effect_source_stacks_unknown:" + Def, unread.Family.Findings);
    }

    [Fact]
    public void IdenticalInputsInAnyOrderGiveIdenticalOutput()
    {
        WasteItem[] waste = [Corpse("b", CorpseOf.Stranger), Corpse("a", CorpseOf.Stranger), Buried("old", CorpseOf.Stranger, "s1")];
        EffectSourceSlot[] slots = [Occupied("s1") with { Id = "slot/2" }, Free];
        var facts = Facts(colonist: Colonist("pawn", 4), waste: waste, slots: slots) is var f
            ? f with { Work = [.. f.Work, new("pawn", "a", Known(40d)), new("pawn", "b", Known(40d)), new("pawn", "slot/2", Known(60d))] } : null!;
        var shuffled = facts with { Waste = [.. waste.Reverse()], Slots = [.. slots.Reverse()], Work = [.. facts.Work.Reverse()], Routes = [.. facts.Routes.Reverse()] };

        var one = Signature(Generate(facts));
        Assert.Equal(one, Signature(Generate(facts)));
        Assert.Equal(one, Signature(Generate(shuffled)));
        Assert.Equal(4, Generate(facts).Roots.Count);
    }

    [Fact]
    public void ExhaustedBudgetIsReported()
    {
        var result = Generate(Facts(waste: [Corpse("a", CorpseOf.Stranger), Corpse("b", CorpseOf.Stranger)], budget: 1) is var f
            ? f with { Work = [.. f.Work, new("pawn", "a", Known(40d)), new("pawn", "b", Known(40d))] } : null!);

        Assert.True(result.Family.Truncated);
        Assert.Contains("effect_source_generation_truncated", result.Family.Findings);
        Assert.Single(result.Roots);
    }

    private static string Signature(EffectSourceResult result) => string.Join("\n",
        result.Steps.Select(s => s.ToString())
            .Concat(result.Roots.Select(r => r.Id + "|" + string.Join(",", r.ActivityCompletions) + "|" + string.Join(",", r.MoodMemories)))
            .Concat(result.Family.Problem?.Activities.Select(a => a.Id + "|" + string.Join(",", a.Prerequisites) + "|" + string.Join(",", a.Inputs) + "|" + string.Join(",", a.Outputs)) ?? [])
            .Concat(result.Family.Problem?.Options.Select(o => o.ToString()) ?? [])
            .Concat(result.Family.Problem?.Routes.Select(o => o.ToString()) ?? [])
            .Concat(result.Family.Findings));
}
