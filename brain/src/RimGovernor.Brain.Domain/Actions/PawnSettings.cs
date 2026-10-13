using System.Collections.Immutable;
using System.Text;

namespace RimGovernor.Brain.Domain;

public static partial class DomainConstants
{
    /// <summary>The Medicine inventory-stock group's max (vanilla InventoryStockGroupDef Medicine: 0-3).</summary>
    public const int MaxMedicineCarry = 3;
}

/// <summary>A vanilla HostilityResponseMode name.</summary>
public enum HostilityResponse
{
    Ignore,
    Attack,
    Flee,
}

/// <summary>Names the PawnSettingsIntent arm a <see cref="PawnSettings"/> carries.</summary>
public enum SettingKind
{
    Hostility,
    SelfTend,

    /// <summary>The Medicine inventory-stock count.</summary>
    MedicineCarry,

    /// <summary>Renames an owned pawn away from a short name an older owned pawn holds; native draws the new name.</summary>
    Nickname,

    /// <summary>Caps the medicine a doctor may use.</summary>
    MedicalCare,

    /// <summary>Assigns the reading policy labelled with the pawn's short name.</summary>
    ReadingPolicy,

    /// <summary>Assigns the drug policy labelled with the pawn's short name.</summary>
    DrugPolicy,

    /// <summary>Assigns the food policy labelled with the pawn's short name.</summary>
    FoodPolicy,

    /// <summary>Sets the MechWorkModeDef a mech's control group runs.</summary>
    MechWorkMode,

    /// <summary>Moves a mech into one of its overseer's control groups.</summary>
    MechControlGroup,

    /// <summary>Spends permit points on one permit of a faction.</summary>
    ChoosePermit,

    /// <summary>Sets a held entity's CompHoldingPlatformTarget extractBioferrite flag, which the game's Doctor work giver reads.</summary>
    ExtractBioferrite,

    /// <summary>The food level (0..1) under which a humanlike colonist starts seeking food; 0.30 is vanilla.</summary>
    EatTrigger,
}

/// <summary>A vanilla MedicalCareCategory name: the best medicine a doctor may use on the pawn. Members run lowest tier first.</summary>
public enum MedicalCare
{
    [WireName("NoCare")]
    None,

    [WireName("NoMeds")]
    NoMeds,

    [WireName("HerbalOrWorse")]
    Herbal,

    [WireName("NormalOrWorse")]
    Normal,

    [WireName("Best")]
    Best,
}

public static class MedicalCareExtensions
{
    /// <summary>Every tier, lowest first.</summary>
    public static readonly ImmutableArray<MedicalCare> MedicalCares = [MedicalCare.None, MedicalCare.NoMeds, MedicalCare.Herbal, MedicalCare.Normal, MedicalCare.Best];

    /// <summary>The tier's place in <see cref="MedicalCares"/>; -1 for an unknown value.</summary>
    public static int Rank(this MedicalCare c) => MedicalCares.IndexOf(c);

    public static bool IsValid(this MedicalCare c) => c.Rank() >= 0;

    /// <summary>The next tier up; Best stays Best.</summary>
    public static MedicalCare Raised(this MedicalCare c)
    {
        var r = c.Rank();
        return r >= 0 && r + 1 < MedicalCares.Length ? MedicalCares[r + 1] : c;
    }
}

/// <summary>An immutable value: the pawn and the one setting it should hold. Arm accessors are null when the arm is not selected.</summary>
public sealed record PawnSettings : IActionPayload
{
    private PawnSettings(PawnId pawn, SettingKind kind)
    {
        Pawn = pawn;
        Kind = kind;
    }

    public PawnId Pawn { get; }

    public SettingKind Kind { get; }

    public HostilityResponse? Hostility { get; private init; }

    public bool SelfTend { get; private init; }

    /// <summary>The nickname arm's colliding short name; empty for every other arm.</summary>
    public string LeaveName { get; private init; } = "";

    public MedicalCare? MedicalCare { get; private init; }

    /// <summary>The carry count when this is the carry arm.</summary>
    public int? MedicineCarry { get; private init; }

    public string? ReadingPolicy { get; private init; }

    public string? DrugPolicy { get; private init; }

    public string? FoodPolicy { get; private init; }

    /// <summary>The work mode def when this is the mech work mode arm.</summary>
    public string? MechWorkMode { get; private init; }

    /// <summary>The group index when this is the control group arm.</summary>
    public int? MechControlGroup { get; private init; }

    /// <summary>The flag value when this is the extract bioferrite arm.</summary>
    public bool? ExtractBioferrite { get; private init; }

    /// <summary>The food level when this is the eat trigger arm.</summary>
    public double? EatTrigger { get; private init; }

    private string? PermitFaction { get; init; }

    private string? PermitDef { get; init; }

    /// <summary>The faction and permit defs when this is the permit arm.</summary>
    public (string Faction, string Permit)? ChoosePermit => Kind == SettingKind.ChoosePermit ? (PermitFaction!, PermitDef!) : null;

    /// <summary>Sets the extract-bioferrite flag of the held entity pawn. Native refuses a pawn that is no entity on a holding platform.</summary>
    public static PawnSettings NewExtractBioferriteSetting(PawnId pawn, bool on)
    {
        DomainException.Require(pawn.IsValid, "an extract bioferrite setting requires a held entity");
        return new PawnSettings(pawn, SettingKind.ExtractBioferrite) { ExtractBioferrite = on };
    }

