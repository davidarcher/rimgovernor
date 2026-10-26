using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>Random social interaction mode of a verified service toil, not a job-def default.</summary>
public enum ActivitySocialMode { Off, Quiet, Normal, SuperActive }

/// <summary>A job whose stationary service toil and DesiredSocialMode override have been verified together.</summary>
public sealed record ActivitySocialJobRead(PawnActivityKind Kind, string Job, ActivitySocialMode Mode);

/// <summary>The two effects of one interaction: its native selection weight and eligible mood memory on its recipient.
/// NegativeOpinionWeight multiplies the observed opinion-factor curve; the other weights stay constant.</summary>
public sealed record BabyInteractionRead(string Interaction, double Weight, bool NegativeOpinionWeight,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<SocialMemoryDefRead>))] EquatableArray<SocialMemoryDefRead> Moods);

/// <summary>A memory's native duration, global and per-other-pawn limits, stacking and final-duration fade.</summary>
public sealed record SocialMemoryDefRead(string Def, double Offset, double DurationTicks, int StackLimit,
    int SamePawnLimit, double Multiplier, double FadeStarts)
{
    public string Label { get; init; } = "";
    public string Unprojected { get; init; } = "";
}

public sealed record PawnOpinionPartsRead(int Relations, int Situational, double Factor, bool Hostile);

/// <summary>A hearer's opinion of one baby, before the mutable baby-fit memories and final hediff multiplier/clamp.
/// Native eligibility and interaction weights are conditional on the observed non-job state.</summary>
public sealed record BabySocialPairRead
{
    public required string Baby { get; init; }
    public required PawnOpinionPartsRead Opinion { get; init; }
    public required bool Eligible { get; init; }
    public required SocialMemoryDefRead? Cry { get; init; }
    public required SocialMemoryDefRead? Giggle { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabyInteractionRead>))]
    public required EquatableArray<BabyInteractionRead> Interactions { get; init; }
}

/// <summary>One adult's social-memory owner; global stack limits span all its babies.</summary>
public sealed record BabySocialPawnRead
{
    public required string Pawn { get; init; }
    public required ActivitySocialMode MaximumMode { get; init; }
    public required double CooldownTicks { get; init; }
    public required double CheckAfterTicks { get; init; }
    public required double RetryAfterTicks { get; init; }
    public required double AbsoluteCooldownTicks { get; init; }
    public required bool WantsInteraction { get; init; }
    public required string Unprojected { get; init; }
    /// <summary>Currently eligible recipients in faction; spatial eligibility is joined against the canonical projected presence in the selected interval.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Recipients { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabySocialPairRead>))]
    public required EquatableArray<BabySocialPairRead> Pairs { get; init; }
}

/// <summary>The read-only native mechanics used by the nursery's conditional social-consequence forecast.</summary>
public sealed record BabySocialRead
{
    public required double CheckIntervalTicks { get; init; }
    public required double RetryIntervalTicks { get; init; }
    public required double MinInteractionTicks { get; init; }
    public required double QuietMtbTicks { get; init; }
    public required double NormalMtbTicks { get; init; }
    public required double SuperActiveMtbTicks { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ActivitySocialJobRead>))]
    public required EquatableArray<ActivitySocialJobRead> Jobs { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CurvePointRead>))]
    public required EquatableArray<CurvePointRead> OpinionFactor { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabySocialPawnRead>))]
    public required EquatableArray<BabySocialPawnRead> Pawns { get; init; }
    /// <summary>Interaction mood-memory defs; their observed members belong to NeedsForecastPawn.Mood.Memories.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> MoodDefs { get; init; }
}
