namespace RimGovernor.Brain.Policy;

/// <summary>The native site-level counterfactual support answer. (Go <c>ExcavationSupport</c>.)</summary>
public enum ExcavationSupport : byte
{
    Unknown,
    Supported,
    Unsupported,
}

public sealed record ExcavationFacts
{
    public GenerationSnapshot Snapshot { get; init; }

    public Tick ObservationTick { get; init; }

    /// <summary>Known false only when native saw the cell unfogged.</summary>
    public Fact<bool> Fogged { get; init; }

    /// <summary>The visible rock definition at the cell, known empty when the cell holds no rock.</summary>
    public Fact<string> Definition { get; init; }

    public Fact<bool> Eligible { get; init; }

    public ExcavationSupport Support { get; init; }

    /// <summary>A mining-capable colonist that can currently reach the excavation's access cell.</summary>
    public Fact<bool> WorkerAvailable { get; init; }

    /// <summary>The excavation's access cell itself.</summary>
    public Fact<bool> AccessReachable { get; init; }
}

public sealed record ExcavationRequest(DomainAction Action, Progress Progress, GenerationSnapshot Current, ExcavationFacts Facts);

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>ExcavationUnsupported</c>: native counterfactual roof support reports that removing this cell would
    /// leave roof outside the installed support radius of any remaining holder, or collapse is already pending.
    /// </summary>
    public static readonly Reason ExcavationUnsupported = new("excavation_unsupported");

    /// <summary>
    /// Go <c>ExcavationGeometryChanged</c>: the cell no longer holds the expected visible rock, holds a
    /// protected structure, or native mining refuses it.
    /// </summary>
    public static readonly Reason ExcavationGeometryChanged = new("excavation_geometry_changed");

    /// <summary>
    /// Go <c>EvaluateExcavation</c>: re-validates one already-selected excavation cell immediately before
    /// dispatch, the same shape <c>EvaluateWallRemoval</c> uses. Admission proves the exact visible rock, native
    /// eligibility, roof support and worker access still hold; it does not prove pawns will finish mining.
    /// </summary>
    public static AdmissionDecision EvaluateExcavation(ExcavationRequest r)
    {
        AdmissionDecision Refuse(Reason reason) => new() { Refused = [new Refusal { Action = r.Action.Id, Reason = reason }] };
        var excavation = r.Action.Excavation;
        // A DomainAction is canonical by construction, so Go's re-canonicalisation check reduces to the kind test.
        if (excavation is null)
        {
            return Refuse(NotReady);
        }
        var v = r.Progress.View;
        var f = r.Facts;
        if (!r.Current.IsValid || r.Current.Native.Value == 0 || !f.Snapshot.Matches(r.Current))
        {
            return Refuse(StaleFacts);
        }
        if (r.Progress.Action != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != Stage.Pending && v.Stage != Stage.Prepared))
        {
            return Refuse(NotReady);
        }
        if (f.ObservationTick < v.Tick
            || (v.Stage == Stage.Prepared && !v.Snapshot.SameWorld(r.Current))
            || (v.Attempt.Value > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)))
        {
            return Refuse(StaleFacts);
        }
        if (!f.Fogged.TryGetValue(out var fogged) || fogged)
        {
            return Refuse(UnknownFacts);
        }
        var known = f.Definition.TryGetValue(out var definition);
        var eligibleKnown = f.Eligible.TryGetValue(out var eligible);
        if (!known || !eligibleKnown)
        {
            return Refuse(UnknownFacts);
        }
        // A cell the pawns already cleared (a designation the controller no longer tracks, finished between
        // planning and dispatch) is adopted as done: dispatch records the cleared evidence instead of designating.
        if (definition!.Length == 0)
        {
            return new AdmissionDecision { Admitted = true };
        }
        if (definition != excavation.Definition || !eligible)
        {
            return Refuse(ExcavationGeometryChanged);
        }
        switch (f.Support)
        {
            case ExcavationSupport.Supported:
                break;
            case ExcavationSupport.Unsupported:
                return Refuse(ExcavationUnsupported);
            default:
                return Refuse(UnknownFacts);
        }
        var accessKnown = f.AccessReachable.TryGetValue(out var access);
        var workerKnown = f.WorkerAvailable.TryGetValue(out var worker);
        if (!accessKnown || !workerKnown)
        {
            return Refuse(UnknownFacts);
        }
        if (!access || !worker)
        {
            return Refuse(NotReady);
        }
        return new AdmissionDecision { Admitted = true };
    }
}
