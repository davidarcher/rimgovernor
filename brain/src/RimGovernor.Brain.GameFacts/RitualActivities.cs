using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>Native legality for a ritual, not a chosen plan: every held ritual precept of the primary ideoligion at every
/// finished colony building its obligation target filter names (#4057). The site cell joins the coupled route locations, so
/// an attendee's walk is priced by the shared leg cache; the ritual's length is the behavior def's durationTicks (catalog, #4058).</summary>
public sealed record RitualActivityRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<RitualSiteRead>))]
    public required EquatableArray<RitualSiteRead> Sites { get; init; }
}

/// <summary>One ritual precept (by load id, with its pattern) at one site (the building's load id and cell): whether the
/// game lets the ritual use the site now (RitualObligationTargetFilter.CanUseTarget against its first active obligation),
/// the game's refusal when not, and every free colonist's verdicts over the candidate set the begin dialog builds.</summary>
public sealed record RitualSiteRead(string Ritual, string Pattern, string Site, Cell Location)
{
    public required bool Usable { get; init; }
    /// <summary>The game's failReason when the site is not usable; "" when it is.</summary>
    public required string Refusal { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<RitualCandidateRead>))]
    public required EquatableArray<RitualCandidateRead> Candidates { get; init; }
}

/// <summary>One free colonist at one ritual site: the game's verdict for each behavior role
/// (RitualRoleAssignments.PawnNotAssignableReason, by the role's id) and for spectating (CanEverSpectate, slot "").</summary>
public sealed record RitualCandidateRead(string Pawn)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<RitualRoleVerdict>))]
    public required EquatableArray<RitualRoleVerdict> Roles { get; init; }
    public required RitualRoleVerdict Spectate { get; init; }
}

/// <summary>Whether the game assigns the pawn to the slot; Reason is the game's text when it does not and "" when it does.</summary>
public sealed record RitualRoleVerdict(string Slot, bool Assignable, string Reason);
