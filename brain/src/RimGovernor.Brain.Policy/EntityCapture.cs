using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One entity pawn row's capture facts. Target is its holding-platform target block (CompHoldingPlatformTarget);
/// null for an entity without one, which the colony cannot capture.
/// </summary>
public sealed record CapturableEntity
{
    public required RimGovernor.Brain.GameFacts.Observations.PawnState Observed { get; init; }

    private RimGovernor.Brain.GameFacts.Observations.PawnAnomaly Anomaly => Observed.Anomaly is RimGovernor.Brain.GameFacts.Observations.PawnAnomalyState.Active active
        ? active.Facts : throw new RequiredFactException("capturable entity anomaly facts");

    public Domain.PawnId Pawn => new(Observed.Pawn.Id);

    /// <summary>The pawn row's.</summary>
    public bool Dead => Observed.Dead;

    /// <summary>The pawn row's.</summary>
    public bool Downed => Observed.Downed;

    /// <summary>MinimumContainmentStrength.</summary>
    public double Need => Anomaly.MinContainmentStrength;

    /// <summary>CompStudiable.CurrentlyStudiable; false for an entity with no study block.</summary>
    public bool CurrentlyStudiable => Anomaly.Study is { CurrentlyStudiable: true };

    public RimGovernor.Brain.GameFacts.Observations.HeldState? Target => Anomaly.Held;

    /// <summary>The holding-platform target's CompHoldingPlatformTarget.CanBeCaptured; false without a target block.</summary>
    public bool CanBeCaptured => Target is { CanBeCaptured: true };

    /// <summary>The holding-platform target's CurrentlyHeldOnPlatform; false without a target block.</summary>
    public bool Held => Target is { Held: true };
}

/// <summary>What the rule does with one downed entity.</summary>
public enum EntityDecision
{
    /// <summary>Takes the entity to a platform that holds it.</summary>
    Capture,

    /// <summary>Finishes the entity: no platform holds it with margin, or the game does not let the colony capture it.</summary>
    Kill,

    /// <summary>Does neither: a colony fact the rule needs is unread. Reason names it.</summary>
    Refuse,
}

/// <summary>The rule's decision for one downed living entity and why in plain English.</summary>
public sealed record EntityVerdict(Domain.PawnId Pawn, EntityDecision Decision, string Reason = "");

// Capture a downed entity only when the cell's containment strength reaches what the entity needs with a
// margin, and kill the rest. A fact that is not read means no capture and a loud refusal, never a kill or a
// guess. The margin is the strength the cell loses when a lapse of upkeep opens its door.
public static class EntityCaptureRules
{
    /// <summary>
    /// The strength a holder of defs loses when its room's doors are forced open: the
    /// door term, scaled by the holder's containmentFactor like every term it sits in. Throws when the door has
    /// no hit points, so no margin is invented.
    /// </summary>
    public static double CaptureMargin(ContainmentDefs defs)
    {
        DomainException.Require(defs.DoorHP > 0, "the cell's door has no hit points");
        return defs.DoorHP / ContainmentStrengthRules.ContainmentDoorHPDivisor * defs.HolderFactor;
    }

    /// <summary>
    /// Decides every downed living entity that is not held, by pawn ID. A pawn that is
    /// dead, standing or already held is not decided.
    /// </summary>
    public static IReadOnlyList<EntityVerdict> EntityVerdicts(ContainmentPlanning p)
    {
        var result = new List<EntityVerdict>();
        foreach (var e in p.Entities)
        {
            var v = EntityVerdictFor(p, e);
            if (v is not null)
            {
                result.Add(v);
            }
        }
        return [.. result.OrderBy(v => v.Pawn)];
    }

    private static EntityVerdict? EntityVerdictFor(ContainmentPlanning p, CapturableEntity e)
    {
        EntityVerdict Refuse(string reason) => new(e.Pawn, EntityDecision.Refuse, reason);
        if (e.Dead || !e.Downed || e.Held)
        {
            return null;
        }
        if (!e.CanBeCaptured)
        {
            return new EntityVerdict(e.Pawn, EntityDecision.Kill, "the game does not let the colony capture it");
        }
        var need = e.Need;
        if (p.Defs is not ContainmentDefsRead.Read { Defs: var defs })
        {
            return Refuse($"the margin cannot be read from the definitions: {ContainmentDefsRead.NotReadWhy(p.Defs)}");
        }
        double margin;
        try
        {
            margin = CaptureMargin(defs);
        }
        catch (DomainException ex)
        {
            return Refuse($"the margin is unknown: {ex.Message}");
        }
        var best = 0.0;
        var found = false;
        foreach (var h in p.Holders)
        {
            if (h.Available && (!found || h.ContainmentStrength > best))
            {
                best = h.ContainmentStrength;
                found = true;
            }
        }
        if (!found)
        {
            return new EntityVerdict(e.Pawn, EntityDecision.Kill, "no holding platform is available");
        }
        if (best < need + margin)
        {
            var reason = string.Create(
                CultureInfo.InvariantCulture,
                $"the strongest available platform holds {best:F1} and the entity needs {need:F1} plus a margin of {margin:F1}"
            );
            return new EntityVerdict(e.Pawn, EntityDecision.Kill, reason);
        }
        return new EntityVerdict(e.Pawn, EntityDecision.Capture);
    }

    /// <summary>The first downed entity the rule captures, by pawn ID; null when none.</summary>
    public static Domain.PawnId? EntityCaptureTarget(ContainmentPlanning p)
    {
        foreach (var v in EntityVerdicts(p))
        {
            if (v.Decision == EntityDecision.Capture)
            {
                return v.Pawn;
            }
        }
        return null;
    }
}
