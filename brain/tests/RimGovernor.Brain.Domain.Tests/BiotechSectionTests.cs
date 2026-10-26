using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The Biotech colony section is total (#3773): a named state (<see cref="RimGovernor.Brain.GameFacts.Observations.BiotechSection.Active"/> or
/// <see cref="RimGovernor.Brain.GameFacts.Observations.BiotechSection.Inactive"/> without Biotech), every member <c>required</c>, a bill, a charging mech
/// and a pack's bank are relations (null for none), and it round-trips through <c>OrdersJsonContext</c> exactly. An
/// inactive DLC section derives no Needs from that expansion.
/// </summary>
public sealed class BiotechSectionTests
{
    /// <summary>The section of a game without Biotech: the section every test without Biotech carries.</summary>
    internal static readonly RimGovernor.Brain.GameFacts.Observations.BiotechSection NoBiotech = new RimGovernor.Brain.GameFacts.Observations.BiotechSection.Inactive();

    internal static readonly RimGovernor.Brain.GameFacts.Observations.BiotechColonyFacts Colony = new()
    {
        PollutedUncoveredCells = 7,
        Wastepacks = [new RimGovernor.Brain.GameFacts.Observations.Wastepack { ThingId = "Thing_Wastepack1", DefName = "Wastepack", Position = new Cell(3, 4), Count = 5, Frozen = false, InAtomizer = false, Forbidden = true }],
        Atomizers = [new RimGovernor.Brain.GameFacts.Observations.WastepackAtomizer { Position = new Cell(8, 9) }],
        Gestators = [new RimGovernor.Brain.GameFacts.Observations.MechGestatorState { BillId = "Bill_Mech1", WasteCount = 2 }, new RimGovernor.Brain.GameFacts.Observations.MechGestatorState { BillId = null, WasteCount = 0 }],
        Chargers = [new RimGovernor.Brain.GameFacts.Observations.MechChargerState { Powered = true, FullOfWaste = false, ChargingMechId = null }],
        GeneBanks = [new RimGovernor.Brain.GameFacts.Observations.GeneBankState { Position = new Cell(1, 1), Capacity = 4, PackIds = ["Thing_Genepack1"] }],
        GeneAssemblers = [new RimGovernor.Brain.GameFacts.Observations.GeneAssemblerState { Position = new Cell(2, 1) }],
        Genepacks = [new RimGovernor.Brain.GameFacts.Observations.GenepackState { BankId = "Thing_GeneBank1" }, new RimGovernor.Brain.GameFacts.Observations.GenepackState { BankId = null }],
    };

    [Fact]
    public void SectionJsonRoundTripIsExact()
    {
        RimGovernor.Brain.GameFacts.Observations.BiotechSection section = new RimGovernor.Brain.GameFacts.Observations.BiotechSection.Active(Colony);

        var json = OrdersJson.Serialize(section);

        Assert.Equal(
            """{"type":"active","facts":{"pollutedUncoveredCells":7,"wastepacks":[{"thingId":"Thing_Wastepack1","defName":"Wastepack","position":{"x":3,"z":4},"count":5,"frozen":false,"inAtomizer":false,"forbidden":true}],"atomizers":[{"position":{"x":8,"z":9}}],"gestators":[{"billId":"Bill_Mech1","wasteCount":2},{"billId":null,"wasteCount":0}],"chargers":[{"powered":true,"fullOfWaste":false,"chargingMechId":null}],"geneBanks":[{"position":{"x":1,"z":1},"capacity":4,"packIds":["Thing_Genepack1"]}],"geneAssemblers":[{"position":{"x":2,"z":1}}],"genepacks":[{"bankId":"Thing_GeneBank1"},{"bankId":null}]}}""",
            json);
        Assert.Equal(section, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.BiotechSection>(json));
        Assert.Equal("""{"type":"inactive"}""", OrdersJson.Serialize(NoBiotech));
        Assert.Equal(NoBiotech, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.BiotechSection>("""{"type":"inactive"}"""));
    }

    [Fact]
    public void PawnBlockJsonRoundTripIsExact()
    {
        RimGovernor.Brain.GameFacts.Observations.PawnBiotechState state = new RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active(new RimGovernor.Brain.GameFacts.Observations.PawnBiotech
        {
            DevelopmentalStage = RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Adult,
            Genes = [new RimGovernor.Brain.GameFacts.Observations.PawnGene { DefName = "Hair_Black", Active = true }],
            Mechanitor = null,
            Mech = new RimGovernor.Brain.GameFacts.Observations.PawnMech
            {
                Overseer = null,
                WorkMode = "Work",
                Energy = new RimGovernor.Brain.GameFacts.Observations.PawnMechEnergy.Level { Fraction = 0.5 },
                Group = new RimGovernor.Brain.GameFacts.Observations.PawnMechGroup { Index = 1, RechargeBelow = 0.1, RechargeAbove = 1 },
            },
            Deathrest = new RimGovernor.Brain.GameFacts.Observations.PawnDeathrest { Deathresting = false, Capacity = 2 },
        });

        var json = OrdersJson.Serialize(state);

        Assert.Equal(
            """{"type":"active","facts":{"developmentalStage":"DEVELOPMENTAL_STAGE_ADULT","genes":[{"defName":"Hair_Black","active":true}],"mechanitor":null,"mech":{"overseer":null,"workMode":"Work","energy":{"type":"level","fraction":0.5},"group":{"index":1,"rechargeBelow":0.1,"rechargeAbove":1}},"deathrest":{"deathresting":false,"capacity":2}}}""",
            json);
        Assert.Equal(state, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.PawnBiotechState>(json));

        var lifted = new PawnBiotech { Observed = ((RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active)state).Facts };
        Assert.Equal(new RimGovernor.Brain.GameFacts.Observations.PawnMechGroup { Index = 1, RechargeBelow = 0.1, RechargeAbove = 1 }, lifted.Mech!.Group);
        Assert.Equal(new RimGovernor.Brain.GameFacts.Observations.PawnMechEnergy.Level { Fraction = 0.5 }, lifted.Mech.Energy);
        Assert.Null(lifted.Mechanitor);
        Assert.Equal(2, lifted.Deathrest!.Capacity);
    }

    [Fact]
    public void AnActiveSectionLiftsWholeIntoThePolicyInputs()
    {
        var pollution = PollutionRules.PollutionOf(Colony);
        Assert.Equal(7u, pollution.UncoveredCells);
        Assert.True(Assert.Single(pollution.Wastepacks).Forbidden);
        Assert.Equal([true, false], Colony.Gestators.Select(g => g.BillId is not null));
        Assert.Null(Assert.Single(Colony.Chargers).ChargingMechId);
        var gestation = new MechGestation { Gestators = Colony.Gestators, Wastepacks = Colony.Wastepacks, Chargers = Colony.Chargers };
        Assert.Same(Colony.Gestators[0], gestation.Gestators[0]);
        Assert.Same(Colony.Wastepacks[0], gestation.Wastepacks[0]);
        Assert.Same(Colony.Chargers[0], gestation.Chargers[0]);
        Assert.True(MechRules.UnclearedWaste(gestation));

        // One bank of four holds one pack: three free slots hold the one loose pack, so no bank is owed; four loose packs overflow it.
        Assert.False(GeneBankConcernRules.GeneBankOwed(Colony));
        Assert.True(GeneBankConcernRules.GeneBankOwed(Colony with { Genepacks = [.. Enumerable.Repeat(new RimGovernor.Brain.GameFacts.Observations.GenepackState { BankId = null }, 4)] }));

        // A charger is owed only with a mechanitor and every charger busy and clear of waste.
        Assert.False(MechRules.MechChargerOwed(1, Colony.Chargers));
        var busy = Colony with { Chargers = [new RimGovernor.Brain.GameFacts.Observations.MechChargerState { Powered = true, FullOfWaste = false, ChargingMechId = "Thing_Mech1" }] };
        Assert.True(MechRules.MechChargerOwed(1, busy.Chargers));
        Assert.False(MechRules.MechChargerOwed(0, busy.Chargers));
    }

    [Fact]
    public void InactiveDlcSectionsDeriveNoNeeds()
    {
        Assert.Empty(PollutionRules.PollutionAllowSupplies(NoBiotech));
        Assert.Equal("Thing_Wastepack1", Assert.Single(PollutionRules.PollutionAllowSupplies(new RimGovernor.Brain.GameFacts.Observations.BiotechSection.Active(Colony))).Thing);

        // Every DLC section of the core snapshot is inactive: no expansion counts as installed, and no family derives.
        var facts = DlcSectionReads.Installed(new ReviewFacts(), ColonyFactsCoreTests.Snapshot());
        Assert.Equal((false, false, false), (facts.Royalty is RoyaltyState.Active, facts.Anomaly is AnomalyState.Active, facts.Biotech is RimGovernor.Brain.GameFacts.Observations.BiotechSection.Active));
        Assert.IsType<RimGovernor.Brain.GameFacts.Observations.BiotechSection.Inactive>(new ReviewFacts().Biotech);
        Assert.Empty(new BiotechFamily(new BiotechNeeds { Mechs = true, MechCharger = true, GeneBank = true, Pollution = true })
            .Derive(new BiotechFacts.Inactive()));
        Assert.Empty(new RoyaltyFamily().Derive(new RoyaltyFamilyFacts { Royalty = facts.Royalty, PsylinkOwed = false }));
        Assert.Empty(new AnomalyFamily().Derive(AnomalyFacts.From(facts)));

        var active = DlcSectionReads.Installed(new ReviewFacts(), ColonyFactsCoreTests.Snapshot() with { Biotech = new RimGovernor.Brain.GameFacts.Observations.BiotechSection.Active(Colony) });
        Assert.Equal(new RimGovernor.Brain.GameFacts.Observations.BiotechSection.Active(Colony), active.Biotech);
    }
}
