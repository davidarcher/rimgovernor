using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3965: native worker families priced through the production joiner mood entry point.</summary>
public sealed class JoinerSituationTests
{
    private sealed record TraitColonist(string Pawn, bool Awake, double LocalHour, PawnTraitSituation TraitSituation);
    private static JoinerSituationThought Row(JoinerSituationKind kind, double offset, bool invert = false, int limit = 10) =>
        new(kind.ToString(), kind, true, invert, limit, [offset]);
    private static TraitColonist Colonist(PawnTraitSituation? situation = null, double hour = 12, bool awake = true) =>
        new("pawn", awake, hour, situation ?? new(false, 0, false, false, false, false, 0));
    private static double Mood(JoinerSituationThought[] rows, params TraitColonist[] colonists) =>
        JoinerMoodRules.MoodOf(new(.5, 0, .35)
        {
            Traits = [], RoomTraits = [], SituationStages = EquatableArray.From(rows), SituationalTraits = [], Memories = [],
        }, new Dictionary<string, double>(), [NeedsFixtures.Pawn()], Selected(rows, colonists))!.Target;

    private static double Selected(JoinerSituationThought[] rows, params TraitColonist[] colonists)
    {
        var read = SelectedRead(colonists);
        return JoinerMoodRules.SelectedSituationTraits(rows, read.Outcome, [], read.Context, 1, new SortedSet<string>());
    }

