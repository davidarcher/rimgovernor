using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared native gear rows (#3777): each whole, in its "nothing here" state, for a test to change with <c>with</c>.
/// </summary>
internal static class GearFixtures
{
    /// <summary>A role over an actual outfit/gear row and canonical pawn, with no catalog-resolved work or traits.</summary>
    public static GearRoleInput Role => new()
    {
        Observed = Loadout("") with { ApparelPolicy = PoliciesTests.EmptyOutfit },
        Pawn = PawnFixtures.Row(""),
    };

    /// <summary>A gear census of no colonist, stock or locked bill, under the fixture climate.</summary>
    public static RimGovernor.Brain.GameFacts.Observations.GearSnapshot NoSnapshot => new() { Pawns = [], Climate = ColonyModelFixtures.Climate(), StoredApparel = [], LockedBills = [] };

    /// <summary>An unarmed pawn wearing nothing.</summary>
    public static readonly RimGovernor.Brain.GameFacts.Observations.PawnEquipment NoEquipment = new() { PrimaryId = null, Equipped = [], Apparel = [], Armed = false, RangedHitFactor = 0, RangedDps = 0, MeleeDps = 0 };

    /// <summary>A loadout model with no traits, worn garments or options.</summary>
    public static readonly RimGovernor.Brain.GameFacts.Observations.GearLoadoutModel NoModel = new() { Worn = [], Options = [] };

    /// <summary>One unworn, uncoded item at full condition.</summary>
    public static RimGovernor.Brain.GameFacts.Observations.GearItem Item(string id, RimGovernor.Brain.GameFacts.Observations.ItemQuality quality) => new()
    {
        Thing = new RimGovernor.Brain.GameFacts.Common.Ref { Id = id },
        Quality = quality,
        ConditionFraction = 1,
        Forced = false,
        Locked = false,
        Biocoded = false,
        BiocodedTo = null,
    };

    /// <summary>One unworn, untainted option at full condition with no crafters.</summary>
    public static RimGovernor.Brain.GameFacts.Observations.GearLoadoutOption Option(string id, string defName, string? stuff, RimGovernor.Brain.GameFacts.Observations.GearSource source) => new()
    {
        Id = id,
        DefName = defName,
        Stuff = stuff,
        Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Normal),
        Source = source,
        Condition = 1,
        Tainted = false,
        Locked = false,
        Crafters = [],
    };

    /// <summary>An adult male colonist's loadout read that can change apparel, with nothing worn, offered or modeled.</summary>
    public static RimGovernor.Brain.GameFacts.Observations.GearLoadout Loadout(string pawn) => new()
    {
        Token = "load",
        Pawn = new PawnId(pawn),
        Candidates = [],
        Blocked = false,
        ApparelPolicy = null,
        LoadoutModel = NoModel,
        Gender = RimGovernor.Brain.GameFacts.Defs.Gender.Male,
        DevelopmentalStage = RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Adult,
        BodyPartGroups = [],
    };
}
