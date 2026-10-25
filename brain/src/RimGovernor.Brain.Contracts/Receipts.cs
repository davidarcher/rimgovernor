using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Receipts;

[JsonConverter(typeof(WireEnumConverter<SettingsField>))]
public enum SettingsField
{
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
    [WireName("SETTINGS_FIELD_EXTRACT_BIOFERRITE")]
    ExtractBioferrite = 33,
}

[JsonConverter(typeof(WireEnumConverter<FieldOutcome>))]
public enum FieldOutcome
{
    [WireName("FIELD_OUTCOME_UNCHANGED")]
    Unchanged = 1,
    [WireName("FIELD_OUTCOME_APPLIED")]
    Applied = 2,
    [WireName("FIELD_OUTCOME_REFUSED")]
    Refused = 3,
}

/// <summary>
/// A queued medical operation bill: the patient, recipe, part index
/// (absent for a whole-body recipe) and the Bill_Medical id.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<SurgeryState>))]
public enum SurgeryState
{
    [WireName("SURGERY_STATE_QUEUED")]
    Queued = 1,
}

/// <summary>
/// QuestStatus is the vanilla QuestState.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<QuestStatus>))]
public enum QuestStatus
{
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

    public ReceiptOutcome Outcome { get; init; } = null!;
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

    /// <summary>The <c>Applied</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Applied? Applied => (this as AppliedCase)?.Value;

    public sealed record AppliedCase(Applied Value) : ReceiptOutcome;

    public static implicit operator ReceiptOutcome(Applied value) => new AppliedCase(value);

    public sealed record UncertainCase(Uncertain Value) : ReceiptOutcome;

    public static implicit operator ReceiptOutcome(Uncertain value) => new UncertainCase(value);
}

public sealed record NoChange
{
    public EffectEvidence? Observed { get; init; }

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
[JsonDerivedType(typeof(EffectEvidenceEffect.WallCase), "wall")]
[JsonDerivedType(typeof(EffectEvidenceEffect.PawnActivitiesCase), "pawnActivities")]
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
[JsonDerivedType(typeof(EffectEvidenceEffect.BuildRoofCase), "buildRoof")]
public abstract record EffectEvidenceEffect
{
    private protected EffectEvidenceEffect() { }
    public sealed record PawnActivitiesCase : EffectEvidenceEffect;

    public sealed record ConstructionCase : EffectEvidenceEffect;

    public sealed record InstallationCase : EffectEvidenceEffect;

    /// <summary>The <c>Designation</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public DesignationEffect? Designation => (this as DesignationCase)?.Value;

