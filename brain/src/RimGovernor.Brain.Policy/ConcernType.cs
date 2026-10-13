namespace RimGovernor.Brain.Policy;

/// <summary>
/// What kind of thing a Concern is: a Standard, a Once Standard (a build-once outcome that settles once and
/// only a measured regression reopens) or an Incident. The table matches the type table in
/// docs/developers/architecture/control-loop.md.
/// </summary>
public enum ConcernType
{
    [WireName("")]
    UnknownConcern = 0,
    [WireName("Standard")]
    StandardConcern,
    [WireName("Once")]
    OnceConcern,
    [WireName("Incident")]
    IncidentConcern,
}

public static partial class PolicyFunctions
{
    /// <summary>Classifies <paramref name="id"/> from the inspection registry; an unknown id is UnknownConcern.</summary>
    public static ConcernType ConcernTypeOf(ConcernId id) =>
        InspectionIndex.TryGetValue(id, out var entry) ? entry.Type : ConcernType.UnknownConcern;

    /// <summary>
    /// Reports whether <paramref name="id"/> is an Incident Concern, whose occurrences are incidents: the
    /// rounds open and close them and never file a Standard row for them.
    /// </summary>
    public static bool IsIncidentKind(ConcernId id) => ConcernTypeOf(id) == ConcernType.IncidentConcern;

    /// <summary>Reports whether <paramref name="id"/> is a Standard Concern, Once or not: the rounds file one Standard row per world for it.</summary>
    public static bool IsStandardKind(ConcernId id)
    {
        var t = ConcernTypeOf(id);
        return t == ConcernType.StandardConcern || t == ConcernType.OnceConcern;
    }

    /// <summary>Reports whether <paramref name="id"/>'s Standard is Once (domain.Standard.Once).</summary>
    public static bool IsOnceKind(ConcernId id) => ConcernTypeOf(id) == ConcernType.OnceConcern;
}
