using D = RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared native gear rows (#3777): each whole, in its "nothing here" state, for a test to change with <c>with</c>.
/// </summary>
internal static class GearFixtures
{
    /// <summary>A gear census of no colonist, stock or locked bill, under the fixture climate.</summary>
    public static O.GearSnapshot NoSnapshot => new() { Pawns = [], Climate = ColonyModelFixtures.Climate(), StoredApparel = [], LockedBills = [] };

    /// <summary>An unarmed pawn wearing nothing.</summary>
    public static readonly O.PawnEquipment NoEquipment = new() { PrimaryId = null, Equipped = [], Apparel = [], Armed = false, RangedHitFactor = 0, RangedDps = 0, MeleeDps = 0 };

    /// <summary>A loadout model with no traits, worn garments or options.</summary>
    public static readonly O.GearLoadoutModel NoModel = new() { Traits = [], Worn = [], Options = [] };

    /// <summary>One unworn, uncoded item at full condition.</summary>
    public static O.GearItem Item(string id, O.ItemQuality quality) => new()
    {
        Thing = new RimGovernor.Contracts.Wire.Common.Ref { Id = id },
        Quality = quality,
        ConditionFraction = 1,
        Forced = false,
        Locked = false,
        Biocoded = false,
        BiocodedTo = null,
    };

    /// <summary>One unworn, untainted option at full condition with no crafters.</summary>
    public static O.GearLoadoutOption Option(string id, string defName, string? stuff, O.GearSource source) => new()
    {
        Id = id,
        DefName = defName,
        Stuff = stuff,
        Quality = new O.ItemQuality.Graded(D.QualityCategory.Normal),
        Source = source,
        Condition = 1,
        Tainted = false,
        Locked = false,
        Crafters = [],
    };

    /// <summary>An adult male colonist's loadout read that can change apparel, with nothing worn, offered or modeled.</summary>
    public static O.GearLoadout Loadout(string pawn) => new()
    {
        Token = "load",
        Pawn = new PawnId(pawn),
        Equipment = NoEquipment,
        Candidates = [],
        ComfortableMinC = 10,
        ComfortableMaxC = 30,
        Blocked = false,
        ApparelPolicy = null,
        LoadoutModel = NoModel,
        Gender = D.Gender.Male,
        DevelopmentalStage = D.DevelopmentalStage.Adult,
        BodyPartGroups = [],
    };
}
