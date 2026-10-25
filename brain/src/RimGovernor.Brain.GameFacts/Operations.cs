using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Operations;

/// <summary>
/// Batched combat micro, the Actions/Apply combat_orders arm:
/// every order applies in request order within one apply, so on one game
/// tick. Each pawn's snapshot token, when sent, is checked once, before any
/// order runs; a pawn may carry several orders (fire mode then move). A pawn
/// order needs an eligible drafted pawn, which a draft order earlier in the
/// batch may make. A door or mortar_fire order names no pawn. Per-order refusals come back
/// in CombatOrdersEffect; only a malformed batch refuses the whole action.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<CombatFireMode>))]
public enum CombatFireMode
{
    [WireName("COMBAT_FIRE_MODE_AT_WILL")]
    AtWill = 1,
    [WireName("COMBAT_FIRE_MODE_HOLD")]
    Hold = 2,
}

/// <summary>
/// HostilityResponse is vanilla HostilityResponseMode.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<HostilityResponse>))]
public enum HostilityResponse
{
    [WireName("HOSTILITY_RESPONSE_IGNORE")]
    Ignore = 1,
    [WireName("HOSTILITY_RESPONSE_ATTACK")]
    Attack = 2,
    [WireName("HOSTILITY_RESPONSE_FLEE")]
    Flee = 3,
}

[JsonConverter(typeof(WireEnumConverter<MedicalCare>))]
public enum MedicalCare
{
    [WireName("MEDICAL_CARE_NO_CARE")]
    NoCare = 1,
    [WireName("MEDICAL_CARE_NO_MEDICINE")]
    NoMedicine = 2,
    [WireName("MEDICAL_CARE_HERBAL_OR_WORSE")]
    HerbalOrWorse = 3,
    [WireName("MEDICAL_CARE_NORMAL_OR_WORSE")]
    NormalOrWorse = 4,
    [WireName("MEDICAL_CARE_BEST")]
    Best = 5,
}

[JsonConverter(typeof(WireEnumConverter<RepeatMode>))]
public enum RepeatMode
{
    [WireName("REPEAT_MODE_FOREVER")]
    Forever = 1,
    [WireName("REPEAT_MODE_COUNT")]
    Count = 2,
    [WireName("REPEAT_MODE_TARGET")]
    Target = 3,
}

/// <summary>
/// reason names the ground rule a refused (accepted false) zone broke.
/// </summary>
public sealed record ZonePreview
{
    public RimGovernor.Brain.GameFacts.Common.ObservationContext? Context { get; init; }

    public required bool Accepted { get; init; }

    public string? Reason { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CellList>))]
    public EquatableArray<CellList> Components { get; init; }
}

public sealed record ZonePreviewReply
{
    public ZonePreviewReplyOutcome Outcome { get; init; } = null!;
}

/// <summary>The <c>outcome</c> choice of <see cref="ZonePreviewReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ZonePreviewReplyOutcome.EvaluatedCase), "evaluated")]
[JsonDerivedType(typeof(ZonePreviewReplyOutcome.FailureCase), "failure")]
public abstract record ZonePreviewReplyOutcome
{
    private protected ZonePreviewReplyOutcome() { }

    /// <summary>The <c>Evaluated</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ZonePreview? Evaluated => (this as EvaluatedCase)?.Value;

    public sealed record EvaluatedCase(ZonePreview Value) : ZonePreviewReplyOutcome;

    public static implicit operator ZonePreviewReplyOutcome(ZonePreview value) => new EvaluatedCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : ZonePreviewReplyOutcome;

    public static implicit operator ZonePreviewReplyOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// Write one drug policy's full contents: the DrugPolicy labelled
/// name (made when missing) carries exactly entries; every drug the intent
/// does not name is off (no joy, addiction or scheduled use, nothing
/// carried). An entry names a drug ThingDef the policy lists, at most once.
/// Native refuses a name several policies carry. A policy that already
/// matches applies again. PawnSettingsIntent.drug_policy assigns it.
/// Evidence is a SettingsEffect on the policy's load id with the
/// DRUG_POLICY field.
/// </summary>
public sealed record DrugPolicyEntry
{
    public required string DrugDef { get; init; }

    public required bool AllowedForJoy { get; init; }

    public required bool AllowedForAddiction { get; init; }

    public required bool AllowScheduled { get; init; }

    public required float DaysFrequency { get; init; }

    public required float OnlyIfMoodBelow { get; init; }

    public required float OnlyIfJoyBelow { get; init; }

    public required int TakeToInventory { get; init; }
}

/// <summary>The wire body of a refused native reply; the domain forms are the verdict and admission refusals, which carry typed ids rather than wire strings.</summary>
public sealed record Refusal
{
    public required RimGovernor.Brain.GameFacts.Common.FailureCode Code { get; init; }

    public string? Reason { get; init; }

    public required RimGovernor.Brain.GameFacts.Common.RefusalClass RefusalClass { get; init; }
}

public sealed record ActionResult
{
    public string? Key { get; init; }

    public ActionResultOutcome Outcome { get; init; } = null!;
}

/// <summary>The <c>outcome</c> choice of <see cref="ActionResult"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ActionResultOutcome.AppliedCase), "applied")]
[JsonDerivedType(typeof(ActionResultOutcome.RefusedCase), "refused")]
[JsonDerivedType(typeof(ActionResultOutcome.FailedCase), "failed")]
public abstract record ActionResultOutcome
{
    private protected ActionResultOutcome() { }

    /// <summary>The <c>Applied</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Receipts.Receipt? Applied => (this as AppliedCase)?.Value;

    public sealed record AppliedCase(RimGovernor.Brain.GameFacts.Receipts.Receipt Value) : ActionResultOutcome;

    public static implicit operator ActionResultOutcome(RimGovernor.Brain.GameFacts.Receipts.Receipt value) => new AppliedCase(value);

    /// <summary>The <c>Refused</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Refusal? Refused => (this as RefusedCase)?.Value;

    public sealed record RefusedCase(Refusal Value) : ActionResultOutcome;

    public static implicit operator ActionResultOutcome(Refusal value) => new RefusedCase(value);

    /// <summary>The <c>Failed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RimGovernor.Brain.GameFacts.Common.Failure? Failed => (this as FailedCase)?.Value;

    public sealed record FailedCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : ActionResultOutcome;

    public static implicit operator ActionResultOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailedCase(value);
}

/// <summary>
/// batch_failure means no action in the batch was applied.
/// </summary>
public sealed record ApplyReply
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ActionResult>))]
    public EquatableArray<ActionResult> Results { get; init; }

    public RimGovernor.Brain.GameFacts.Common.Failure? BatchFailure { get; init; }
}

public sealed record CellList
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.Domain.Cell>))]
    public EquatableArray<RimGovernor.Brain.Domain.Cell> Cells { get; init; }
}
