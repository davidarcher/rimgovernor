using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The Biotech colony section is total (#3773): a named state (<see cref="O.BiotechSection.Active"/> or
/// <see cref="O.BiotechSection.Inactive"/> without Biotech), every member <c>required</c>, a bill, a charging mech
/// and a pack's bank are relations (null for none), and it round-trips through <c>WireJsonContext</c> exactly. An
/// inactive DLC section derives no Needs from that expansion.
/// </summary>
public sealed class BiotechSectionTests
{
    /// <summary>The section of a game without Biotech: the section every test without Biotech carries.</summary>
    internal static readonly O.BiotechSection NoBiotech = new O.BiotechSection.Inactive();

    internal static readonly O.BiotechColonyFacts Colony = new()
    {
        PollutedUncoveredCells = 7,
        Wastepacks = [new O.Wastepack { ThingId = "Thing_Wastepack1", DefName = "Wastepack", Position = new Cell(3, 4), Count = 5, Frozen = false, InAtomizer = false, Forbidden = true }],
        Atomizers = [new O.WastepackAtomizer { Position = new Cell(8, 9) }],
        Gestators = [new O.MechGestatorState { BillId = "Bill_Mech1", WasteCount = 2 }, new O.MechGestatorState { BillId = null, WasteCount = 0 }],
        Chargers = [new O.MechChargerState { Powered = true, FullOfWaste = false, ChargingMechId = null }],
        GeneBanks = [new O.GeneBankState { Position = new Cell(1, 1), Capacity = 4, PackIds = ["Thing_Genepack1"] }],
        GeneAssemblers = [new O.GeneAssemblerState { Position = new Cell(2, 1) }],
        Genepacks = [new O.GenepackState { BankId = "Thing_GeneBank1" }, new O.GenepackState { BankId = null }],
        Babies = ["Thing_Human2"],
        Breastfeeders = ["Thing_Human1"],
    };

    [Fact]
    public void SectionJsonRoundTripIsExact()
    {
        O.BiotechSection section = new O.BiotechSection.Active(Colony);

        var json = WireJson.Serialize(section);

        Assert.Equal(
            """{"type":"active","facts":{"pollutedUncoveredCells":7,"wastepacks":[{"thingId":"Thing_Wastepack1","defName":"Wastepack","position":{"x":3,"z":4},"count":5,"frozen":false,"inAtomizer":false,"forbidden":true}],"atomizers":[{"position":{"x":8,"z":9}}],"gestators":[{"billId":"Bill_Mech1","wasteCount":2},{"billId":null,"wasteCount":0}],"chargers":[{"powered":true,"fullOfWaste":false,"chargingMechId":null}],"geneBanks":[{"position":{"x":1,"z":1},"capacity":4,"packIds":["Thing_Genepack1"]}],"geneAssemblers":[{"position":{"x":2,"z":1}}],"genepacks":[{"bankId":"Thing_GeneBank1"},{"bankId":null}],"babies":["Thing_Human2"],"breastfeeders":["Thing_Human1"]}}""",
            json);
        Assert.Equal(section, WireJson.Deserialize<O.BiotechSection>(json));
        Assert.Equal("""{"type":"inactive"}""", WireJson.Serialize(NoBiotech));
        Assert.Equal(NoBiotech, WireJson.Deserialize<O.BiotechSection>("""{"type":"inactive"}"""));
    }

    [Fact]
    public void PawnBlockJsonRoundTripIsExact()
    {
        O.PawnBiotechState state = new O.PawnBiotechState.Active(new O.PawnBiotech
        {
            DevelopmentalStage = RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Adult,
            Genes = [new O.PawnGene { DefName = "Hair_Black", Active = true }],
            Mechanitor = null,
            Mech = new O.PawnMech
            {
                Overseer = null,
                WorkMode = "Work",
                Energy = new O.PawnMechEnergy.Level { Fraction = 0.5 },
                Group = new O.PawnMechGroup { Index = 1, RechargeBelow = 0.1, RechargeAbove = 1 },
            },
            Deathrest = new O.PawnDeathrest { Deathresting = false, Capacity = 2 },
        });

        var json = WireJson.Serialize(state);

        Assert.Equal(
            """{"type":"active","facts":{"developmentalStage":"DEVELOPMENTAL_STAGE_ADULT","genes":[{"defName":"Hair_Black","active":true}],"mechanitor":null,"mech":{"overseer":null,"workMode":"Work","energy":{"type":"level","fraction":0.5},"group":{"index":1,"rechargeBelow":0.1,"rechargeAbove":1}},"deathrest":{"deathresting":false,"capacity":2}}}""",
            json);
        Assert.Equal(state, WireJson.Deserialize<O.PawnBiotechState>(json));

        var lifted = new PawnBiotech { Observed = ((O.PawnBiotechState.Active)state).Facts };
        Assert.Equal(new O.PawnMechGroup { Index = 1, RechargeBelow = 0.1, RechargeAbove = 1 }, lifted.Mech!.Group);
        Assert.Equal(new O.PawnMechEnergy.Level { Fraction = 0.5 }, lifted.Mech.Energy);
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
        Assert.True(GeneBankConcernRules.GeneBankOwed(Colony with { Genepacks = [.. Enumerable.Repeat(new O.GenepackState { BankId = null }, 4)] }));

        // A charger is owed only with a mechanitor and every charger busy and clear of waste.
        Assert.False(MechRules.MechChargerOwed(1, Colony.Chargers));
        var busy = Colony with { Chargers = [new O.MechChargerState { Powered = true, FullOfWaste = false, ChargingMechId = "Thing_Mech1" }] };
        Assert.True(MechRules.MechChargerOwed(1, busy.Chargers));
        Assert.False(MechRules.MechChargerOwed(0, busy.Chargers));
    }

    [Fact]
    public void InactiveDlcSectionsDeriveNoNeeds()
    {
        Assert.Empty(PollutionRules.PollutionAllowSupplies(NoBiotech));
        Assert.Equal("Thing_Wastepack1", Assert.Single(PollutionRules.PollutionAllowSupplies(new O.BiotechSection.Active(Colony))).Thing);

        // Every DLC section of the core snapshot is inactive: no expansion counts as installed, and no family derives.
        var facts = DlcSectionReads.Installed(new RoundsFacts(), ColonyFactsCoreTests.Snapshot());
        Assert.Equal((false, false, false), (facts.Royalty is RoyaltyState.Active, facts.Anomaly is AnomalyState.Active, facts.Biotech is O.BiotechSection.Active));
        Assert.IsType<O.BiotechSection.Inactive>(new RoundsFacts().Biotech);
        Assert.Empty(new BiotechFamily(new BiotechNeeds { Mechs = true, MechCharger = true, GeneBank = true, Pollution = true })
            .Derive(new BiotechFacts.Inactive()));
        Assert.Empty(new RoyaltyFamily().Derive(new RoyaltyFamilyFacts { Royalty = facts.Royalty, PsylinkOwed = false }));
        Assert.Empty(new AnomalyFamily().Derive(AnomalyFacts.From(facts)));

        var active = DlcSectionReads.Installed(new RoundsFacts(), ColonyFactsCoreTests.Snapshot() with { Biotech = new O.BiotechSection.Active(Colony) });
        Assert.Equal(new O.BiotechSection.Active(Colony), active.Biotech);
    }
}