    private static (ForecastCoupledContext Context, ForecastCoupledOutcome Outcome) SelectedRead(params TraitColonist[] colonists)
    {
        var pawns = new List<NeedsForecastPawn>();
        var outfits = new Dictionary<string, EquatableArray<GearOption>>();
        var needs = new Dictionary<string, ForecastNeedsResult>();
        foreach (var colonist in colonists.DistinctBy(c => c.Pawn))
        {
            var s = colonist.TraitSituation;
            var cell = new NeedCell(s.Indoor, false, s.NaturalRoof, !s.Indoor, !s.Indoor) { NearbyFires = new NeedNearbyFires.Counted(s.Fires) };
            var target = NeedsFixtures.Service("target", colonist.Awake ? "joy" : "rest", 0, 1) with { Cell = cell };
            var pawn = NeedsFixtures.Pawn(colonist.Pawn) with { LocalHour = colonist.LocalHour, Awake = colonist.Awake,
                Targets = [target], Needs = [NeedsFixtures.Rate("rest", 0, 0, .3)] };
            pawns.Add(pawn);
            var worn = new List<GearOption>();
            for (var i = 0; i < Math.Max(s.HumanLeather, s.Clothed ? 1 : 0); i++)
                worn.Add(new() { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("apparel" + i, "garment",
                    i < s.HumanLeather ? PreferenceRules.HumanLeather.Value : "", GearSource.Worn)), Slot = GearSlot.SkinTorso,
                    CountsAsClothingForNudity = s.Clothed, Groups = ["Torso"] });
            if (s.RangedWeapon || s.IncendiaryWeapon)
                worn.Add(new() { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("weapon", "weapon", "", GearSource.Worn)), Slot = GearSlot.Primary,
                    Weapon = new() { TraitRanged = s.RangedWeapon, TraitIncendiary = s.IncendiaryWeapon } });
            outfits[colonist.Pawn] = EquatableArray.From(worn);
            needs[colonist.Pawn] = new([], new ForecastTerm.Unassessed("unused"), new ForecastTerm.Unassessed("unused"),
                new(new(colonist.Pawn, "", new ForecastCapacityWindows.Unread(), 0), []), [])
                { Services = [new("service", colonist.Pawn, target.Kind, target.Id, "", 0, 0, 0, 1, 1, [])] };
        }
        var context = ForecastValueTests.Context(NeedsFixtures.Pawn(), outfits: outfits) with { Needs = NeedsFixtures.Read(0, [.. pawns]) };
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), true, needs, [])
        {
            Terminal = new(new() { Origin = new(0), HorizonDays = 1d / GameTime.TicksPerDay, Coupled = context },
                new Dictionary<string, ForecastCommitment>(), new Dictionary<Resource, double>(), new Dictionary<string, double>(), null, new Dictionary<Resource, double>()),
        };
        return (context, outcome);
    }

    [Fact]
    public void SelectedOutfitTakesEffectAtItsOwnCommitmentTick()
    {
        var read = SelectedRead(Colonist());
        var shirt = new GearOption { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("shirt", "shirt", "", GearSource.Stored)),
            Slot = GearSlot.SkinTorso, Groups = ["Torso"], CountsAsClothingForNudity = true };
        ForecastCommitment[] changes = [new("wear", default) { ActivityCompletions = ["deliver"], OutfitChanges = [new("pawn", [shirt])] }];
        var outcome = read.Outcome with { OutfitTicks = [new("wear", .5)] };
        var findings = new SortedSet<string>();
        Assert.Equal(10, JoinerMoodRules.SelectedSituationTraits([Row(JoinerSituationKind.Nude, 20)], outcome, changes, read.Context, 1, findings), 12);
        Assert.Empty(findings);
    }

    [Fact]
    public void TransitAndIdleDoNotInheritTheObservedRoofOrFire()
    {
        var read = SelectedRead(Colonist(new(false, 0, false, false, true, true, 4)));
        var outcome = read.Outcome with { Needs = read.Outcome.Needs.ToDictionary(p => p.Key, p => p.Value with { Services = [] }) };
        var findings = new SortedSet<string>();
        Assert.Equal(0, JoinerMoodRules.SelectedSituationTraits([Row(JoinerSituationKind.Flames, 2), Row(JoinerSituationKind.Underground, 3),
            Row(JoinerSituationKind.Day, -10)], outcome, [], read.Context, 1, findings));
        Assert.Contains("joiner_trait_location_unprojected", findings);
        Assert.Contains("joiner_trait_awake_unprojected", findings);
    }

    [Fact]
    public void SelectedClosureChangesEnclosureAndInvalidatesTheOldSameRoomFireCount()
    {
        var read = SelectedRead(Colonist());
        var pawn = read.Context.Needs.Pawns[0];
        var target = pawn.Targets[0] with { Cell = new(true, false, false, true, true) { NearbyFires = new NeedNearbyFires.Counted(2) } };
        var context = read.Context with
        {
            Needs = read.Context.Needs with { Pawns = [pawn with { Targets = [target] }] },
            Closures = [new(1, "", [target.Location], [], [], PlannedRoomPredictionTests.Closed("1", "", "Bedroom", 10, 1, 9, 0, false))
                { Placed = [new("wall", ["finish"])] }],
        };
        var outcome = read.Outcome with
        {
            Terminal = new(new() { Origin = new(0), HorizonDays = 1d / GameTime.TicksPerDay, Coupled = context },
                new Dictionary<string, ForecastCommitment>(), new Dictionary<Resource, double>(), new Dictionary<string, double> { ["finish"] = .5 }, null,
                new Dictionary<Resource, double>()),
        };
        var findings = new SortedSet<string>();
        Assert.Equal(3, JoinerMoodRules.SelectedSituationTraits([Row(JoinerSituationKind.Indoors, 2), Row(JoinerSituationKind.Flames, 2)],
            outcome, [], context, 1, findings), 12);
        Assert.Contains("joiner_trait_location_unprojected", findings);
    }

    [Fact]
    public void RetainedGroundSleepUsesItsActualNativeCell()
    {
        var read = SelectedRead(Colonist(awake: false));
        var pawn = read.Context.Needs.Pawns[0] with { Targets = [],
            LyingIn = new(new("", true, true, 1, 0, 20), false, new LyingInDeathrest.None())
                { DeathrestWakeStage = -1, Cell = new(false, false, false, true, true) { NearbyFires = new NeedNearbyFires.Counted(2) } } };
        var context = read.Context with { Needs = read.Context.Needs with { Pawns = [pawn] } };
        var outcome = read.Outcome with { Needs = read.Outcome.Needs.ToDictionary(p => p.Key,
            p => p.Value with { Services = [p.Value.Services[0] with { Target = "" }] }) };
        var findings = new SortedSet<string>();
        Assert.Equal(4, JoinerMoodRules.SelectedSituationTraits([Row(JoinerSituationKind.Flames, 2)], outcome, [], context, 1, findings), 12);
        Assert.Empty(findings);
    }

    [Fact]
    public void ApparelUsesColonistNudityAndHumanLeatherCountAtJoinerStages()
    {
        var rows = new[] { Row(JoinerSituationKind.Nude, -3, invert: true),
            new JoinerSituationThought("leather", JoinerSituationKind.HumanLeather, true, false, 1, [2, 4, 6, 8]) };
        Assert.Equal(.5, Mood(rows, Colonist()), 12);
        Assert.Equal(.5 + .05, Mood(rows, Colonist(new(true, 7, false, false, false, false, 0))), 12);
        Assert.Equal(.5 + .01, Mood(rows, Colonist(new(true, 2, false, false, false, false, 0))), 12);
    }

    [Fact]
    public void WeaponTraitsReadColonistRangedAndIncendiaryPrimary()
    {
        var rows = new[] { Row(JoinerSituationKind.RangedWeapon, -20), Row(JoinerSituationKind.IncendiaryWeapon, 5) };
        Assert.Equal(.5, Mood(rows, Colonist()), 12);
        Assert.Equal(.35, Mood(rows, Colonist(new(false, 0, true, true, false, false, 0))), 12);
        Assert.Equal(.55, Mood(rows, Colonist(new(false, 0, false, true, false, false, 0))), 12);
    }

    [Fact]
    public void PyromaniacUsesSameRoomFireCountAndDefsStackLimit()
    {
        var rows = new[] { Row(JoinerSituationKind.Flames, 2, limit: 3) };
        Assert.Equal(.5, Mood(rows, Colonist()), 12);
        Assert.Equal(.56, Mood(rows, Colonist(new(false, 0, false, false, false, false, 7))), 12);
        Assert.Equal(.54, Mood(rows, Colonist(new(false, 0, false, false, false, false, 2))), 12);
    }

    [Fact]
    public void NightOwlUsesIntegerLocalHourAndAwakeState()
    {
        var rows = new[] { Row(JoinerSituationKind.Day, -10), Row(JoinerSituationKind.Night, 16) };
        Assert.Equal(.4, Mood(rows, Colonist(hour: 17.99)), 12);
        Assert.Equal(.5, Mood(rows, Colonist(hour: 18)), 12);
        Assert.Equal(.66, Mood(rows, Colonist(hour: 23)), 12);
        Assert.Equal(.66, Mood(rows, Colonist(hour: 5.99)), 12);
        Assert.Equal(.5, Mood(rows, Colonist(hour: 6)), 12);
        Assert.Equal(.5, Mood(rows, Colonist(hour: 12, awake: false)), 12);
    }

    [Fact]
    public void UndergrounderUsesAwakeRoofAndOutdoorTemperatureEnclosure()
    {
        var rows = new[] { Row(JoinerSituationKind.Indoors, 2), Row(JoinerSituationKind.Underground, 3), Row(JoinerSituationKind.Outdoors, -3) };
        Assert.Equal(.47, Mood(rows, Colonist()), 12);
        Assert.Equal(.52, Mood(rows, Colonist(new(false, 0, false, false, true, false, 0))), 12);
        Assert.Equal(.53, Mood(rows, Colonist(new(false, 0, false, false, true, true, 0))), 12);
        Assert.Equal(.5, Mood(rows, Colonist(awake: false)), 12);
    }

    [Fact]
    public void ChemicalDesireRetainsTheJoinersOwnNativeWorkerOffsets()
    {
        // OwnStateWorkers reads drugsDesire.MoodBuffForCurrentLevel; joining does not change that need.
        var mood = new JoinerMoodRead(.5, 0, .35)
        {
            Traits = [new("DrugDesireInterest", -6), new("DrugDesireInterestSatisfied", 0)],
            RoomTraits = [], SituationStages = [], SituationalTraits = [], Memories = [],
        };
        Assert.Equal(.44, JoinerMoodRules.MoodOf(mood, new Dictionary<string, double>(), [NeedsFixtures.Pawn()])!.Target, 12);
        Assert.Equal(.53, JoinerMoodRules.MoodOf(mood with { Traits = [new("DrugDesireInterest", 0), new("DrugDesireInterestSatisfied", 3)] },
            new Dictionary<string, double>(), [NeedsFixtures.Pawn()])!.Target, 12);
    }

    [Fact]
    public void VoidFascinationRetainsTheNativeHeldEntityStateOnTheSameMap()
    {
        // OwnStateWorkers invokes ThoughtWorker_VoidFascination (AnomalyActive and CompEntityHolder.HeldPawn).
        var mood = new JoinerMoodRead(.5, 0, .35)
        {
            Traits = [new("VoidFascination", 6)], RoomTraits = [], SituationStages = [], SituationalTraits = [], Memories = [],
        };
        Assert.Equal(.56, JoinerMoodRules.MoodOf(mood, new Dictionary<string, double>(), [NeedsFixtures.Pawn()])!.Target, 12);
        Assert.Equal(.5, JoinerMoodRules.MoodOf(mood with { Traits = [new("VoidFascination", 0)] },
            new Dictionary<string, double>(), [NeedsFixtures.Pawn()])!.Target, 12);
    }

    [Fact]
    public void SituationStagesReplaceColonyThoughtsAndTakeDistinctColonistsMedian()
    {
        var row = Row(JoinerSituationKind.Nude, 20);
        var mood = new JoinerMoodRead(.5, 0, .35)
        {
            Traits = [], RoomTraits = [], SituationStages = [row], SituationalTraits = [], Memories = [],
        };
        var naked = Colonist() with { Pawn = "naked" };
        var clothed = Colonist(new(true, 0, false, false, false, false, 0)) with { Pawn = "clothed" };
        Assert.Equal(.6, JoinerMoodRules.MoodOf(mood, new Dictionary<string, double> { [row.Thought] = 99 }, [NeedsFixtures.Pawn()], Selected([row], naked, clothed, clothed))!.Target, 12);
        Assert.Throws<RequiredFactException>(() => Mood([row]));
        Assert.Throws<RequiredFactException>(() => Mood([row with { Stages = [] }], naked));
        Assert.Equal(.5, Mood([row with { CanGet = false }], naked), 12);
    }
}
