using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3886: a combat pawn state reads whole. A pawn the combat read lists is <see cref="CombatPresence.Engaged"/> with its
/// row's stance, target, fire mode, belt and drug scan as named states; a pawn only the pawn table holds has
/// <see cref="CombatPresence.NoCombatRow"/>. The cell, the bleed facts and the health are plain values off the rows; a
/// combat row whose pawn the frame's table does not hold, or a table row off the map, is not in the view.
/// </summary>
public sealed class CombatPresenceTests
{
    private static readonly DefinitionCatalog Catalog = new(
        [new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" }], DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));

    private static RimGovernor.Brain.GameFacts.Mirror.CombatPawn Row(string id, RimGovernor.Brain.GameFacts.Mirror.CombatSide side) => new()
    {
        Id = new PawnId(id),
        Side = side,
        Cell = new RimGovernor.Brain.Domain.Cell { X = 3, Z = 4 },
        Downed = false,
        Dead = false,
        Stance = RimGovernor.Brain.GameFacts.Mirror.CombatStance.Warmup,
        Health = 0.75,
        MoveSpeed = 4.6,
        Armor = 0.35,
        Changed = new RimGovernor.Brain.GameFacts.Mirror.Watermark { Tick = 1200, Seq = 3 },
        MedicalSkill = 6,
        GoJuiceHigh = false,
        LuciferiumAddicted = false,
        StunTicksLeft = 0,
        TargetMortar = false,
    };

    private static RimGovernor.Brain.GameFacts.Observations.PawnState Placed(string id) => EmergencyFixtures.Placed(id, 3, 4);

    private static RimGovernor.Brain.Bridge.Combat Combat(IReadOnlyList<RimGovernor.Brain.GameFacts.Mirror.CombatPawn> rows, params RimGovernor.Brain.GameFacts.Observations.PawnState[] table) =>
        new() { Pawns = rows, Detail = Pawns.Of(table), Catalog = Catalog };

    [Fact]
    public void ACombatRowIsEngagedWithItsNamedStates()
    {
        var row = Row("Thing_Human1", RimGovernor.Brain.GameFacts.Mirror.CombatSide.Colonist) with
        {
            Draft = new RimGovernor.Brain.GameFacts.Mirror.CombatDraft { FireMode = RimGovernor.Brain.GameFacts.Operations.CombatFireMode.Hold },
            Shield = new RimGovernor.Brain.GameFacts.Mirror.CombatShield { Energy = 0.4 },
            CarriedDrugs = new RimGovernor.Brain.GameFacts.Mirror.CarriedDrugs { Defs = ["GoJuice"] },
            Weapon = "Gun_AssaultRifle",
            TargetId = "Thing_Raider1",
            Job = "AttackStatic",
            StunTicksLeft = 60,
        };
        var bleeding = Placed("Thing_Human1") with
        {
            Health = PawnFixtures.Healthy with { BloodLoss = 0.3, BleedRatePerDay = 1.5, BleedOut = new RimGovernor.Brain.GameFacts.Observations.BleedOut.Within(5) },
        };
        var weapons = new Dictionary<string, WeaponDef> { ["Gun_AssaultRifle"] = new() { Ranged = true, Range = 31, Reach = 31 } };

        var s = Assert.Single(DefensePlanner.CombatPawnStates(Combat([row], bleeding), new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PawnState>(), weapons));

        var engaged = Assert.IsType<CombatPresence.Engaged>(s.Presence);
        Assert.Equal(CombatStance.Warmup, engaged.Row.Stance);
        Assert.Equal(new PawnId("Thing_Raider1"), engaged.Row.Target);
        Assert.Equal(DraftFireMode.HoldFire, engaged.Row.FireMode);
        Assert.Equal(new ShieldBelt.Worn(0.4), engaged.Row.Shield);
        Assert.Equal(["GoJuice"], Assert.IsType<DrugInventory.Scanned>(engaged.Row.CarriedDrugs).Defs.ToArray());
        Assert.Equal(60, engaged.Row.StunTicks);
        Assert.Equal(6, engaged.Row.MedicalSkill);
        Assert.Equal("Gun_AssaultRifle", s.Weapon);
        Assert.Equal(31, s.WeaponFacts.Reach);
        Assert.Equal(new Cell(3, 4), s.Cell);
        Assert.Equal(0.75, s.Health);
        Assert.Equal("AttackStatic", s.Job);
        Assert.Equal(0.3, s.BloodLoss);
        Assert.Equal(1.5, s.BleedRatePerDay);
        Assert.Equal(new RimGovernor.Brain.GameFacts.Observations.BleedOut.Within(5), s.BleedOut);
        Assert.Equal("Colonist", s.Kind);
    }

    [Fact]
    public void AnUndraftedBareRowHasNoBeltNoScanAndNoWeapon()
    {
        var row = Row("Thing_Raider1", RimGovernor.Brain.GameFacts.Mirror.CombatSide.Hostile);

        var s = Assert.Single(DefensePlanner.CombatPawnStates(Combat([row], Placed("Thing_Raider1")), new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PawnState>(), new Dictionary<string, WeaponDef>()));

        var engaged = Assert.IsType<CombatPresence.Engaged>(s.Presence);
        Assert.Equal(DraftFireMode.Undrafted, engaged.Row.FireMode);
        Assert.IsType<ShieldBelt.NoBelt>(engaged.Row.Shield);
        Assert.IsType<DrugInventory.NotScanned>(engaged.Row.CarriedDrugs);
        Assert.Null(s.Weapon);
        Assert.Equal(new WeaponDef(), s.WeaponFacts);
    }

    [Fact]
    public void ACombatRowTheFramesTableDoesNotHoldIsNotInTheView()
    {
        var rows = new[] { Row("Thing_Human1", RimGovernor.Brain.GameFacts.Mirror.CombatSide.Colonist), Row("Thing_Wolf1", RimGovernor.Brain.GameFacts.Mirror.CombatSide.WildAnimal) };

        var states = DefensePlanner.CombatPawnStates(Combat(rows, Placed("Thing_Human1")), new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PawnState>(), new Dictionary<string, WeaponDef>());

        Assert.Equal([new PawnId("Thing_Human1")], states.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void WithoutCombatRowsThePawnTableGivesNoCombatRowStatesOnTheMap()
    {
        var rows = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.PawnState>
        {
            ["Colonist_a"] = Placed("Colonist_a"),
            ["Colonist_b"] = PawnFixtures.Row("Colonist_b"),
        };

        var s = Assert.Single(DefensePlanner.CombatPawnStates(Combat([]), rows, new Dictionary<string, WeaponDef>()));

        Assert.Equal(new PawnId("Colonist_a"), s.Id);
        Assert.IsType<CombatPresence.NoCombatRow>(s.Presence);
        Assert.Null(s.Weapon);
        Assert.Equal(new Cell(3, 4), s.Cell);
        Assert.Equal(1, s.Health);
        Assert.False(s.Wild);
    }

    [Fact]
    public void ADraftWithoutAFireModeBreaksTheRowContract()
    {
        Assert.Equal(DraftFireMode.FireAtWill, DefensePlanner.DraftFireMode(RimGovernor.Brain.GameFacts.Operations.CombatFireMode.AtWill));
        Assert.Equal(DraftFireMode.HoldFire, DefensePlanner.DraftFireMode(RimGovernor.Brain.GameFacts.Operations.CombatFireMode.Hold));
        Assert.Throws<ControlException>(() => DefensePlanner.DraftFireMode((RimGovernor.Brain.GameFacts.Operations.CombatFireMode)0));
        Assert.Throws<ControlException>(() => DefensePlanner.CombatStance((RimGovernor.Brain.GameFacts.Mirror.CombatStance)0));
        Assert.Equal(CombatStance.Melee, DefensePlanner.CombatStance(RimGovernor.Brain.GameFacts.Mirror.CombatStance.Melee));
    }

    [Fact]
    public void HoldFireReadsTheRowAndKeepsTheLastOrderOtherwise()
    {
        var pawn = CombatPawnFixtures.Pawn(new PawnId("Colonist_a"), new Cell(0, 0));
        CombatPawnState Mode(DraftFireMode mode) => pawn with { Presence = new CombatPresence.Engaged(CombatPawnFixtures.Row() with { FireMode = mode }) };

        Assert.True(CombatFireRules.Held(Mode(DraftFireMode.HoldFire), lastHeld: false));
        Assert.False(CombatFireRules.Held(Mode(DraftFireMode.FireAtWill), lastHeld: true));
        Assert.True(CombatFireRules.Held(Mode(DraftFireMode.Undrafted), lastHeld: true));
        Assert.False(CombatFireRules.Held(Mode(DraftFireMode.Undrafted), lastHeld: false));
        Assert.True(CombatFireRules.Held(pawn, lastHeld: true));
        Assert.False(CombatFireRules.Held(pawn, lastHeld: false));
        Assert.Equal("hold_fire", CombatFireRules.FireModeName(DraftFireMode.HoldFire));
        Assert.Equal("fire_at_will", CombatFireRules.FireModeName(DraftFireMode.FireAtWill));
        Assert.Equal("", CombatFireRules.FireModeName(DraftFireMode.Undrafted));
    }

    [Fact]
    public void ADownedRaidersStripFollowsWhatTheFrameShowsItWearing()
    {
        Assert.Equal(PostFightStep.Wait, CombatThreatRules.PostFightNext(RaiderApparel.Bare, StripState.StripOpen));
        Assert.Equal(PostFightStep.Done, CombatThreatRules.PostFightNext(RaiderApparel.Bare, StripState.StripNone));
        Assert.Equal(PostFightStep.Strip, CombatThreatRules.PostFightNext(RaiderApparel.Wearing, StripState.StripNone));
        Assert.Equal(PostFightStep.Strip, CombatThreatRules.PostFightNext(RaiderApparel.NotInFrame, StripState.StripNone));
        Assert.Equal(PostFightStep.Wait, CombatThreatRules.PostFightNext(RaiderApparel.Wearing, StripState.StripPlaced));
        // A placed strip whose raider the frame no longer holds is done, not a fight held on a missing row.
        Assert.Equal(PostFightStep.Done, CombatThreatRules.PostFightNext(RaiderApparel.NotInFrame, StripState.StripPlaced));
        Assert.Equal(PostFightStep.Done, CombatThreatRules.PostFightNext(RaiderApparel.Wearing, StripState.StripRefused));

        Assert.Equal(RaiderApparel.NotInFrame, DefensePlanner.ApparelOf(Pawns.Of(), new PawnId("Thing_Raider1")));
        Assert.Equal(RaiderApparel.Bare, DefensePlanner.ApparelOf(Pawns.Of(Placed("Thing_Raider1")), new PawnId("Thing_Raider1")));
    }

    [Fact]
    public void NoPodEventIsNoArrival() =>
        Assert.Null(DefensePlanner.PodArrival(Combat([])));

    [Fact]
    public void EveryShellKindNamesItsDef()
    {
        var shells = new MortarShells { HE = "Shell_HighExplosive", Incendiary = "Shell_Incendiary", EMP = "Shell_EMP" };
        Assert.Equal("", shells.Def(ShellKind.None));
        Assert.Equal("Shell_HighExplosive", shells.Def(ShellKind.HE));
        Assert.Equal("Shell_Incendiary", shells.Def(ShellKind.Incendiary));
        Assert.Equal("Shell_EMP", shells.Def(ShellKind.EMP));
        Assert.Throws<DomainException>(() => shells.Def((ShellKind)9));
    }
}
