using System.Collections.Immutable;
using System.Text;

namespace RimGovernor.Brain.Domain;

public static class PawnSettingsConstants
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

/// <summary>An immutable value: the pawn and the one setting it should hold. One arm per <see cref="SettingKind"/>.</summary>
public abstract record PawnSettings : IActionPayload
{
    private protected PawnSettings(PawnId pawn)
    {
        Pawn = pawn;
    }

    public PawnId Pawn { get; }

    /// <summary>The arm this setting is; the journal's discriminator.</summary>
    public abstract SettingKind Kind { get; }

    /// <summary>The hostility response a pawn should hold.</summary>
    public sealed record HostilitySetting : PawnSettings
    {
        internal HostilitySetting(PawnId pawn, HostilityResponse mode)
            : base(pawn) => Mode = mode;

        public HostilityResponse Mode { get; }

        public override SettingKind Kind => SettingKind.Hostility;
    }

    /// <summary>The pawn's playerSettings.selfTend.</summary>
    public sealed record SelfTendSetting : PawnSettings
    {
        internal SelfTendSetting(PawnId pawn, bool on)
            : base(pawn) => On = on;

        public bool On { get; }

        public override SettingKind Kind => SettingKind.SelfTend;
    }

    /// <summary>The pawn's Medicine inventory-stock count.</summary>
    public sealed record MedicineCarrySetting : PawnSettings
    {
        internal MedicineCarrySetting(PawnId pawn, int count)
            : base(pawn) => Count = count;

        public int Count { get; }

        public override SettingKind Kind => SettingKind.MedicineCarry;
    }

    /// <summary>The nickname arm's colliding short name.</summary>
    public sealed record NicknameSetting : PawnSettings
    {
        internal NicknameSetting(PawnId pawn, string leaveName)
            : base(pawn) => LeaveName = leaveName;

        public string LeaveName { get; }

        public override SettingKind Kind => SettingKind.Nickname;
    }

    /// <summary>The cap on the medicine a doctor may use on the pawn.</summary>
    public sealed record MedicalCareSetting : PawnSettings
    {
        internal MedicalCareSetting(PawnId pawn, MedicalCare care)
            : base(pawn) => Care = care;

        public MedicalCare Care { get; }

        public override SettingKind Kind => SettingKind.MedicalCare;
    }

    /// <summary>The reading policy labelled <see cref="Name"/>.</summary>
    public sealed record ReadingPolicySetting : PawnSettings
    {
        internal ReadingPolicySetting(PawnId pawn, string name)
            : base(pawn) => Name = name;

        public string Name { get; }

        public override SettingKind Kind => SettingKind.ReadingPolicy;
    }

    /// <summary>The drug policy labelled <see cref="Name"/>.</summary>
    public sealed record DrugPolicySetting : PawnSettings
    {
        internal DrugPolicySetting(PawnId pawn, string name)
            : base(pawn) => Name = name;

        public string Name { get; }

        public override SettingKind Kind => SettingKind.DrugPolicy;
    }

    /// <summary>The food policy labelled <see cref="Name"/>.</summary>
    public sealed record FoodPolicySetting : PawnSettings
    {
        internal FoodPolicySetting(PawnId pawn, string name)
            : base(pawn) => Name = name;

        public string Name { get; }

        public override SettingKind Kind => SettingKind.FoodPolicy;
    }

    /// <summary>The MechWorkModeDef name the mech's control group runs.</summary>
    public sealed record MechWorkModeSetting : PawnSettings
    {
        internal MechWorkModeSetting(PawnId pawn, string mode)
            : base(pawn) => Mode = mode;

        public string Mode { get; }

        public override SettingKind Kind => SettingKind.MechWorkMode;
    }

    /// <summary>The index of the overseer control group the mech moves into.</summary>
    public sealed record MechControlGroupSetting : PawnSettings
    {
        internal MechControlGroupSetting(PawnId pawn, int group)
            : base(pawn) => Group = group;

        public int Group { get; }

        public override SettingKind Kind => SettingKind.MechControlGroup;
    }

    /// <summary>The permit (a RoyalTitlePermitDef name) the colonist takes with a faction (a FactionDef name).</summary>
    public sealed record ChoosePermitSetting : PawnSettings
    {
        internal ChoosePermitSetting(PawnId pawn, string faction, string permit)
            : base(pawn)
        {
            Faction = faction;
            Permit = permit;
        }

        public string Faction { get; }

        public string Permit { get; }

        public override SettingKind Kind => SettingKind.ChoosePermit;
    }

    /// <summary>The held entity's extract-bioferrite flag value.</summary>
    public sealed record ExtractBioferriteSetting : PawnSettings
    {
        internal ExtractBioferriteSetting(PawnId pawn, bool on)
            : base(pawn) => On = on;

        public bool On { get; }

        public override SettingKind Kind => SettingKind.ExtractBioferrite;
    }

    /// <summary>Sets the extract-bioferrite flag of the held entity pawn. Native refuses a pawn that is no entity on a holding platform.</summary>
    public static PawnSettings NewExtractBioferriteSetting(PawnId pawn, bool on)
    {
        DomainException.Require(
            pawn.IsValid,
            "an extract bioferrite setting requires a held entity"
        );
        return new ExtractBioferriteSetting(pawn, on);
    }