    /// <summary>
    /// Has the colonist take permit (a RoyalTitlePermitDef name) with faction (a FactionDef name) through the
    /// game's own permit checks. The faction def carries no ':' (the store joins the two).
    /// </summary>
    public static PawnSettings NewChoosePermitSetting(PawnId pawn, string faction, string permit)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(faction) && !faction.Contains(':', StringComparison.Ordinal) && Identity.IsValidText(permit),
            "a choose permit setting requires a colonist, a faction def and a permit def"
        );
        return new PawnSettings(pawn, SettingKind.ChoosePermit) { PermitFaction = faction, PermitDef = permit };
    }

    /// <summary>
    /// Sets the work mode (a MechWorkModeDef name) of the control group the mech pawn belongs to. Native refuses
    /// a pawn that is no mech, has no colonist overseer or names no such mode.
    /// </summary>
    public static PawnSettings NewMechWorkModeSetting(PawnId pawn, string mode)
    {
        DomainException.Require(pawn.IsValid && Identity.IsValidText(mode), "a mech work mode setting requires a mech and a work mode def");
        return new PawnSettings(pawn, SettingKind.MechWorkMode) { MechWorkMode = mode };
    }

    /// <summary>Moves the mech pawn into its overseer's control group with the given index. Native refuses an index the overseer has no group for.</summary>
    public static PawnSettings NewMechControlGroupSetting(PawnId pawn, int group)
    {
        DomainException.Require(pawn.IsValid && group >= 0, "a mech control group setting requires a mech and a non-negative group index");
        return new PawnSettings(pawn, SettingKind.MechControlGroup) { MechControlGroup = group };
    }

    /// <summary>Assigns the drug policy labelled name.</summary>
    public static PawnSettings NewDrugPolicySetting(PawnId pawn, string name)
    {
        DomainException.Require(pawn.IsValid && Identity.IsValidText(name) && ByteLength(name) <= 80, "a drug policy setting requires a pawn and a policy name");
        return new PawnSettings(pawn, SettingKind.DrugPolicy) { DrugPolicy = name };
    }

    /// <summary>Assigns the reading policy labelled name.</summary>
    public static PawnSettings NewReadingPolicySetting(PawnId pawn, string name)
    {
        DomainException.Require(pawn.IsValid && Identity.IsValidText(name) && ByteLength(name) <= 80, "a reading policy setting requires a pawn and a policy name");
        return new PawnSettings(pawn, SettingKind.ReadingPolicy) { ReadingPolicy = name };
    }

    /// <summary>Assigns the food policy labelled name.</summary>
    public static PawnSettings NewFoodPolicySetting(PawnId pawn, string name)
    {
        DomainException.Require(pawn.IsValid && Identity.IsValidText(name) && ByteLength(name) <= 80, "a food policy setting requires a pawn and a policy name");
        return new PawnSettings(pawn, SettingKind.FoodPolicy) { FoodPolicy = name };
    }

    /// <summary>The pawn's Medicine inventory-stock count, 0 to <see cref="DomainConstants.MaxMedicineCarry"/>.</summary>
    public static PawnSettings NewMedicineCarrySetting(PawnId pawn, int count)
    {
        DomainException.Require(pawn.IsValid && count >= 0 && count <= DomainConstants.MaxMedicineCarry, "a medicine carry setting requires a pawn and a count of 0-3");
        return new PawnSettings(pawn, SettingKind.MedicineCarry) { MedicineCarry = count };
    }

    public static PawnSettings NewHostilitySetting(PawnId pawn, HostilityResponse mode)
    {
        DomainException.Require(pawn.IsValid && Wire.IsDefined(mode), "a hostility setting requires a pawn and Ignore, Attack or Flee");
        return new PawnSettings(pawn, SettingKind.Hostility) { Hostility = mode };
    }

    /// <summary>Sets playerSettings.selfTend.</summary>
    public static PawnSettings NewSelfTendSetting(PawnId pawn, bool on)
    {
        DomainException.Require(pawn.IsValid, "a self-tend setting requires a pawn");
        return new PawnSettings(pawn, SettingKind.SelfTend) { SelfTend = on };
    }

    /// <summary>Renames an owned pawn away from leave, its colliding short name.</summary>
    public static PawnSettings NewNicknameSetting(PawnId pawn, string leave)
    {
        DomainException.Require(pawn.IsValid && !string.IsNullOrWhiteSpace(leave) && ByteLength(leave) <= 256, "a nickname setting requires a pawn and the short name it leaves");
        return new PawnSettings(pawn, SettingKind.Nickname) { LeaveName = leave };
    }

    /// <summary>Caps the pawn's medical care.</summary>
    public static PawnSettings NewMedicalCareSetting(PawnId pawn, MedicalCare care)
    {
        DomainException.Require(pawn.IsValid && care.IsValid(), "a medical care setting requires a pawn and a MedicalCareCategory");
        return new PawnSettings(pawn, SettingKind.MedicalCare) { MedicalCare = care };
    }

    /// <summary>The food level, 0 to 1, under which the humanlike colonist starts seeking food. Native clears its override at 0.30 or more.</summary>
    public static PawnSettings NewEatTriggerSetting(PawnId pawn, double level)
    {
        DomainException.Require(pawn.IsValid && !double.IsNaN(level) && level >= 0 && level <= 1, "an eat trigger setting requires a pawn and a food level of 0-1");
        return new PawnSettings(pawn, SettingKind.EatTrigger) { EatTrigger = level };
    }

    private static int ByteLength(string s) => Encoding.UTF8.GetByteCount(s);
}

public sealed partial record DomainAction
{
    public static DomainAction NewPawnSettingsAction(ActionId id, PawnSettings s) => Create(id, ActionKind.PawnSettings, s);

    public PawnSettings? PawnSettings => PayloadOf<PawnSettings>(ActionKind.PawnSettings);
}