    public sealed record DesignationCase(DesignationEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(DesignationEffect value) => new DesignationCase(value);

    /// <summary>The <c>Settings</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SettingsEffect? Settings => (this as SettingsCase)?.Value;

    public sealed record SettingsCase(SettingsEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(SettingsEffect value) => new SettingsCase(value);

    /// <summary>The <c>Bill</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public BillEffect? Bill => (this as BillCase)?.Value;

    public sealed record BillCase(BillEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(BillEffect value) => new BillCase(value);

    public sealed record ResearchCase : EffectEvidenceEffect;

    /// <summary>The <c>Zone</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ZoneEffect? Zone => (this as ZoneCase)?.Value;

    public sealed record ZoneCase([property: JsonRequired] ZoneEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(ZoneEffect value) => new ZoneCase(value);

    /// <summary>The <c>Wall</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public WallEffect? Wall => (this as WallCase)?.Value;

    public sealed record WallCase(WallEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(WallEffect value) => new WallCase(value);

    /// <summary>The <c>Job</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public JobEffect? Job => (this as JobCase)?.Value;

    public sealed record JobCase(JobEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(JobEffect value) => new JobCase(value);

    /// <summary>The <c>Animal</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public AnimalEffect? Animal => (this as AnimalCase)?.Value;

    public sealed record AnimalCase(AnimalEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(AnimalEffect value) => new AnimalCase(value);

    /// <summary>The <c>Prisoner</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PrisonerEffect? Prisoner => (this as PrisonerCase)?.Value;

    public sealed record PrisonerCase(PrisonerEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(PrisonerEffect value) => new PrisonerCase(value);

    /// <summary>The <c>Trade</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public TradeEffect? Trade => (this as TradeCase)?.Value;

    public sealed record TradeCase(TradeEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(TradeEffect value) => new TradeCase(value);

    /// <summary>The <c>Caravan</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CaravanEffect? Caravan => (this as CaravanCase)?.Value;

    public sealed record CaravanCase(CaravanEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(CaravanEffect value) => new CaravanCase(value);

    /// <summary>The <c>Quest</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public QuestEffect? Quest => (this as QuestCase)?.Value;

    public sealed record QuestCase(QuestEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(QuestEffect value) => new QuestCase(value);

    public sealed record AcquisitionCase : EffectEvidenceEffect;

    public sealed record ExcavationCase : EffectEvidenceEffect;

    public sealed record DialogCase : EffectEvidenceEffect;

    /// <summary>The <c>Deconstruct</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public DeconstructEffect? Deconstruct => (this as DeconstructCase)?.Value;

    public sealed record DeconstructCase(DeconstructEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(DeconstructEffect value) => new DeconstructCase(value);

    /// <summary>The <c>CombatOrders</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CombatOrdersEffect? CombatOrders => (this as CombatOrdersCase)?.Value;

    public sealed record CombatOrdersCase(CombatOrdersEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(CombatOrdersEffect value) => new CombatOrdersCase(value);

    /// <summary>The <c>SurgeryBill</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SurgeryEffect? SurgeryBill => (this as SurgeryBillCase)?.Value;

    public sealed record SurgeryBillCase(SurgeryEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(SurgeryEffect value) => new SurgeryBillCase(value);

    /// <summary>The <c>AreaEdit</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public AreaEffect? AreaEdit => (this as AreaEditCase)?.Value;

    public sealed record AreaEditCase(AreaEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(AreaEffect value) => new AreaEditCase(value);

    /// <summary>The <c>PolicyPrune</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PolicyPruneEffect? PolicyPrune => (this as PolicyPruneCase)?.Value;

    public sealed record PolicyPruneCase(PolicyPruneEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(PolicyPruneEffect value) => new PolicyPruneCase(value);

    /// <summary>The <c>RemoveRoof</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RemoveRoofEffect? RemoveRoof => (this as RemoveRoofCase)?.Value;

    public sealed record RemoveRoofCase(RemoveRoofEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(RemoveRoofEffect value) => new RemoveRoofCase(value);

    public sealed record AreaPlantCutCase : EffectEvidenceEffect;

    /// <summary>The <c>Ritual</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RitualEffect? Ritual => (this as RitualCase)?.Value;

    public sealed record RitualCase(RitualEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(RitualEffect value) => new RitualCase(value);

    public sealed record AbilityCase : EffectEvidenceEffect;

    /// <summary>The <c>Ideoligion</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public IdeoligionEffect? Ideoligion => (this as IdeoligionCase)?.Value;

    public sealed record IdeoligionCase(IdeoligionEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(IdeoligionEffect value) => new IdeoligionCase(value);

    /// <summary>The <c>QuestShuttle</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public QuestShuttleEffect? QuestShuttle => (this as QuestShuttleCase)?.Value;

    public sealed record QuestShuttleCase(QuestShuttleEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(QuestShuttleEffect value) => new QuestShuttleCase(value);

    /// <summary>The <c>HackDesignation</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public HackDesignationEffect? HackDesignation => (this as HackDesignationCase)?.Value;

    public sealed record HackDesignationCase(HackDesignationEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(HackDesignationEffect value) => new HackDesignationCase(value);

    /// <summary>The <c>GiveItem</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public GiveItemEffect? GiveItem => (this as GiveItemCase)?.Value;

    public sealed record GiveItemCase(GiveItemEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(GiveItemEffect value) => new GiveItemCase(value);

    /// <summary>The <c>CommsTradeRequest</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CommsTradeRequestEffect? CommsTradeRequest => (this as CommsTradeRequestCase)?.Value;

    public sealed record CommsTradeRequestCase(CommsTradeRequestEffect Value) : EffectEvidenceEffect;

    public static implicit operator EffectEvidenceEffect(CommsTradeRequestEffect value) => new CommsTradeRequestCase(value);

    public sealed record BuildRoofCase : EffectEvidenceEffect;
}

public sealed record HackDesignationEffect
{
    public string? TargetId { get; init; }

    public required bool Enabled { get; init; }
}

/// <summary>
/// An accepted vanilla order; delivery is observed separately.
/// </summary>
public sealed record GiveItemEffect
{
    public string? RecipientId { get; init; }

    public required long ExpectedRemaining { get; init; }

}

public sealed record IdeoligionEffect
{
    public string? IdeoId { get; init; }

    public Common.IdeoligionDesign? Design { get; init; }
}

public sealed record SnapshotEvidence
{
    public string? EntityId { get; init; }

    public string? BeforeToken { get; init; }

    public string? AfterToken { get; init; }
}

public sealed record DesignationEffect
{
    public string? ThingId { get; init; }

    public string? DesignationDef { get; init; }

    public required bool Present { get; init; }

}

public sealed record FieldResult
{
    public required SettingsField Field { get; init; }

    public required FieldOutcome Outcome { get; init; }

}

public sealed record SettingsEffect
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FieldResult>))]
    public EquatableArray<FieldResult> Fields { get; init; }

    /// <summary>
    /// The requested work types a work settings write skipped because the pawn can no longer do them at apply (the
    /// game holds them at 0), in request order; empty for any other write.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> SkippedWork { get; init; }
}

/// <summary>
/// The bill a production bill intent placed or found already standing.
/// </summary>
public sealed record BillEffect
{
    public Common.Ref? Bill { get; init; }
}

public sealed record CreatedZone
{
    public string? ZoneId { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> Cells { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ZoneEffect.CreatedCase), "created")]
[JsonDerivedType(typeof(ZoneEffect.ZoneCase), "zone")]
public abstract record ZoneEffect
{
    private protected ZoneEffect() { }
    public sealed record CreatedCase([property: JsonRequired, JsonConverter(typeof(EquatableArrayJsonConverter<CreatedZone>))] EquatableArray<CreatedZone> Zones) : ZoneEffect;
    public sealed record ZoneCase([property: JsonRequired] string ZoneId, [property: JsonRequired] bool Present, [property: JsonRequired] int ListedCellCount) : ZoneEffect;
}
public sealed record DeconstructEffect
{
    public string? TargetId { get; init; }

    public string? DesignationId { get; init; }

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
    public string? PawnId { get; init; }

    public required bool Applied { get; init; }

    public string? Refusal { get; init; }

    public string? JobDef { get; init; }
}

public sealed record CombatOrdersEffect
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<CombatOrderResult>))]
    public EquatableArray<CombatOrderResult> Results { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SurgeryTarget.WholeBodyCase), "wholeBody")]
[JsonDerivedType(typeof(SurgeryTarget.PartCase), "part")]
public abstract record SurgeryTarget
{
    private protected SurgeryTarget() { }
    public sealed record WholeBodyCase : SurgeryTarget;
    public sealed record PartCase([property: JsonRequired] int Index) : SurgeryTarget;
}
public sealed record SurgeryEffect
{
    public string? PawnId { get; init; }

    public required SurgeryTarget Target { get; init; }

    public Common.Ref? Bill { get; init; }

    public required SurgeryState State { get; init; }
}

/// <summary>
/// One AreaIntent's result: the bot area key or home, the area's load
/// id (absent once deleted), whether it stands, and its cell count after the
/// edit.
/// </summary>
public sealed record AreaEffect
{
    public required bool Home { get; init; }

    public string? AreaId { get; init; }

    public required bool Present { get; init; }

    public required int CellCount { get; init; }
}

/// <summary>
/// One PolicyPruneIntent's result: the ids deleted by this apply (ids
/// already gone are absent) and the pawns moved off them first.
/// </summary>
public sealed record PolicyPruneEffect
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DeletedIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> ReassignedPawnIds { get; init; }
}

/// <summary>
/// One RemoveRoofIntent's result: cells this apply designated.
/// Applied means designated; the roof read decides when pawns finished.
/// </summary>
public sealed record RemoveRoofEffect
{
    public required int Designated { get; init; }
}

/// <summary>
/// One RitualIntent's result: the ritual applied to the pawn
/// and whether the ritual is started afterwards (the bestowing lord job's
/// ceremonyStarted flag).
/// </summary>
public sealed record RitualEffect
{
    public string? Ritual { get; init; }

}

public sealed record WallEffect
{
    public string? TargetId { get; init; }

    public string? RemovalId { get; init; }

    public SnapshotEvidence? Site { get; init; }
}

public sealed record JobTarget
{
    public JobTargetTarget? Target { get; init; }
}

/// <summary>The <c>target</c> choice of <see cref="JobTarget"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JobTargetTarget.ThingIdCase), "thingId")]
public abstract record JobTargetTarget
{
    private protected JobTargetTarget() { }

    /// <summary>The <c>ThingId</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public string? ThingId => (this as ThingIdCase)?.Value;

    public sealed record ThingIdCase(string Value) : JobTargetTarget;

}

public sealed record JobEffect
{
    public string? PawnId { get; init; }

    public string? JobDef { get; init; }

    public JobTarget? TargetA { get; init; }

    public required bool Issued { get; init; }

    public string? ResultingSnapshotToken { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnimalEffect.TrainingCase), "training")]
[JsonDerivedType(typeof(AnimalEffect.SlaughterCase), "slaughter")]
[JsonDerivedType(typeof(AnimalEffect.TameCase), "tame")]
[JsonDerivedType(typeof(AnimalEffect.ReleaseCase), "release")]
[JsonDerivedType(typeof(AnimalEffect.SterilizationQueuedCase), "sterilizationQueued")]
[JsonDerivedType(typeof(AnimalEffect.SterilizedCase), "sterilized")]
[JsonDerivedType(typeof(AnimalEffect.AreaCase), "area")]
[JsonDerivedType(typeof(AnimalEffect.MasterCase), "master")]
[JsonDerivedType(typeof(AnimalEffect.FollowDraftedCase), "followDrafted")]
[JsonDerivedType(typeof(AnimalEffect.FollowFieldworkCase), "followFieldwork")]
public abstract record AnimalEffect
{
    private protected AnimalEffect() { }
    public sealed record TrainingCase([property: JsonRequired] string TrainableDef, [property: JsonRequired] bool Wanted) : AnimalEffect;
    public sealed record SlaughterCase([property: JsonRequired] bool Designated) : AnimalEffect;
    public sealed record TameCase([property: JsonRequired] bool Designated) : AnimalEffect;
    public sealed record ReleaseCase([property: JsonRequired] bool Designated) : AnimalEffect;
    public sealed record SterilizationQueuedCase : AnimalEffect;
    public sealed record SterilizedCase : AnimalEffect;
    public sealed record AreaCase([property: JsonRequired] string AreaId) : AnimalEffect;
    public sealed record MasterCase([property: JsonRequired] string MasterId) : AnimalEffect;
    public sealed record FollowDraftedCase([property: JsonRequired] bool Follow) : AnimalEffect;
    public sealed record FollowFieldworkCase([property: JsonRequired] bool Follow) : AnimalEffect;
}
/// <summary>
/// outcome is the pawn's actual custody state read from native at observation:
/// held (still a colony prisoner), recruited, enslaved, converted, released,
/// escaped or died. It is the evidence a completed or unsuccessful progress
/// rests on; the interaction_def readback alone only proves the setting.
/// </summary>
public sealed record PrisonerEffect
{
    public string? InteractionDef { get; init; }

    public string? Outcome { get; init; }
}

public sealed record TradeLineEffect
{
    public string? LineId { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradeEffect.ApproachingCase), "approaching")]
[JsonDerivedType(typeof(TradeEffect.OpenCase), "open")]
[JsonDerivedType(typeof(TradeEffect.StagedCase), "staged")]
[JsonDerivedType(typeof(TradeEffect.ExecutedCase), "executed")]
[JsonDerivedType(typeof(TradeEffect.EndedCase), "ended")]
public abstract record TradeEffect
{
    private protected TradeEffect() { }
    public sealed record ApproachingCase : TradeEffect;
    public sealed record OpenCase([property: JsonRequired] string SessionId, [property: JsonRequired] string FactionId) : TradeEffect;
    public sealed record StagedCase([property: JsonRequired, JsonConverter(typeof(EquatableArrayJsonConverter<TradeLineEffect>))] EquatableArray<TradeLineEffect> Lines) : TradeEffect;
    public sealed record ExecutedCase([property: JsonRequired] string DealSignature, [property: JsonRequired] int BeforeSilver, [property: JsonRequired] int AfterSilver, [property: JsonRequired] bool ActuallyTraded) : TradeEffect;
    public sealed record EndedCase : TradeEffect;
}
public sealed record CommsTradeRequestEffect
{
    public string? FactionId { get; init; }

    public string? TraderKind { get; init; }

    public required bool WorkQueued { get; init; }
}

public sealed record CaravanEffect
{
    public required bool AssemblyStarted { get; init; }

    public required int DestinationTile { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }
}

public sealed record QuestEffect
{
    public string? QuestId { get; init; }

    public required bool Accepted { get; init; }

}

public sealed record QuestShuttleEffect
{
    public string? QuestId { get; init; }

    public required bool Autoload { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PawnIds { get; init; }
}