    /// <summary>
    /// Has the colonist take permit (a RoyalTitlePermitDef name) with faction (a FactionDef name) through the
    /// game's own permit checks. The faction def carries no ':' (the store joins the two).
    /// </summary>
    public static PawnSettings NewChoosePermitSetting(PawnId pawn, string faction, string permit)
    {
        DomainException.Require(
            pawn.IsValid && IdText.IsValidText(faction) && !faction.Contains(':', StringComparison.Ordinal) && IdText.IsValidText(permit),
            "a choose permit setting requires a colonist, a faction def and a permit def"
        );
        return new ChoosePermitSetting(pawn, faction, permit);
    }

    /// <summary>
    /// Sets the work mode (a MechWorkModeDef name) of the control group the mech pawn belongs to. Native refuses
    /// a pawn that is no mech, has no colonist overseer or names no such mode.
    /// </summary>
    public static PawnSettings NewMechWorkModeSetting(PawnId pawn, string mode)
    {
        DomainException.Require(
            pawn.IsValid && IdText.IsValidText(mode),
            "a mech work mode setting requires a mech and a work mode def"
        );
        return new MechWorkModeSetting(pawn, mode);
    }

    /// <summary>Moves the mech pawn into its overseer's control group with the given index. Native refuses an index the overseer has no group for.</summary>
    public static PawnSettings NewMechControlGroupSetting(PawnId pawn, int group)
    {
        DomainException.Require(
            pawn.IsValid && group >= 0,
            "a mech control group setting requires a mech and a non-negative group index"
        );
        return new MechControlGroupSetting(pawn, group);
    }

    /// <summary>Assigns the drug policy labelled name.</summary>
    public static PawnSettings NewDrugPolicySetting(PawnId pawn, string name)
    {
        DomainException.Require(
            pawn.IsValid && IdText.IsValidText(name) && ByteLength(name) <= 80,
            "a drug policy setting requires a pawn and a policy name"
        );
        return new DrugPolicySetting(pawn, name);
    }

    /// <summary>Assigns the reading policy labelled name.</summary>
    public static PawnSettings NewReadingPolicySetting(PawnId pawn, string name)
    {
        DomainException.Require(
            pawn.IsValid && IdText.IsValidText(name) && ByteLength(name) <= 80,
            "a reading policy setting requires a pawn and a policy name"
        );
        return new ReadingPolicySetting(pawn, name);
    }

    /// <summary>Assigns the food policy labelled name.</summary>
    public static PawnSettings NewFoodPolicySetting(PawnId pawn, string name)
    {
        DomainException.Require(
            pawn.IsValid && IdText.IsValidText(name) && ByteLength(name) <= 80,
            "a food policy setting requires a pawn and a policy name"
        );
        return new FoodPolicySetting(pawn, name);
    }

    /// <summary>The pawn's Medicine inventory-stock count, 0 to <see cref="PawnSettingsConstants.MaxMedicineCarry"/>.</summary>
    public static PawnSettings NewMedicineCarrySetting(PawnId pawn, int count)
    {
        DomainException.Require(
            pawn.IsValid && count >= 0 && count <= PawnSettingsConstants.MaxMedicineCarry,
            "a medicine carry setting requires a pawn and a count of 0-3"
        );
        return new MedicineCarrySetting(pawn, count);
    }

    public static PawnSettings NewHostilitySetting(PawnId pawn, HostilityResponse mode)
    {
        DomainException.Require(
            pawn.IsValid && Wire.IsDefined(mode),
            "a hostility setting requires a pawn and Ignore, Attack or Flee"
        );
        return new HostilitySetting(pawn, mode);
    }

    /// <summary>Sets playerSettings.selfTend.</summary>
    public static PawnSettings NewSelfTendSetting(PawnId pawn, bool on)
    {
        DomainException.Require(pawn.IsValid, "a self-tend setting requires a pawn");
        return new SelfTendSetting(pawn, on);
    }

    /// <summary>Renames an owned pawn away from leave, its colliding short name.</summary>
    public static PawnSettings NewNicknameSetting(PawnId pawn, string leave)
    {
        DomainException.Require(
            pawn.IsValid && !string.IsNullOrWhiteSpace(leave) && ByteLength(leave) <= 256,
            "a nickname setting requires a pawn and the short name it leaves"
        );
        return new NicknameSetting(pawn, leave);
    }

    /// <summary>Caps the pawn's medical care.</summary>
    public static PawnSettings NewMedicalCareSetting(PawnId pawn, MedicalCare care)
    {
        DomainException.Require(
            pawn.IsValid && care.IsValid(),
            "a medical care setting requires a pawn and a MedicalCareCategory"
        );
        return new MedicalCareSetting(pawn, care);
    }

    private static int ByteLength(string s) => Encoding.UTF8.GetByteCount(s);
}

public sealed partial record DomainAction
{
    public static DomainAction NewPawnSettingsAction(ActionId id, PawnSettings s) => Create(id, ActionKind.PawnSettings, s);

    public PawnSettings? PawnSettings => PayloadOf<PawnSettings>(ActionKind.PawnSettings);
}
