using System.Text;

namespace RimGovernor.Brain.Domain;

public static class IncidentIds
{
    /// <summary>Mints a fresh incident id when an occurrence opens (Go <c>MintIncidentID</c>).</summary>
    public static IncidentId MintIncidentId() => new("incident-" + PlanIds.MintPlanId().Value);
}

/// <summary>
/// One occurrence of a Response: opened by a review assessment, closed when the assessment stops asserting it.
/// The open key is (world, Kind, Subject); a re-trigger inside an open occurrence refreshes Priority and
/// Snapshot and keeps the Trigger it opened with.
/// </summary>
public sealed record Incident
{
    public IncidentId Id { get; init; }

    /// <summary>The Response's ConcernId.</summary>
    public ConcernId Kind { get; init; }

    /// <summary>The pawn for per-pawn kinds (mood, medical), empty otherwise.</summary>
    public PawnId Subject { get; init; }

    public string Trigger { get; init; } = "";

    /// <summary>From the latest assessment.</summary>
    public int Priority { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public Tick Started { get; init; }

    public bool Closed { get; init; }

    public Tick Ended { get; init; }

    public void Validate()
    {
        DomainException.Require(Id.IsValid && Kind.IsValid && (Subject.IsEmpty || Subject.IsValid), "invalid incident identity");
        DomainException.Require(
            IsWellFormed(Trigger) && !string.IsNullOrWhiteSpace(Trigger) && Encoding.UTF8.GetByteCount(Trigger) <= 256,
            "invalid incident trigger"
        );
        DomainException.Require(
            Priority >= 0 && Priority <= DomainConstants.MaxPriority && Started.Value >= 0 && Snapshot.IsValid,
            "invalid incident assessment"
        );
        DomainException.Require(!(Closed && Ended < Started) && !(!Closed && Ended.Value != 0), "invalid incident lifetime");
    }

    /// <summary>Go's <c>utf8.ValidString</c>: no unpaired surrogates.</summary>
    private static bool IsWellFormed(string s)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(s);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
