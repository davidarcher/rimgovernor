using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class VoluntaryMethodAdmissionTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    [Theory]
    [InlineData("repair")]
    [InlineData("clean")]
    [InlineData("haul")]
    [InlineData("refuel")]
    [InlineData("ritual")]
    [InlineData("ignite")]
    [InlineData("equip")]
    [InlineData("apparel")]
    [InlineData("gathering")]
    [InlineData("neuroformer")]
    [InlineData("drop")]
    [InlineData("movement")]
    [InlineData("wall-replacement")]
    [InlineData("capture")]
    [InlineData("rescue")]
    [InlineData("tend")]
    [InlineData("gift")]
    [InlineData("comms")]
    [InlineData("formation")]
    [InlineData("shuttle-explicit")]
    [InlineData("shuttle-autoload")]
    public void AnotherStandardCannotCommandeerPawnReservedByCoupledMethod(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-voluntary-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var review = journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") with { PsylinkOwed = Fact.Known(true) } }, ct);
        var labor = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainWorkLedger);
        var other = Assert.Single(review.Standards, s => s.Standard.Kind == (kind == "neuroformer" ? PolicyConstants.MaintainPsylink : MaintainArt));
        var held = new PlanSpec(new("coupled-plan"), new(1),
            [DomainAction.NewPawnActivitiesAction(new("coupled-action"), new(1,
                [new("pawn", "assignment", "coupled-method", 0, 1, false,
                    [new(PawnActivityKind.Idle, "", "", "", 0, 500)])]))]);
        journal.CommitMethod(labor.Standard.Id, labor.Revision, new("coupled-method"), held, ct);
        var direct = new PlanSpec(new("direct-plan"), new(1), kind == "movement"
            ? [DomainAction.NewOwnedDraftAction(new("draft"), new(new("pawn"))), Direct(kind)] : [Direct(kind)]);
        var error = Assert.Throws<NotAdmittedException>(() =>
            journal.CommitMethod(other.Standard.Id, other.Revision, new("direct-method"), direct, ct));
        Assert.Contains("unsupported_voluntary_method:", error.Message);
        Assert.Empty(journal.LoadOwnerMethods(other, ct));
        Assert.Equal(other.Revision, journal.LoadStandard(other.Standard.Id, ct).Revision);
        Assert.False(journal.LoadPlan(held.Id, ct).Retired);
        Assert.Contains("pawn", journal.WorkResources(World, ct).Claims.Pawns);
    }

    [Theory]
    [InlineData(RecoveryMethod.Repair)]
    [InlineData(RecoveryMethod.Breakdown)]
    [InlineData(RecoveryMethod.Refuel)]
    public void SeparatelyOwnedIncidentRecoveryRetainsItsAdmission(RecoveryMethod method)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-incident-recovery-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var incident = journal.OpenIncident(new(RecoverDisasterServices, new("colony"), "observed_disaster",
            World, new(100), []), ct);
        var plan = new PlanSpec(new("emergency-plan"), new(1),
            [DomainAction.NewRecoveryServiceAction(new("emergency-action"), new(new("pawn"), "building", method))]);
        var committed = journal.CommitIncidentMethod(incident.Incident.Id, new("emergency-method"), "", plan, ct);
        Assert.Equal(plan.Id, Assert.Single(committed.Methods).Plan);
    }

    [Theory]
    [InlineData(CaravanDepartureMode.ExistingRoute)]
    [InlineData(CaravanDepartureMode.SiteReform)]
    public void ReturnIntentRetainsExactModeAcrossJournal(CaravanDepartureMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-return-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var review = journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainArt);
        var departure = new CaravanDeparture([new("pawn")], [], 2, mode);
        var plan = new PlanSpec(new("return-plan"), new(1),
            [DomainAction.NewCaravanDepartureAction(new("return-action"), departure)]);
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new("return-method"), plan, ct);
        Assert.Equal(mode, Assert.Single(journal.LoadPlan(plan.Id, ct).Spec.Actions).CaravanDeparture!.Mode);
    }
    private static DomainAction Direct(string kind) => kind switch
    {
        "repair" => DomainAction.NewRepairAction(new("direct-action"), new(new("pawn"), "building", new(1, 1))),
        "clean" => DomainAction.NewCleanAction(new("direct-action"), new(new("pawn"), "filth", new(1, 1))),
        "haul" => DomainAction.NewHaulAction(new("direct-action"), new(new("pawn"), "stack", "Rice", new(1, 1))),
        "refuel" => DomainAction.NewRecoveryServiceAction(new("direct-action"), new(new("pawn"), "building", RecoveryMethod.Refuel)),
        "ritual" => DomainAction.NewRitualAction(new("direct-action"), new(new("pawn"), RitualKind.Bestowing, RitualVerb.Start)),
        "ignite" => DomainAction.NewIgniteAction(new("direct-action"), new(new("pawn"), new(1, 1))),
        "equip" => DomainAction.NewEquipAction(new("direct-action"), new(new("pawn"), "weapon", "Gun_Revolver", new(1, 1))),
        "apparel" => DomainAction.NewGearReplaceAction(new("direct-action"), new(new("pawn"), "shirt", "Apparel_BasicShirt")),
        "gathering" => DomainAction.NewGatheringAction(new("direct-action"), new("Party", new("pawn"))),
        "neuroformer" => DomainAction.NewUseItemAction(new("direct-action"), new(new("pawn"), "neuroformer", new("pawn"))),
        "drop" => DomainAction.NewDropEquipmentAction(new("direct-action"), new(new("pawn"), "weapon")),
        "movement" => DomainAction.NewMovementAction(new("direct-action"), new(new("pawn"), new(1, 1), new("draft"))),
        "wall-replacement" => DomainAction.NewDeconstructionAction(new("direct-action"), new Deconstruction("door", "Door", new(1, 1)).WithWallReplacement()),
        "capture" => DomainAction.NewCaptureAction(new("direct-action"), new(new("pawn"), new("patient"))),
        "rescue" => DomainAction.NewRescueAction(new("direct-action"), new(new("pawn"), new("patient"))),
        "tend" => DomainAction.NewTendAction(new("direct-action"), new(new("pawn"), new("patient"))),
        "gift" => DomainAction.NewGiveItemAction(new("direct-action"), new(new("pawn"), new("recipient"), "Rice", 1)),
        "comms" => DomainAction.NewCommsTradeRequestAction(new("direct-action"), new(TradeRequestKind.Caravan, "faction", "trader", "console", "pawn", 0)),
        "formation" => DomainAction.NewCaravanDepartureAction(new("direct-action"), new([new("pawn")], [], 1)),
        "shuttle-explicit" => DomainAction.NewQuestShuttleAction(new("direct-action"), new(new("quest"), ShuttleLoading.ExplicitPawns, false, [new("pawn")], false)),
        "shuttle-autoload" => DomainAction.NewQuestShuttleAction(new("direct-action"), new(new("quest"), ShuttleLoading.Autoload, true, [], false)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}