using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The mech planners' choices (#1692; the planner claims moved here from the deleted child/mech-gestation acceptance
/// case, #3402): a mech is owed while a mechanitor's free bandwidth buys an offered kind (MechGestationOwed), the
/// gestator gets one single-count bill for a kind that fits (SelectMechGestationBill), and a work mech's group runs
/// the catalog's work mode while a combat mech's escorts (PlanMechControl), over hand-built reads.
/// </summary>
public sealed class MechGestationPlanningTests
{
    private static readonly PawnId Mechanitor = new("mechanitor");

    private static readonly MechCatalog Catalog = new()
    {
        Kinds = new Dictionary<string, MechKind>
        {
            ["Mech_Lifter"] = new() { Name = "Mech_Lifter", WorkMech = true, WorkTypes = [WorkType.Hauling], BandwidthCost = 1, CombatPower = 10 },
            ["Mech_Militor"] = new() { Name = "Mech_Militor", WorkMech = false, BandwidthCost = 1, CombatPower = 50 },
            ["Mech_Centurion"] = new() { Name = "Mech_Centurion", WorkMech = false, BandwidthCost = 6, CombatPower = 400 },
        },
        // Deliberately not the game's def names: the planner must take the modes from the catalog rows.
        Work = "RoleWork",
        Escort = "RoleEscort",
        Recharge = "RoleRecharge",
    };

    private static MechanitorInput Overseer(int total, int used, int groups = 1) => new()
    {
        Id = Mechanitor,
        Mechanitor = new PawnMechanitor
        {
            TotalBandwidth = total,
            UsedBandwidth = used,
            GestationBandwidth = 0,
            ControlGroups = groups,
            ControlledMechs = [],
        },
    };

    /// <summary>An idle gestator with no waste, a ready charger and nothing on the floor.</summary>
    private static MechGestation Gestation(MechanitorInput m) => new()
    {
        Catalog = Catalog,
        Mechanitors = [m],
        Gestators = [new GestatorFact { Active = false, WasteCount = 0 }],
        Chargers = [new MechCharger { Powered = true, FullOfWaste = false, Charging = false }],
    };

    private static ProductionRecipe Recipe(string kind) => new() { Name = "Gestate" + kind, MechKind = kind, Available = true, Food = new RecipeFood.NotFood() };

    private static ProductionBench Gestator(params ProductionRecipe[] recipes) => new()
    {
        Id = "gestator",
        Definition = "MechGestator",
        Token = "gestator-token",
        Usable = true,
        Recipes = recipes,
    };

    [Fact]
    public void FreeBandwidthOwesAMech()
    {
        Assert.True(MechRules.MechGestationOwed(Gestation(Overseer(6, 0))));
    }

    [Fact]
    public void UsedUpBandwidthOwesNoMech()
    {
        Assert.False(MechRules.MechGestationOwed(Gestation(Overseer(6, 6))));
    }

    [Fact]
    public void GestatorGetsOneSingleCountBillForAMechThatFits()
    {
        // Two bandwidth free: the centurion (6) does not fit, the militor (1) does.
        var bench = Gestator(Recipe("Mech_Centurion"), Recipe("Mech_Militor"));

        var (selection, gap) = MechRules.SelectMechGestationBill([bench], Gestation(Overseer(6, 4)));

        Assert.Equal(BillGap.None, gap);
        Assert.Equal("gestator", selection.Bench);
        Assert.Equal("GestateMech_Militor", selection.Recipe);
        Assert.Equal(BillMode.GearBatch, selection.Mode);
        Assert.Equal(1, selection.Target);
    }

    [Fact]
    public void NoBillWhenNothingFitsTheFreeBandwidth()
    {
        var bench = Gestator(Recipe("Mech_Centurion"));

        var (selection, gap) = MechRules.SelectMechGestationBill([bench], Gestation(Overseer(6, 6)));

        Assert.Equal(BillGap.NothingWanted, gap);
        Assert.Equal("", selection.Recipe);
    }

    [Fact]
    public void WorkMechWorksAndCombatMechEscortsInTheCatalogModes()
    {
        static MechInput Mech(string id, string kind) => new()
        {
            Id = new PawnId(id),
            Kind = kind,
            Mech = new PawnMech { Overseer = Mechanitor.Value, Group = new PawnMechGroup(0, 0.1, 1), WorkMode = "SelfShutdown", Energy = new MechEnergy.Level(1) },
            Place = new RimGovernor.Contracts.Wire.Observations.EntityPlace.Unplaced(),
        };
        var lifter = Mech("lifter", "Mech_Lifter");
        var militor = Mech("militor", "Mech_Militor");

        var plan = MechRules.PlanMechControl(Catalog, [Overseer(6, 2, groups: 2)], [lifter, militor], hostile: false, chargerReady: false);

        Assert.Equal(
            [
                PawnSettings.NewMechControlGroupSetting(militor.Id, 1),
                PawnSettings.NewMechWorkModeSetting(lifter.Id, "RoleWork"),
                PawnSettings.NewMechWorkModeSetting(militor.Id, "RoleEscort"),
            ],
            plan
        );
    }

    [Fact]
    public void AGuardOnTheMapAttacksTheNearestHostileAndAnUnplacedOneIsLeftAlone()
    {
        static MechInput Guard(string id, RimGovernor.Contracts.Wire.Observations.EntityPlace place) => new()
        {
            Id = new PawnId(id),
            Kind = "Mech_Militor",
            Mech = new PawnMech { Overseer = Mechanitor.Value, Group = new PawnMechGroup(0, 0.1, 1), WorkMode = "RoleEscort", Energy = new MechEnergy.Level(1) },
            Place = place,
        };
        var onMap = Guard("guard-a", new RimGovernor.Contracts.Wire.Observations.EntityPlace.OnMap(0, new RimGovernor.Contracts.Wire.Common.Cell { X = 10, Z = 10 }));
        var unplaced = Guard("guard-b", new RimGovernor.Contracts.Wire.Observations.EntityPlace.Unplaced());
        var overseers = new Dictionary<string, Cell> { [Mechanitor.Value] = new Cell(10, 12) };
        MechHostile[] hostiles = [new() { Id = new PawnId("far"), Cell = new Cell(20, 10) }, new() { Id = new PawnId("near"), Cell = new Cell(12, 10) }];

        var plan = MechRules.PlanMechGuards(Catalog, overseers, [onMap, unplaced], hostiles);

        Assert.Equal([onMap.Id], plan.Drafts);
        var order = Assert.Single(plan.Orders);
        Assert.Equal((onMap.Id, new PawnId("near")), (order.Pawn, order.Target));

        // An overseer off the map (no cell) orders none of its guards.
        Assert.Empty(MechRules.PlanMechGuards(Catalog, new Dictionary<string, Cell>(), [onMap], hostiles).Orders);
    }
}
