using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared construction and activity-task reads the tests stand (#3775): every required member set, so a test names
/// only what it reads.
/// </summary>
internal static class ConstructionFixtures
{
    internal static ObservationContext Context(long tick = 0) =>
        new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Tick = tick, Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 } };

    /// <summary>A construction read that answered nothing: no rows, sites, removals, routes or predictions.</summary>
    internal static ConstructionActivityRead NoConstruction(long tick = 0) => new()
    {
        Context = Context(tick), Findings = [], Options = [], Planned = [], Removals = [], Routes = [], Thermal = null, RoomPredictions = [],
    };

    /// <summary>An activity-task read that bound nothing.</summary>
    internal static ActivityTaskRead NoTasks(long tick = 0) => new()
    {
        Context = Context(tick), Truncated = false, Bindings = [], Pawns = [], SatisfiedTasks = [], Needs = NeedsFixtures.Empty with { Tick = new(tick) },
    };

    /// <summary>A planned site's answer with no rows and no clearing.</summary>
    internal static PlannedSiteAnswer Answer(string site, PlannedSiteVerdict verdict = PlannedSiteVerdict.Feasible) =>
        new(site) { Verdict = verdict, Options = [], Clearing = [] };

    /// <summary>A clearing op with no rows, targets or cells.</summary>
    internal static PlannedSiteClearing Clearing(string id, PlannedClearingKind kind) =>
        new(id, kind) { Options = [], Targets = [], Cells = [] };

    /// <summary>The answered <paramref name="site"/> owing <paramref name="clearing"/>.</summary>
    internal static RimGovernor.Brain.Policy.PlannedBuildSite Cleared(this RimGovernor.Brain.Policy.PlannedBuildSite site, params PlannedSiteClearing[] clearing) =>
        site with { Answer = site.Answer! with { Clearing = EquatableArray.From(clearing) } };
}

/// <summary>
/// A construction row written the way the tests think of it: a pawn's legs as estimates, unknown where the pawn has none.
/// It converts to the wire row's named states: a known carry is <see cref="ConstructionDelivery.Carried"/>, a row with no
/// stack <see cref="ConstructionDelivery.Unsourced"/>, a pawn with no capacity <see cref="ConstructionDelivery.CannotCarry"/>
/// and any other <see cref="ConstructionDelivery.Unreachable"/>; a known finish <see cref="ConstructionFinish.Finishes"/>,
/// else <see cref="ConstructionFinish.CannotFinish"/>; an unread stack count is the row's Count.
/// </summary>
internal sealed record Row(string Site, string Definition, Cell Location, string Pawn,
    string Resource, string Input, Cell Origin, int Count, int Capacity, string WorkGiver,
    Fact<double> ApproachTicks, Fact<double> DeliveryTicks, Fact<double> FinishTicks)
{
    public ProductiveSkillWork? SkillWork { get; init; }
    public Fact<double> SiteApproachTicks { get; init; }
    public Fact<double> AvailableInput { get; init; }
    public Fact<double> StorageDistance { get; init; }

    public static implicit operator ConstructionActivityOption(Row row) => row.Option;

    internal ConstructionActivityOption Option => new(Site, Definition, Location, Pawn, Resource, Input, Origin, Count, Capacity, WorkGiver,
        SiteApproachTicks.IsKnown ? SiteApproachTicks.Value : 0,
        ApproachTicks.IsKnown && DeliveryTicks.IsKnown ? new ConstructionDelivery.Carried(ApproachTicks.Value, DeliveryTicks.Value)
            : Count == 0 || Input.Length == 0 ? new ConstructionDelivery.Unsourced()
            : Capacity <= 0 ? new ConstructionDelivery.CannotCarry()
            : new ConstructionDelivery.Unreachable(),
        FinishTicks.IsKnown ? new ConstructionFinish.Finishes(FinishTicks.Value, SkillWork) : new ConstructionFinish.CannotFinish(),
        AvailableInput.IsKnown ? (int)AvailableInput.Value : Count,
        StorageDistance.IsKnown ? new StackStorage.Accepted(StorageDistance.Value)
            : Input.Length == 0 ? new StackStorage.Unsourced() : new StackStorage.Unaccepted());
}
