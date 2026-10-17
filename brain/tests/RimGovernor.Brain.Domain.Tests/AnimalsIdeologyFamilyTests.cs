using System.Text.Json.Nodes;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;
using StoreWorld = RimGovernor.Brain.Store.World;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The animals and ideology families (#3260): Needs with pure met, unmet and unknown predicates; the ward as the
/// sterilize Need's precondition; role Methods admitted side by side and refused on a shared role; the pending
/// reform kept in the <c>family/ideology/reform</c> record across a save and reload, and read once from an older
/// save's Standard record.
/// </summary>
public sealed class AnimalsIdeologyFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static Need Animal(AnimalsFacts facts, NeedId id, bool containment = true) =>
        Assert.Single(new AnimalsFamily(true, containment).Derive(facts), n => n.Id == id);

    private static Need Ideology(IdeologyFacts facts, NeedId id) => Assert.Single(new IdeologyFamily().Derive(facts), n => n.Id == id);

    private static HerdStep Step(HerdStepKind kind) => new() { Kind = kind };

    [Fact]
    public void ContainmentIsMetUnmetOrUnknownByThePenBoundAnimalsAndThePenStep()
    {
        var penned = new AnimalsFacts { Uncontained = Known<EquatableArray<PawnId>>([]), Pen = Known(Step(HerdStepKind.None)) };
        Assert.Equal((Satisfaction.Met, NeedTier.Wellbeing), (Animal(penned, AnimalsFamily.Containment).Satisfaction, Animal(penned, AnimalsFamily.Containment).Tier));

        var loose = Animal(penned with { Uncontained = Known<EquatableArray<PawnId>>([new("cow"), new("pig")]) }, AnimalsFamily.Containment);
        Assert.Equal((Satisfaction.Unmet, 2.0), (loose.Satisfaction, loose.Deficit.Value));

        var owed = Animal(penned with { Pen = Known(Step(HerdStepKind.Reconcile)) }, AnimalsFamily.Containment);
        Assert.Equal((Satisfaction.Unmet, 1.0), (owed.Satisfaction, owed.Deficit.Value));

        // No latch: the Need is only what this read shows, and an unread census is unknown.
        Assert.Equal(Satisfaction.Unknown, Animal(penned with { Uncontained = Unknown<EquatableArray<PawnId>>() }, AnimalsFamily.Containment).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Animal(new AnimalsFacts(), AnimalsFamily.Containment).Satisfaction);
    }

    [Fact]
    public void TheWardIsMetUnmetOrUnknownByItsStep()
    {
        Assert.Equal(Satisfaction.Met, Animal(new AnimalsFacts { Ward = Known(Step(HerdStepKind.None)) }, AnimalsFamily.Ward).Satisfaction);
        var medical = Animal(new AnimalsFacts { Ward = Known(Step(HerdStepKind.Medical)) }, AnimalsFamily.Ward);
        Assert.Equal(Satisfaction.Unmet, medical.Satisfaction);
        Assert.Contains(medical.Evidence, e => e.Fact == "ward_step" && e.Value.Value == "medical");
        Assert.Equal(Satisfaction.Unknown, Animal(new AnimalsFacts(), AnimalsFamily.Ward).Satisfaction);
    }

    [Fact]
    public void SterilizeWaitsOnTheWardAndIsMetUnmetOrUnknownByTheCensusAndTheWardArea()
    {
        // The herd ward is sterilize's precondition when the family composes it, and nothing else waits on another Need.
        var needs = new AnimalsFamily().Derive(new AnimalsFacts());
        Assert.Equal([AnimalsFamily.Ward], Assert.Single(needs, n => n.Id == AnimalsFamily.Sterilize).Preconditions);
        Assert.All(needs.Where(n => n.Id != AnimalsFamily.Sterilize), n => Assert.Empty(n.Preconditions));
        Assert.Empty(Animal(new AnimalsFacts(), AnimalsFamily.Sterilize, containment: false).Preconditions);
        Assert.DoesNotContain(new AnimalsFamily(true, false).Derive(new AnimalsFacts()), n => n.Id == AnimalsFamily.Ward);

        var cow = new UpkeepAnimal { Id = new("cow"), Definition = new("Cow"), AllowedArea = Known("pasture") };
        var empty = new AnimalsFacts { Animals = Known<EquatableArray<UpkeepAnimal>>([cow]), WardRoom = new AnimalWard { Area = Known("ward") } };
        Assert.Equal(Satisfaction.Met, Animal(empty, AnimalsFamily.Sterilize).Satisfaction);

        var held = empty with { Animals = Known<EquatableArray<UpkeepAnimal>>([cow with { AllowedArea = Known("ward") }]) };
        Assert.Equal(Satisfaction.Unmet, Animal(held, AnimalsFamily.Sterilize).Satisfaction);

        Assert.Equal(Satisfaction.Unknown, Animal(empty with { WardRoom = new AnimalWard() }, AnimalsFamily.Sterilize).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Animal(new AnimalsFacts(), AnimalsFamily.Sterilize).Satisfaction);
    }

    [Fact]
    public void TheHerdIsUnknownWhileAnyChoiceReadsAnUnreadFactAndMetWithNothingOwed()
    {
        var unknown = Animal(new AnimalsFacts(), AnimalsFamily.Herd);
        Assert.Equal((Satisfaction.Unknown, NeedTier.Development), (unknown.Satisfaction, unknown.Tier));
        Assert.False(unknown.Deficit.IsKnown);

        var none = new AnimalsFacts
        {
            Animals = Known<EquatableArray<UpkeepAnimal>>([]),
            Wild = Known<EquatableArray<UpkeepAnimal>>([]),
            FeedShort = Known(false),
            Handlers = Known<EquatableArray<PawnProfile>>([]),
        };
        // The food plan's slaughter and tame read it: unread, the herd is unknown, never held as met.
        Assert.Equal(Satisfaction.Unknown, Animal(none, AnimalsFamily.Herd).Satisfaction);
        var planned = none with { FoodPlan = Known(new FoodPlan()) };
        Assert.Equal(Satisfaction.Met, Animal(planned, AnimalsFamily.Herd).Satisfaction);
        Assert.Empty(new AnimalsFamily().Propose([Animal(planned, AnimalsFamily.Herd)], planned));
    }

    [Fact]
    public void RolesAndRitualsAreMetUnmetOrUnknownByWhatIsOwed()
    {
        var owed = Ideology(new IdeologyFacts { Roles = Known<EquatableArray<RoleAssignment>>([new(new("a"), "role-1", "Leader")]) }, IdeologyFamily.Roles);
        Assert.Equal((Satisfaction.Unmet, 1.0), (owed.Satisfaction, owed.Deficit.Value));
        Assert.Equal(Satisfaction.Met, Ideology(new IdeologyFacts { Roles = Known<EquatableArray<RoleAssignment>>([]) }, IdeologyFamily.Roles).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Ideology(new IdeologyFacts(), IdeologyFamily.Roles).Satisfaction);

        Assert.Equal(Satisfaction.Met, Ideology(new IdeologyFacts { Rituals = Known<EquatableArray<RitualPlan>>([]) }, IdeologyFamily.Rituals).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Ideology(new IdeologyFacts(), IdeologyFamily.Rituals).Satisfaction);
        var rituals = new Need { Id = IdeologyFamily.Rituals, Tier = NeedTier.Development, Satisfaction = Satisfaction.Unmet };
        Assert.Equal(IdeologyFamily.UnsupportedRitual, Assert.Single(new IdeologyFamily().Propose([rituals], new IdeologyFacts())).Hold!.Subject);
    }

    private static readonly IdeoligionDesign Current = new(["MemeA"], ["PreceptA"], true);

    private static readonly IdeoligionDesign Target = new(["MemeB"], ["PreceptB"], true);

    private static Fact<DesignChoice> Choice(DesignDecision decision) =>
        Known(new DesignChoice { Design = Target, Comparison = new DesignComparison { Decision = decision } });

    [Fact]
    public void TheReformIsUnmetWhileAnImprovingDesignIsChosenAndMetOnceObserved()
    {
        var intent = new IdeoligionReform("ideo-1", Current, Target, 3);
        var owed = new IdeologyFacts { Reform = Choice(DesignDecision.Improves), Intent = intent };
        Assert.Equal(Satisfaction.Unmet, Ideology(owed, IdeologyFamily.Reform).Satisfaction);
        Assert.Equal(Satisfaction.Met, Ideology(owed with { ReformObserved = true }, IdeologyFamily.Reform).Satisfaction);
        Assert.Equal(Satisfaction.Met, Ideology(owed with { Reform = Choice(DesignDecision.Unchanged) }, IdeologyFamily.Reform).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, Ideology(new IdeologyFacts(), IdeologyFamily.Reform).Satisfaction);

        // The Method names the reform it makes; with no intent the Need is held on its unread inputs.
        var need = Ideology(owed, IdeologyFamily.Reform);
        var method = Assert.Single(new IdeologyFamily().Propose([need], owed));
        Assert.Equal("ideoligion-3", method.Method.Value);
        Assert.Equal(IdeologyFamily.ReformUnread, Assert.Single(new IdeologyFamily().Propose([need], owed with { Intent = null })).Hold!.Subject);
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<IdeologyFacts> Runner { get; init; }
        public IdeologyFacts Facts { get; set; } = new();

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public ConcernProgress? Row(NeedId need) => Journal.LoadRounds(TestContext.Current.CancellationToken).Progress.SingleOrDefault(p => p.Concern == need.FilingKey);
    }

    private static Journal OpenJournal(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-" + name + "-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        return journal;
    }

    private static Colony Open()
    {
        Colony? colony = null;
        var runner = new FamilyRunner<IdeologyFacts>(new IdeologyFamily(true, false, false), (_, _) => Task.FromResult(colony!.Facts),
            new FamilySchedule { Kinds = [ActionKind.Assign] });
        colony = new Colony { Journal = OpenJournal("ideology"), Runner = runner };
        return colony;
    }

    private static IdeologyFacts Roles(params RoleAssignment[] roles) => new() { Roles = Known<EquatableArray<RoleAssignment>>([.. roles]) };

    [Fact]
    public void RoleMethodsRunSideBySideAndAShareOfARoleIsHeldByItsClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Roles(new RoleAssignment(new("a"), "role-1", "Leader"), new RoleAssignment(new("b"), "role-2", "Shooter"));
        var run = colony.Run();
        Assert.Equal(Outcome.Admitted, Assert.Single(run.Reports, r => r.Need == IdeologyFamily.Roles).Verdict.Outcome);
        Assert.Equal<string>(["ideorole-a-role-1", "ideorole-b-role-2"], run.Methods.Select(m => m.Method.Value).Order(StringComparer.Ordinal));
        Assert.Equal(2, colony.Journal.LoadNeed(IdeologyFamily.Roles, World, ct).Methods.Count);
        Assert.StartsWith("Ideoligion roles", OperationIntent.Of(colony.Journal.PlanMethod(run.Methods.First(m => m.Method.Value == "ideorole-a-role-1").Plan, ct)!), StringComparison.Ordinal);

        // Another believer for role-1 overlaps the open Method's claim and is refused with nothing written.
        colony.Facts = Roles(new RoleAssignment(new("c"), "role-1", "Leader"));
        var held = Assert.Single(colony.Run().Reports, r => r.Need == IdeologyFamily.Roles).Verdict;
        Assert.True(held.Is(Cause.Claim));
        Assert.Equal("role-1", held.Refusal.Subject);
        Assert.Equal(2, colony.Journal.LoadNeed(IdeologyFamily.Roles, World, ct).Methods.Count);
    }

    [Fact]
    public void NeedRowsReachRoundsProgress()
    {
        using var colony = Open();
        colony.Facts = Roles(new RoleAssignment(new("a"), "role-1", "Leader"));
        colony.Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [PlannerWave.NeedProgressOf("ideology", colony.Run())!], TestContext.Current.CancellationToken);
        var row = colony.Row(IdeologyFamily.Roles)!;
        Assert.Equal(("ideorole-a-role-1", true), (row.Method, row.Open));
    }

    [Fact]
    public void EachFamilyIsOneDueQueueEntryAndTheOldPlannersAreGone()
    {
        var animals = ClockPlannerCatalog.FamilyEntry(new FamilyRunner<AnimalsFacts>(new AnimalsFamily(), (_, _) => Task.FromResult(new AnimalsFacts()), AnimalsReader.Schedule));
        Assert.Equal("animals", animals.Name);
        Assert.Contains(ActionKind.Husbandry, animals.Kinds);
        Assert.Contains(ActionKind.BedUse, animals.Kinds);
        Assert.Contains(ActionKind.Building, animals.Kinds);

        var ideology = ClockPlannerCatalog.FamilyEntry(new FamilyRunner<IdeologyFacts>(new IdeologyFamily(), (_, _) => Task.FromResult(new IdeologyFacts()), IdeologyReader.Schedule));
        Assert.Equal("ideology", ideology.Name);
        Assert.Contains(ActionKind.Assign, ideology.Kinds);
        Assert.Contains(ActionKind.Ritual, ideology.Kinds);
        Assert.Contains(ActionKind.IdeoligionReform, ideology.Kinds);

        Assert.DoesNotContain(ClockPlannerCatalog.Entries, e => e.Name is "animalContainment" or "husbandry" or "ideoRoles" or "rituals" or "ideoligion");
    }

    [Fact]
    public void HusbandryRitualsAndReformsClaimTheirSubjects()
    {
        var plan = new PlanId("p");
        DomainAction[] actions =
        [
            DomainAction.NewHusbandryAction(new("p-0"), new Husbandry(new("cow"), HusbandryMethod.Slaughter, "")),
            DomainAction.NewRitualAction(new("p-1"), new Ritual(new("a"), RitualKind.Bestowing, RitualVerb.Start)),
            DomainAction.NewIdeoligionReformAction(new("p-2"), new IdeoligionReform("ideo-1", Current, Target, 0)),
        ];
        var claims = MethodClaimsRules.MethodClaimsOf(new WorkAdmissionEvidence(), [.. actions.Select(a => new WorkAction(plan, a, new ProgressView { Action = a.Id, Stage = Stage.Pending }))]);
        Assert.Contains("cow", claims.Targets);
        Assert.Contains("ritual/bestowing", claims.Targets);
        Assert.Contains("ideoligion/ideo-1", claims.Targets);
    }

    [Fact]
    public void ThePendingReformIsReadBackAfterASaveAndReload()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new StoreWorld(World.Colony, World.Load, World.Map);
        var intent = new IdeoligionReform("ideo-1", Current, Target, 2);
        using var source = OpenJournal("reform");
        Assert.Null(source.LoadIdeologyReform(world, ct));
        source.SaveIdeologyReform(new IdeologyReformRecord { World = world, Reform = intent }, ct);
        var saved = source.GovernorStateBlobs(ct);
        Assert.Contains(Journal.GovernorIdeologyReformKey, saved.Keys);

        using var loaded = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-reform-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        loaded.RebuildOwners(null, ct);
        loaded.RebuildFamilies(saved, ct);
        Assert.Equal(intent, loaded.LoadIdeologyReform(world with { Load = new("load-2") }, ct)?.Reform);

        // Another map's record is not this map's, and a cleared record leaves the family to re-derive.
        Assert.Null(loaded.LoadIdeologyReform(world with { Map = new(2) }, ct));
        loaded.ClearIdeologyReform(ct);
        Assert.DoesNotContain(Journal.GovernorIdeologyReformKey, loaded.GovernorStateBlobs(ct).Keys);
    }

    [Fact]
    public void AnOlderSavesReformStandardIsReadOnceAndNotRestoredAsAStandard()
    {
        var ct = TestContext.Current.CancellationToken;
        using var source = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-legacy-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        source.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);
        var saved = new Dictionary<string, string>(source.GovernorStateBlobs(ct));
        var key = Journal.LegacyStandardKeyPrefix + "std-reform";

        // The ImproveIdeoligion row an older save holds, its Record the Go-named intent.
        var blob = LegacyStandardBlob.Of("std-reform", "ImproveIdeoligion", World);
        blob["standard"]!["Record"] = new JsonObject
        {
            ["Ideo"] = "ideo-1",
            ["Expected"] = JsonNode.Parse(StoreJson.Serialize(Current)),
            ["Target"] = JsonNode.Parse(StoreJson.Serialize(Target)),
            ["Count"] = 4,
        }.ToJsonString();
        saved[key] = blob.ToJsonString();

        using var loaded = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-legacy-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        loaded.RebuildOwners(null, ct);
        loaded.RebuildFamilies(saved, ct);
        var world = new StoreWorld(World.Colony, World.Load, World.Map);
        Assert.Equal(new IdeoligionReform("ideo-1", Current, Target, 4), loaded.LoadIdeologyReform(world, ct)?.Reform);
        Assert.DoesNotContain(key, loaded.GovernorStateBlobs(ct).Keys);
        Assert.Contains(Journal.GovernorIdeologyReformKey, loaded.GovernorStateBlobs(ct).Keys);

        // A save without the Standard or the blob has no pending reform.
        saved.Remove(key);
        saved.Remove(Journal.GovernorIdeologyReformKey);
        loaded.RebuildFamilies(saved, ct);
        Assert.Null(loaded.LoadIdeologyReform(world, ct));
    }
}
