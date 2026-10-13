using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Receipts;

[JsonConverter(typeof(WireEnumConverter<ConstructionStage>))]
public enum ConstructionStage
{
    [WireName("CONSTRUCTION_STAGE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("CONSTRUCTION_STAGE_BLUEPRINT")]
    Blueprint = 1,
    [WireName("CONSTRUCTION_STAGE_FRAME")]
    Frame = 2,
    [WireName("CONSTRUCTION_STAGE_BUILDING")]
    Building = 3,
    [WireName("CONSTRUCTION_STAGE_CANCELLED")]
    Cancelled = 4,
}

[JsonConverter(typeof(WireEnumConverter<InstallationStage>))]
public enum InstallationStage
{
    [WireName("INSTALLATION_STAGE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("INSTALLATION_STAGE_PACKED")]
    Packed = 1,
    [WireName("INSTALLATION_STAGE_PLACEABLE")]
    Placeable = 2,
    [WireName("INSTALLATION_STAGE_QUEUED")]
    Queued = 3,
    [WireName("INSTALLATION_STAGE_INSTALLED")]
    Installed = 4,
    [WireName("INSTALLATION_STAGE_UNVERIFIED")]
    Unverified = 5,
    [WireName("INSTALLATION_STAGE_UNINSTALL_QUEUED")]
    UninstallQueued = 6,
}

[JsonConverter(typeof(WireEnumConverter<SettingsField>))]
public enum SettingsField
{
    [WireName("SETTINGS_FIELD_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("SETTINGS_FIELD_FORBIDDEN")]
    Forbidden = 1,
    [WireName("SETTINGS_FIELD_POWER")]
    Power = 2,
    [WireName("SETTINGS_FIELD_TEMPERATURE")]
    Temperature = 3,
    [WireName("SETTINGS_FIELD_MEDICAL_BED")]
    MedicalBed = 4,
    [WireName("SETTINGS_FIELD_OWNER")]
    Owner = 5,
    [WireName("SETTINGS_FIELD_PRISONER_BED")]
    PrisonerBed = 6,
    [WireName("SETTINGS_FIELD_WORK")]
    Work = 7,
    [WireName("SETTINGS_FIELD_SCHEDULE")]
    Schedule = 8,
    [WireName("SETTINGS_FIELD_MEDICAL_CARE")]
    MedicalCare = 9,
    [WireName("SETTINGS_FIELD_HOSTILITY")]
    Hostility = 10,
    [WireName("SETTINGS_FIELD_SELF_TEND")]
    SelfTend = 11,
    [WireName("SETTINGS_FIELD_FOLLOW_DRAFTED")]
    FollowDrafted = 12,
    [WireName("SETTINGS_FIELD_FOLLOW_FIELDWORK")]
    FollowFieldwork = 13,
    [WireName("SETTINGS_FIELD_ALLOWED_AREA")]
    AllowedArea = 14,
    [WireName("SETTINGS_FIELD_MASTER")]
    Master = 15,
    [WireName("SETTINGS_FIELD_TRAINING")]
    Training = 16,
    [WireName("SETTINGS_FIELD_SLAUGHTER")]
    Slaughter = 17,
    [WireName("SETTINGS_FIELD_RELEASE")]
    Release = 18,
    [WireName("SETTINGS_FIELD_GROWER_CROP")]
    GrowerCrop = 19,
    [WireName("SETTINGS_FIELD_CLAIM")]
    Claim = 20,
    [WireName("SETTINGS_FIELD_FOOD_RESTRICTION")]
    FoodRestriction = 21,
    [WireName("SETTINGS_FIELD_DRUG_POLICY")]
    DrugPolicy = 22,
    [WireName("SETTINGS_FIELD_SLAVE_BED")]
    SlaveBed = 23,
    [WireName("SETTINGS_FIELD_AUTO_REFUEL")]
    AutoRefuel = 24,
    [WireName("SETTINGS_FIELD_AUTO_HOME_AREA")]
    AutoHomeArea = 25,
    [WireName("SETTINGS_FIELD_NICKNAME")]
    Nickname = 26,
    [WireName("SETTINGS_FIELD_MEDICINE_CARRY")]
    MedicineCarry = 27,
    [WireName("SETTINGS_FIELD_READING_POLICY")]
    ReadingPolicy = 28,
    [WireName("SETTINGS_FIELD_MECH_WORK_MODE")]
    MechWorkMode = 29,
    [WireName("SETTINGS_FIELD_MECH_CONTROL_GROUP")]
    MechControlGroup = 30,
    [WireName("SETTINGS_FIELD_PERMIT")]
    Permit = 31,
    [WireName("SETTINGS_FIELD_RULES")]
    Rules = 32,
    [WireName("SETTINGS_FIELD_EXTRACT_BIOFERRITE")]
    ExtractBioferrite = 33,
    [WireName("SETTINGS_FIELD_EAT_TRIGGER")]
    EatTrigger = 34,
}

[JsonConverter(typeof(WireEnumConverter<FieldOutcome>))]
public enum FieldOutcome
{
    [WireName("FIELD_OUTCOME_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("FIELD_OUTCOME_UNCHANGED")]
    Unchanged = 1,
    [WireName("FIELD_OUTCOME_APPLIED")]
    Applied = 2,
    [WireName("FIELD_OUTCOME_REFUSED")]
    Refused = 3,
    [WireName("FIELD_OUTCOME_UNKNOWN")]
    Unknown = 4,
}

/// <summary>
/// A queued medical operation bill: the patient, recipe, part index
/// (absent for a whole-body recipe) and the Bill_Medical id.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<SurgeryState>))]
public enum SurgeryState
{
    [WireName("SURGERY_STATE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("SURGERY_STATE_QUEUED")]
    Queued = 1,
}

/// <summary>
/// QuestStatus is the vanilla QuestState.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<QuestStatus>))]
public enum QuestStatus
{
    [WireName("QUEST_STATUS_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("QUEST_STATUS_NOT_YET_ACCEPTED")]
    NotYetAccepted = 1,
    [WireName("QUEST_STATUS_ONGOING")]
    Ongoing = 2,
    [WireName("QUEST_STATUS_ENDED_UNKNOWN_OUTCOME")]
    EndedUnknownOutcome = 3,
    [WireName("QUEST_STATUS_ENDED_SUCCESS")]
    EndedSuccess = 4,
    [WireName("QUEST_STATUS_ENDED_FAILED")]
    EndedFailed = 5,
    [WireName("QUEST_STATUS_ENDED_OFFER_EXPIRED")]
    EndedOfferExpired = 6,
    [WireName("QUEST_STATUS_ENDED_INVALID")]
    EndedInvalid = 7,
}

[JsonConverter(typeof(WireEnumConverter<UnsuccessfulReason>))]
public enum UnsuccessfulReason
{
    [WireName("UNSUCCESSFUL_REASON_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("UNSUCCESSFUL_REASON_NATIVE_FAILURE")]
    NativeFailure = 1,
    [WireName("UNSUCCESSFUL_REASON_CANCELLED")]
    Cancelled = 2,
    [WireName("UNSUCCESSFUL_REASON_INTERRUPTED")]
    Interrupted = 3,
    [WireName("UNSUCCESSFUL_REASON_EXPIRED")]
    Expired = 4,
    [WireName("UNSUCCESSFUL_REASON_TARGET_DEAD")]
    TargetDead = 5,
    [WireName("UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED")]
    OutcomeNotAchieved = 6,
}

/// <summary>
/// authorizing_owner (rimgovernor.authority.v1.Owner) was removed: with a single
/// bot process there is only ever one possible authorizing actor, so recording
/// which "owner" admitted a write no longer carries information. Admission is
/// still proven by the admitted_context generation matching the caller's
/// expected_generation at the time of the write.
/// </summary>
public sealed record Receipt
{
    public Common.AttemptKey? Attempt { get; init; }

    public Common.ObservationContext? AdmittedContext { get; init; }

    public ReceiptOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="Receipt"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReceiptOutcome.NoChangeCase), "noChange")]
[JsonDerivedType(typeof(ReceiptOutcome.AppliedCase), "applied")]
[JsonDerivedType(typeof(ReceiptOutcome.UncertainCase), "uncertain")]
public abstract record ReceiptOutcome
{
    private protected ReceiptOutcome() { }

    public sealed record NoChangeCase(NoChange Value) : ReceiptOutcome;

    public static implicit operator ReceiptOutcome(NoChange value) => new NoChangeCase(value);

    public sealed record AppliedCase(Applied Value) : ReceiptOutcome;

    public static implicit operator ReceiptOutcome(Applied value) => new AppliedCase(value);

    public sealed record UncertainCase(Uncertain Value) : ReceiptOutcome;

    public static implicit operator ReceiptOutcome(Uncertain value) => new UncertainCase(value);
}

public sealed record NoChange
{
    public EffectEvidence? Observed { get; init; }

    public string? Detail { get; init; }
}

public sealed record Applied
{
    public EffectEvidence? Observed { get; init; }
}

public sealed record Uncertain
{
    /// <summary>
    /// Only facts actually observed. Missing effects do not assert absence.
    /// </summary>
    public EffectEvidence? LastObserved { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// The admitted operation is recovered from the immutable request for this attempt.
/// Every evidence branch is concrete; none permits arbitrary named properties.
/// </summary>
public sealed record EffectEvidence
{
    /// <summary>
    /// Area and surgery effects went with RecoveryArea and QueueSurgery.
    /// Revoking authority releases owned deconstructions natively.
    /// </summary>
    public EffectEvidenceEffect? Effect { get; init; }
}

/// <summary>The <c>effect</c> choice of <see cref="EffectEvidence"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EffectEvidenceEffect.ConstructionCase), "construction")]
[JsonDerivedType(typeof(EffectEvidenceEffect.InstallationCase), "installation")]
[JsonDerivedType(typeof(EffectEvidenceEffect.DesignationCase), "designation")]
[JsonDerivedType(typeof(EffectEvidenceEffect.SettingsCase), "settings")]
[JsonDerivedType(typeof(EffectEvidenceEffect.BillCase), "bill")]
[JsonDerivedType(typeof(EffectEvidenceEffect.ResearchCase), "research")]
[JsonDerivedType(typeof(EffectEvidenceEffect.ZoneCase), "zone")]
[JsonDerivedType(typeof(EffectEvidenceEffect.AssignCase), "assign")]
[JsonDerivedType(typeof(EffectEvidenceEffect.WallCase), "wall")]
[JsonDerivedType(typeof(EffectEvidenceEffect.JobCase), "job")]
[JsonDerivedType(typeof(EffectEvidenceEffect.AnimalCase), "animal")]
[JsonDerivedType(typeof(EffectEvidenceEffect.PrisonerCase), "prisoner")]
[JsonDerivedType(typeof(EffectEvidenceEffect.TradeCase), "trade")]
[JsonDerivedType(typeof(EffectEvidenceEffect.CaravanCase), "caravan")]
[JsonDerivedType(typeof(EffectEvidenceEffect.QuestCase), "quest")]
[JsonDerivedType(typeof(EffectEvidenceEffect.AcquisitionCase), "acquisition")]
[JsonDerivedType(typeof(EffectEvidenceEffect.ExcavationCase), "excavation")]
[JsonDerivedType(typeof(EffectEvidenceEffect.DialogCase), "dialog")]
[JsonDerivedType(typeof(EffectEvidenceEffect.DeconstructCase), "deconstruct")]
[JsonDerivedType(typeof(EffectEvidenceEffect.CombatOrdersCase), "combatOrders")]
[JsonDerivedType(typeof(EffectEvidenceEffect.SurgeryBillCase), "surgeryBill")]
[JsonDerivedType(typeof(EffectEvidenceEffect.AreaEditCase), "areaEdit")]
[JsonDerivedType(typeof(EffectEvidenceEffect.PolicyPruneCase), "policyPrune")]
[JsonDerivedType(typeof(EffectEvidenceEffect.RemoveRoofCase), "removeRoof")]
[JsonDerivedType(typeof(EffectEvidenceEffect.AreaPlantCutCase), "areaPlantCut")]
[JsonDerivedType(typeof(EffectEvidenceEffect.RitualCase), "ritual")]
[JsonDerivedType(typeof(EffectEvidenceEffect.AbilityCase), "ability")]
[JsonDerivedType(typeof(EffectEvidenceEffect.IdeoligionCase), "ideoligion")]
[JsonDerivedType(typeof(EffectEvidenceEffect.QuestShuttleCase), "questShuttle")]
[JsonDerivedType(typeof(EffectEvidenceEffect.HackDesignationCase), "hackDesignation")]
[JsonDerivedType(typeof(EffectEvidenceEffect.GiveItemCase), "giveItem")]
[JsonDerivedType(typeof(EffectEvidenceEffect.CommsTradeRequestCase), "commsTradeRequest")]
[JsonDerivedType(typeof(EffectEvidenceEffect.GatheringCase), "gathering")]
[JsonDerivedType(typeof(EffectEvidenceEffect.BuildRoofCase), "buildRoof")]
public abstract record EffectEvidenceEffect
{
    private protected EffectEvidenceEffect() { }

    public sealed record ConstructionCase(ConstructionEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(ConstructionEffect value) => new ConstructionCase(value);

    public sealed record InstallationCase(InstallationEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(InstallationEffect value) => new InstallationCase(value);

    public sealed record DesignationCase(DesignationEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(DesignationEffect value) => new DesignationCase(value);

    public sealed record SettingsCase(SettingsEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(SettingsEffect value) => new SettingsCase(value);

    public sealed record BillCase(BillEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(BillEffect value) => new BillCase(value);

    public sealed record ResearchCase(ResearchEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(ResearchEffect value) => new ResearchCase(value);

    public sealed record ZoneCase(ZoneEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(ZoneEffect value) => new ZoneCase(value);

    public sealed record AssignCase(AssignEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(AssignEffect value) => new AssignCase(value);

    public sealed record WallCase(WallEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(WallEffect value) => new WallCase(value);

    public sealed record JobCase(JobEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(JobEffect value) => new JobCase(value);

    public sealed record AnimalCase(AnimalEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(AnimalEffect value) => new AnimalCase(value);

    public sealed record PrisonerCase(PrisonerEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(PrisonerEffect value) => new PrisonerCase(value);

    public sealed record TradeCase(TradeEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(TradeEffect value) => new TradeCase(value);

    public sealed record CaravanCase(CaravanEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(CaravanEffect value) => new CaravanCase(value);

    public sealed record QuestCase(QuestEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(QuestEffect value) => new QuestCase(value);

    public sealed record AcquisitionCase(AcquisitionEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(AcquisitionEffect value) => new AcquisitionCase(value);

    public sealed record ExcavationCase(ExcavationEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(ExcavationEffect value) => new ExcavationCase(value);

    public sealed record DialogCase(DialogEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(DialogEffect value) => new DialogCase(value);

    public sealed record DeconstructCase(DeconstructEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(DeconstructEffect value) => new DeconstructCase(value);

    public sealed record CombatOrdersCase(CombatOrdersEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(CombatOrdersEffect value) => new CombatOrdersCase(value);

    public sealed record SurgeryBillCase(SurgeryEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(SurgeryEffect value) => new SurgeryBillCase(value);

    public sealed record AreaEditCase(AreaEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(AreaEffect value) => new AreaEditCase(value);

    public sealed record PolicyPruneCase(PolicyPruneEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(PolicyPruneEffect value) => new PolicyPruneCase(value);

    public sealed record RemoveRoofCase(RemoveRoofEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(RemoveRoofEffect value) => new RemoveRoofCase(value);

    public sealed record AreaPlantCutCase(AreaPlantCutEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(AreaPlantCutEffect value) => new AreaPlantCutCase(value);

    public sealed record RitualCase(RitualEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(RitualEffect value) => new RitualCase(value);

    public sealed record AbilityCase(AbilityEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(AbilityEffect value) => new AbilityCase(value);

    public sealed record IdeoligionCase(IdeoligionEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(IdeoligionEffect value) => new IdeoligionCase(value);

    public sealed record QuestShuttleCase(QuestShuttleEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(QuestShuttleEffect value) => new QuestShuttleCase(value);

    public sealed record HackDesignationCase(HackDesignationEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(HackDesignationEffect value) => new HackDesignationCase(value);

    public sealed record GiveItemCase(GiveItemEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(GiveItemEffect value) => new GiveItemCase(value);

    public sealed record CommsTradeRequestCase(CommsTradeRequestEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(CommsTradeRequestEffect value) => new CommsTradeRequestCase(value);

    public sealed record GatheringCase(GatheringEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(GatheringEffect value) => new GatheringCase(value);

    public sealed record BuildRoofCase(BuildRoofEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(BuildRoofEffect value) => new BuildRoofCase(value);
}

public sealed record HackDesignationEffect
{
    public string? TargetId { get; init; }

    public bool? Enabled { get; init; }
}

/// <summary>
/// An accepted vanilla order; delivery is observed separately.
/// </summary>
public sealed record GiveItemEffect
{
    public string? HaulerId { get; init; }

    public string? RecipientId { get; init; }

    public string? Definition { get; init; }

    public long? ExpectedRemaining { get; init; }

    public string? SourceItemId { get; init; }

    public JobEffect? Job { get; init; }
}

public sealed record IdeoligionEffect
{
    public string? IdeoId { get; init; }

    public Common.IdeoligionDesign? Design { get; init; }

    public int? ReformCount { get; init; }

    public int? DevelopmentPoints { get; init; }
}

public sealed record SnapshotEvidence
{
    public string? EntityId { get; init; }

    public string? BeforeToken { get; init; }

    public string? AfterToken { get; init; }
}

/// <summary>
/// Output quantities come from ordinary native placement callbacks or exact
/// source-to-corpse identity. Expected yield never populates produced_units.
/// </summary>
public sealed record AcquisitionOutput
{
    public string? ThingId { get; init; }

    public int? Units { get; init; }
}

public sealed record AcquisitionEffect
{
    public string? SourceId { get; init; }

    public string? ResourceDef { get; init; }

    public Common.Cell? Cell { get; init; }

    public bool? Designated { get; init; }

    public bool? LaborFinished { get; init; }

    public int? ProducedUnits { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<AcquisitionOutput>))]
    public EquatableArray<AcquisitionOutput> Outputs { get; init; }

    public bool? OutputObserved { get; init; }

    public bool? OutputComplete { get; init; }

    /// <summary>
    /// Why a still-designated plant harvest has no worker (forbidden, below
    /// harvest growth, no enabled plant cutter, unreachable, cutters busy on
    /// other work); set only on a pending effect.
    /// </summary>
    public string? PendingReason { get; init; }
}

/// <summary>
/// Excavation completes when no mineable remains at the cell; yield is not evidence.
/// </summary>
public sealed record ExcavationEffect
{
    public Common.Cell? Cell { get; init; }

    public string? MineableDefName { get; init; }

    public bool? Designated { get; init; }

    public bool? AdoptedExistingDesignation { get; init; }

    public bool? Cleared { get; init; }

    public bool? Cancelled { get; init; }

    public string? Blocker { get; init; }
}

/// <summary>
/// activated: the exact option's native Activate ran. closed: the window left
/// the stack; advanced: the dialog moved to a linked node instead (a new
/// ColonyFactsSnapshot.dialog observation names the next choice).
/// </summary>
public sealed record DialogEffect
{
    public int? WindowId { get; init; }

    public int? OptionIndex { get; init; }

    public string? OptionLabel { get; init; }

    public bool? Activated { get; init; }

    public bool? Closed { get; init; }

    public bool? Advanced { get; init; }

    public string? JoinerLetterToken { get; init; }

    public string? JoinerPawnId { get; init; }

    public bool? Joined { get; init; }
}

public sealed record ConstructionEffect
{
    public string? OriginThingId { get; init; }

    public string? CurrentThingId { get; init; }

    public string? DefName { get; init; }

    public string? Stuff { get; init; }

    public Common.Cell? Cell { get; init; }

    public Placement.Rotation? Rotation { get; init; }

    public ConstructionStage? Stage { get; init; }

    public bool? Present { get; init; }

    public bool? Started { get; init; }

    public bool? Failed { get; init; }

    public string? Blocker { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> CancelledFrameIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> WipedThingIds { get; init; }
}

public sealed record InstallationEffect
{
    public string? InnerThingId { get; init; }

    public string? PackedThingId { get; init; }

    public string? BlueprintId { get; init; }

    public string? DefName { get; init; }

    public string? Stuff { get; init; }

    public Common.Cell? Cell { get; init; }

    public Placement.Rotation? Rotation { get; init; }

    public InstallationStage? Stage { get; init; }
}

public sealed record DesignationEffect
{
    public string? ThingId { get; init; }

    public string? DesignationDef { get; init; }

    public bool? Present { get; init; }

    public string? ResourceDef { get; init; }

    public Common.Cell? Cell { get; init; }
}

public sealed record FieldResult
{
    public SettingsField? Field { get; init; }

    public FieldOutcome? Outcome { get; init; }

    public string? Reason { get; init; }

    public FieldResultEntry? Entry { get; init; }
}

/// <summary>The <c>entry</c> choice of <see cref="FieldResult"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FieldResultEntry.WorkTypeDefCase), "workTypeDef")]
[JsonDerivedType(typeof(FieldResultEntry.TrainableDefCase), "trainableDef")]
[JsonDerivedType(typeof(FieldResultEntry.ScheduleHourCase), "scheduleHour")]
public abstract record FieldResultEntry
{
    private protected FieldResultEntry() { }

    public sealed record WorkTypeDefCase(string Value) : FieldResultEntry;

    public sealed record TrainableDefCase(string Value) : FieldResultEntry;

    public sealed record ScheduleHourCase(int Value) : FieldResultEntry;
}

public sealed record SettingsEffect
{
    public SnapshotEvidence? Snapshot { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<FieldResult>))]
    public EquatableArray<FieldResult> Fields { get; init; }
}

/// <summary>
/// The bill a production bill intent placed or found already standing.
/// </summary>
public sealed record BillEffect
{
    public SnapshotEvidence? Stack { get; init; }

    public Common.Ref? Bill { get; init; }

    public string? RecipeDef { get; init; }
}

public sealed record ResearchEffect
{
    public string? PreviousProjectDef { get; init; }

    public string? CurrentProjectDef { get; init; }

    public SnapshotEvidence? Snapshot { get; init; }
}

public sealed record CellResult
{
    public Common.Cell? Cell { get; init; }

    public bool? Accepted { get; init; }

    public string? Reason { get; init; }

    public string? TakenFromZoneId { get; init; }
}

public sealed record CreatedZone
{
    public string? ZoneId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }
}

public sealed record ZoneEffect
{
    public SnapshotEvidence? Snapshot { get; init; }

    public string? ZoneId { get; init; }

    public bool? Present { get; init; }

    public int? ChangedCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CellResult>))]
    public EquatableArray<CellResult> Cells { get; init; }

    public int? ListedCellCount { get; init; }

    public int? GridCellCount { get; init; }

    public int? PhantomCellCount { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CreatedZone>))]
    public EquatableArray<CreatedZone> Created { get; init; }
}

public sealed record AssignEffect
{
    public string? PawnId { get; init; }

    public string? ThingId { get; init; }

    public string? PreviousThingId { get; init; }

    public bool? Assigned { get; init; }

    public bool? Sleeping { get; init; }
}

public sealed record DeconstructEffect
{
    public string? TargetId { get; init; }

    public string? DesignationId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> WorkerIds { get; init; }

    public bool? DemolitionObserved { get; init; }

    public SnapshotEvidence? Site { get; init; }

    public bool? WaitingForRoof { get; init; }

    public string? ReplacementId { get; init; }
}

/// <summary>
/// One result per CombatOrders order, in request order. An applied result
/// names the ordered job where one was taken (Goto, AttackStatic,
/// AttackMelee, Wait_Combat); a refusal carries a short snake_case reason:
/// not_drafted, cannot_draft, stale_snapshot, not_found, unreachable,
/// cannot_hit, no_ground_verb, not_a_door, native_refused.
/// </summary>
public sealed record CombatOrderResult
{
    public uint? Index { get; init; }

    public string? PawnId { get; init; }

    public bool? Applied { get; init; }

    public string? Refusal { get; init; }

    public string? JobDef { get; init; }
}

public sealed record CombatOrdersEffect
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<CombatOrderResult>))]
    public EquatableArray<CombatOrderResult> Results { get; init; }
}

public sealed record SurgeryEffect
{
    public string? PawnId { get; init; }

    public string? RecipeDef { get; init; }

    public int? PartIndex { get; init; }

    public Common.Ref? Bill { get; init; }

    public SurgeryState? State { get; init; }
}

/// <summary>
/// One AreaIntent's result: the bot area key, home or pollution_clear, the area's load
/// id (absent once deleted), whether it stands, and its cell count after the
/// edit.
/// </summary>
public sealed record AreaEffect
{
    public string? Key { get; init; }

    public bool? Home { get; init; }

    public string? AreaId { get; init; }

    public bool? Present { get; init; }

    public int? CellCount { get; init; }

    public bool? PollutionClear { get; init; }
}

/// <summary>
/// One PolicyPruneIntent's result: the database (outfit, drug, food,
/// reading or allowed_area), the ids deleted by this apply (ids already gone
/// are absent) and the pawns moved off them first.
/// </summary>
public sealed record PolicyPruneEffect
{
    public string? Database { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DeletedIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> ReassignedPawnIds { get; init; }
}

/// <summary>
/// One RemoveRoofIntent's result: cells this apply designated, cells
/// whose RemoveRoof designation already stood, and cells already unroofed.
/// Applied means designated; the roof read decides when pawns finished.
/// </summary>
public sealed record RemoveRoofEffect
{
    public int? Designated { get; init; }

    public int? Adopted { get; init; }

    public int? Unroofed { get; init; }
}

/// <summary>
/// One BuildRoofIntent's result: cells this apply designated, cells whose BuildRoof
/// designation already stood, and cells already roofed. Applied means designated.
/// </summary>
public sealed record BuildRoofEffect
{
    public int? Designated { get; init; }

    public int? Adopted { get; init; }

    public int? Roofed { get; init; }
}

/// <summary>
/// One AreaPlantCutIntent's result: plants this apply designated
/// CutPlant, trees it designated chop-wood, and plants whose cut or chop
/// designation already stood. Applied means designated; the plant cut census
/// decides when pawns finished.
/// </summary>
public sealed record AreaPlantCutEffect
{
    public int? Cut { get; init; }

    public int? Chopped { get; init; }

    public int? Adopted { get; init; }
}

/// <summary>
/// One RitualIntent's result: the ritual and verb applied to the pawn
/// and whether the ritual is started afterwards (the bestowing lord job's
/// ceremonyStarted flag).
/// </summary>
public sealed record RitualEffect
{
    public string? PawnId { get; init; }

    public string? Ritual { get; init; }

    public string? Verb { get; init; }

    public bool? Started { get; init; }
}

/// <summary>
/// Evidence of one AbilityIntent: the pawn that used it and one arm per
/// source. PermitUseEffect asserts the native postcondition: the permit's
/// cooldown started at the current tick and the favor cost was spent. Arrival of
/// the aid and the strike's results are separate observed states.
/// </summary>
public sealed record AbilityEffect
{
    public string? PawnId { get; init; }

    public AbilityEffectOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="AbilityEffect"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AbilityEffectOutcome.PermitCase), "permit")]
[JsonDerivedType(typeof(AbilityEffectOutcome.PsycastCase), "psycast")]
public abstract record AbilityEffectOutcome
{
    private protected AbilityEffectOutcome() { }

    public sealed record PermitCase(PermitUseEffect Value) : AbilityEffectOutcome;

    public static implicit operator AbilityEffectOutcome(PermitUseEffect value) => new PermitCase(value);

    public sealed record PsycastCase(PsycastUseEffect Value) : AbilityEffectOutcome;

    public static implicit operator AbilityEffectOutcome(PsycastUseEffect value) => new PsycastCase(value);
}

/// <summary>
/// cooldown_started_tick is FactionPermit.LastUsedTick read back after the use
/// (equal to the tick of the apply); favor_after == favor_before - the permit's
/// favor cost. aid_things counts the things the use placed when countable
/// (reported, not gated).
/// </summary>
public sealed record PermitUseEffect
{
    public string? FactionDef { get; init; }

    public string? Permit { get; init; }

    public long? CooldownStartedTick { get; init; }

    public int? FavorBefore { get; init; }

    public int? FavorAfter { get; init; }

    public int? AidThings { get; init; }
}

/// <summary>
/// The psycast outcome: the cast order the game accepted. The pawn
/// holds the ability's cast job (job_def) as its current or queued job; the
/// cast itself, its psyfocus cost and its cooldown follow when the pawn
/// reaches it, so they are observed state, not receipt claims.
/// </summary>
public sealed record PsycastUseEffect
{
    public string? Ability { get; init; }

    public string? JobDef { get; init; }
}

public sealed record WallEffect
{
    public string? TargetId { get; init; }

    public string? RemovalId { get; init; }

    public bool? DemolitionObserved { get; init; }

    public SnapshotEvidence? Site { get; init; }
}

public sealed record JobTarget
{
    public JobTargetTarget? Target { get; init; }
}

/// <summary>The <c>target</c> choice of <see cref="JobTarget"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JobTargetTarget.ThingIdCase), "thingId")]
[JsonDerivedType(typeof(JobTargetTarget.CellCase), "cell")]
public abstract record JobTargetTarget
{
    private protected JobTargetTarget() { }

    public sealed record ThingIdCase(string Value) : JobTargetTarget;

    public sealed record CellCase(Common.Cell Value) : JobTargetTarget;

    public static implicit operator JobTargetTarget(Common.Cell value) => new CellCase(value);
}

public sealed record JobEffect
{
    public string? PawnId { get; init; }

    public int? JobId { get; init; }

    public string? JobDef { get; init; }

    public JobTarget? TargetA { get; init; }

    public JobTarget? TargetB { get; init; }

    public string? WorkGiverDef { get; init; }

    public string? WorkTypeDef { get; init; }

    public int? Count { get; init; }

    public bool? Issued { get; init; }

    public bool? Verified { get; init; }

    public string? VerifiedReason { get; init; }

    public bool? Drafted { get; init; }

    public string? DraftOwner { get; init; }

    public bool? AutoDrafted { get; init; }

    public bool? AutoUndrafted { get; init; }

    public string? SupplyThingId { get; init; }

    public int? HitPoints { get; init; }

    public float? Fuel { get; init; }

    public float? NeedBefore { get; init; }

    public bool? CanTry { get; init; }

    public string? ResultingSnapshotToken { get; init; }

    /// <summary>
    /// Fresh CAS token for the operation's non-pawn target (e.g. RecoverService's
    /// building), populated only by an unconstrained preview discovery read;
    /// distinct from resulting_snapshot_token's post-effect pawn meaning.
    /// </summary>
    public string? TargetSnapshotToken { get; init; }
}

public sealed record AnimalEffect
{
    public SnapshotEvidence? Animal { get; init; }

    public string? TrainableDef { get; init; }

    public bool? Wanted { get; init; }

    public bool? SlaughterDesignated { get; init; }

    public bool? TameDesignated { get; init; }

    public bool? ReleaseDesignated { get; init; }

    public string? AllowedAreaId { get; init; }

    public string? MasterId { get; init; }

    public bool? FollowDrafted { get; init; }

    public bool? FollowFieldwork { get; init; }

    public bool? Sterilized { get; init; }

    public bool? SterilizeQueued { get; init; }
}

/// <summary>
/// outcome is the pawn's actual custody state read from native at observation:
/// held (still a colony prisoner), recruited, enslaved, converted, released,
/// escaped or died. It is the evidence a completed or unsuccessful progress
/// rests on; the interaction_def readback alone only proves the setting.
/// </summary>
public sealed record PrisonerEffect
{
    public SnapshotEvidence? Pawn { get; init; }

    public string? InteractionDef { get; init; }

    public string? Outcome { get; init; }
}

public sealed record TradeLineEffect
{
    public string? LineId { get; init; }

    public int? BeforeCount { get; init; }

    public int? AfterCount { get; init; }
}

public sealed record TradeEffect
{
    public Common.TradeTarget? Target { get; init; }

    public string? SessionId { get; init; }

    public string? DealSignature { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TradeLineEffect>))]
    public EquatableArray<TradeLineEffect> Lines { get; init; }

    public bool? Executed { get; init; }

    public bool? Closed { get; init; }

    public int? BeforeSilver { get; init; }

    public int? AfterSilver { get; init; }

    public int? BeforeGoodwill { get; init; }

    public int? AfterGoodwill { get; init; }

    public string? FactionId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> ReceivedQuestIds { get; init; }

    public SnapshotEvidence? Snapshot { get; init; }

    public bool? ActuallyTraded { get; init; }
}

public sealed record CommsTradeRequestEffect
{
    public string? FactionId { get; init; }

    public Common.TradeRequestKind Kind { get; init; }

    public string? TraderKind { get; init; }

    public string? NegotiatorId { get; init; }

    public bool? WorkQueued { get; init; }

    public long? BeforeRequestTick { get; init; }

    public long? AfterRequestTick { get; init; }

    public int? BeforeGoodwill { get; init; }

    public int? AfterGoodwill { get; init; }

    public long? ArrivalTick { get; init; }
}

public sealed record CaravanEffect
{
    public string? CaravanId { get; init; }

    public bool? AssemblyStarted { get; init; }

    public bool? PathStarted { get; init; }

    public bool? Stopped { get; init; }

    public int? DestinationTile { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }

    public SnapshotEvidence? Snapshot { get; init; }
}

public sealed record QuestEffect
{
    public string? QuestId { get; init; }

    public bool? Accepted { get; init; }

    public bool? RequestActive { get; init; }

    public QuestStatus? State { get; init; }

    public long? AcceptanceTick { get; init; }

    public SnapshotEvidence? Snapshot { get; init; }
}

public sealed record QuestShuttleEffect
{
    public string? QuestId { get; init; }

    public string? ShuttleId { get; init; }

    public bool? Autoload { get; init; }

    public bool? Loading { get; init; }

    public bool? LaunchRequested { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }
}

public sealed record LookupRequest
{
    public Common.Identity? Identity { get; init; }

    public Common.AttemptKey? Attempt { get; init; }
}

public sealed record UnknownAttempt
{
    public Common.ObservationContext? Context { get; init; }
}

public sealed record InFlight
{
    public Common.AttemptKey? Attempt { get; init; }

    public Common.ObservationContext? AdmittedContext { get; init; }
}

public sealed record LookupReply
{
    public LookupReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="LookupReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LookupReplyOutcome.ReceiptCase), "receipt")]
[JsonDerivedType(typeof(LookupReplyOutcome.InFlightCase), "inFlight")]
[JsonDerivedType(typeof(LookupReplyOutcome.UnknownCase), "unknown")]
[JsonDerivedType(typeof(LookupReplyOutcome.FailureCase), "failure")]
public abstract record LookupReplyOutcome
{
    private protected LookupReplyOutcome() { }

    public sealed record ReceiptCase(Receipt Value) : LookupReplyOutcome;

    public static implicit operator LookupReplyOutcome(Receipt value) => new ReceiptCase(value);

    public sealed record InFlightCase(InFlight Value) : LookupReplyOutcome;

    public static implicit operator LookupReplyOutcome(InFlight value) => new InFlightCase(value);

    public sealed record UnknownCase(UnknownAttempt Value) : LookupReplyOutcome;

    public static implicit operator LookupReplyOutcome(UnknownAttempt value) => new UnknownCase(value);

    public sealed record FailureCase(Common.Failure Value) : LookupReplyOutcome;

    public static implicit operator LookupReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record PendingEffect
{
    public EffectEvidence? Evidence { get; init; }
}

public sealed record CompletedEffect
{
    public EffectEvidence? Evidence { get; init; }
}

public sealed record AbsentEffect
{
    public string? InspectionToken { get; init; }
}

public sealed record UnknownEffect
{
    public string? Reason { get; init; }
}

public sealed record UnsuccessfulEffect
{
    public UnsuccessfulReason? Reason { get; init; }

    public EffectEvidence? Evidence { get; init; }

    public string? Detail { get; init; }
}

public sealed record Progress
{
    public Common.AttemptKey? Attempt { get; init; }

    public Common.ObservationContext? Context { get; init; }

    /// <summary>
    /// Required true for absence/completion. Not inferred from empty result arrays.
    /// </summary>
    public bool? CompleteInspection { get; init; }

    public ProgressEffect? Effect { get; init; }
}

/// <summary>The <c>effect</c> choice of <see cref="Progress"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProgressEffect.UnknownCase), "unknown")]
[JsonDerivedType(typeof(ProgressEffect.PendingCase), "pending")]
[JsonDerivedType(typeof(ProgressEffect.CompletedCase), "completed")]
[JsonDerivedType(typeof(ProgressEffect.AbsentCase), "absent")]
[JsonDerivedType(typeof(ProgressEffect.UnsuccessfulCase), "unsuccessful")]
public abstract record ProgressEffect
{
    private protected ProgressEffect() { }

    public sealed record UnknownCase(UnknownEffect Value) : ProgressEffect;

    public static implicit operator ProgressEffect(UnknownEffect value) => new UnknownCase(value);

    public sealed record PendingCase(PendingEffect Value) : ProgressEffect;

    public static implicit operator ProgressEffect(PendingEffect value) => new PendingCase(value);

    public sealed record CompletedCase(CompletedEffect Value) : ProgressEffect;

    public static implicit operator ProgressEffect(CompletedEffect value) => new CompletedCase(value);

    public sealed record AbsentCase(AbsentEffect Value) : ProgressEffect;

    public static implicit operator ProgressEffect(AbsentEffect value) => new AbsentCase(value);

    public sealed record UnsuccessfulCase(UnsuccessfulEffect Value) : ProgressEffect;

    public static implicit operator ProgressEffect(UnsuccessfulEffect value) => new UnsuccessfulCase(value);
}

/// <summary>
/// Applied means the lord was created; spot is the cell the game chose.
/// </summary>
public sealed record GatheringEffect
{
    public string? GatheringDef { get; init; }

    public string? OrganizerId { get; init; }

    public Common.Cell? Spot { get; init; }
}

