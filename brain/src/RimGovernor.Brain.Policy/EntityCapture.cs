using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One entity pawn row's capture facts: each is unknown when native did not read it. (Go <c>CapturableEntity</c>.)
/// </summary>
public sealed record CapturableEntity
{
    public Domain.PawnId Pawn { get; init; }

    /// <summary>The pawn row's.</summary>
    public Fact<bool> Dead { get; init; }

    /// <summary>The pawn row's.</summary>
    public Fact<bool> Downed { get; init; }

    /// <summary>The holding-platform target's CompHoldingPlatformTarget.CanBeCaptured.</summary>
    public Fact<bool> CanBeCaptured { get; init; }

    /// <summary>The holding-platform target's CurrentlyHeldOnPlatform.</summary>
    public Fact<bool> Held { get; init; }

    /// <summary>MinimumContainmentStrength.</summary>
    public Fact<double> Need { get; init; }

    /// <summary>The held entity's health.</summary>
    public Fact<bool> NeedsTend { get; init; }

    /// <summary>The held entity's health.</summary>
    public Fact<bool> Bleeding { get; init; }

    /// <summary>The held entity's EscapeChance flag.</summary>
    public Fact<bool> Escaping { get; init; }

    /// <summary>CompStudiable.CurrentlyStudiable; a known false for an entity with no study block.</summary>
    public Fact<bool> CurrentlyStudiable { get; init; }

    /// <summary>The held entity's bioferrite harvest fact.</summary>
    public Fact<ContainmentMode> Mode { get; init; }

    /// <summary>The held entity's bioferrite harvest fact.</summary>
    public Fact<bool> ExtractBioferrite { get; init; }

    /// <summary>The held entity's bioferrite harvest fact.</summary>
    public Fact<bool> HarvesterAttached { get; init; }

    /// <summary>The held entity's bioferrite harvest fact.</summary>
    public Fact<double> BioferritePerDay { get; init; }
}

/// <summary>What the rule does with one downed entity. (Go <c>EntityDecision</c>.)</summary>
public enum EntityDecision
{
    /// <summary>Takes the entity to a platform that holds it.</summary>
    Capture,

    /// <summary>Finishes the entity: no platform holds it with margin, or the game does not let the colony capture it.</summary>
    Kill,

    /// <summary>Does neither: a fact the rule needs is unread. Reason names it.</summary>
    Refuse,
}

/// <summary>The rule's decision for one downed living entity and why in plain English. (Go <c>EntityVerdict</c>.)</summary>
public sealed record EntityVerdict(Domain.PawnId Pawn, EntityDecision Decision, string Reason = "");

// Capture a downed entity only when the cell's containment strength reaches what the entity needs with a
// margin, and kill the rest. A fact that is not read means no capture and a loud refusal, never a kill or a
// guess. The margin is the strength the cell loses when a lapse of upkeep opens its door.
public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>CaptureMargin</c>: the strength a holder of defs loses when its room's doors are forced open: the
    /// door term, scaled by the holder's containmentFactor like every term it sits in. Throws when the door has
    /// no hit points, so no margin is invented.
    /// </summary>
    public static double CaptureMargin(ContainmentDefs defs)
    {
        DomainException.Require(defs.DoorHP > 0, "the cell's door has no hit points");
        return defs.DoorHP / ContainmentDoorHPDivisor * defs.HolderFactor;
    }

    /// <summary>
    /// Go <c>EntityVerdicts</c>: decides every downed living entity that is not held, by pawn ID. A pawn that is
    /// dead, standing or already held is not decided.
    /// </summary>
    public static IReadOnlyList<EntityVerdict> EntityVerdicts(ContainmentPlanning p)
    {
        if (!p.Entities.TryGetValue(out var entities))
        {
            return [];
        }
        var result = new List<EntityVerdict>();
        foreach (var e in entities)
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
        var dk = e.Dead.TryGetValue(out var dead);
        if (dk && dead)
        {
            return null;
        }
        var wk = e.Downed.TryGetValue(out var downed);
        var hk = e.Held.TryGetValue(out var held);
        if ((wk && !downed) || (hk && held))
        {
            return null;
        }
        if (!dk)
        {
            return Refuse("whether the entity is dead is unread");
        }
        if (!wk)
        {
            return Refuse("whether the entity is downed is unread");
        }
        if (!hk)
        {
            return Refuse("whether a platform holds the entity is unread");
        }
        if (!e.CanBeCaptured.TryGetValue(out var capturable))
        {
            return Refuse("whether the game lets the colony capture the entity is unread");
        }
        if (!capturable)
        {
            return new EntityVerdict(e.Pawn, EntityDecision.Kill, "the game does not let the colony capture it");
        }
        if (!e.Need.TryGetValue(out var need))
        {
            return Refuse("the containment strength the entity needs is unread");
        }
        if (!p.Holders.TryGetValue(out var holders))
        {
            return Refuse("the standing holding platforms are unread");
        }
        if (!p.Defs.TryGetValue(out var defs))
        {
            return Refuse($"the margin cannot be read from the definitions: {p.DefsReason}");
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
        foreach (var h in holders)
        {
            if (h.Available && (!found || h.Strength > best))
            {
                best = h.Strength;
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

    /// <summary>
    /// Whether the rule has a downed entity to capture: a standing work for MaintainPopulation, whose custody
    /// step carries it. A refusal or a kill is no deficit.
    /// </summary>
    internal static bool EntityCaptureOwed(ContainmentPlanning p) => EntityCaptureTarget(p) is not null;

    /// <summary>Go <c>EntityCaptureTarget</c>: the first downed entity the rule captures, by pawn ID; null when none.</summary>
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
