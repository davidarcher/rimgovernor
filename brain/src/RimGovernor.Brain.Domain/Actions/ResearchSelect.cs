namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent to set the native current research project to one already-queued, prerequisite-ordered
/// ResearchProjectDef. It is sent as a ResearchIntent on Actions/Apply; EnsureResearch's routine planner
/// proposes one only while no native research project is current.
/// </summary>
public sealed record ResearchSelect : IActionPayload
{
    /// <summary>Go <c>NewResearchSelect</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public ResearchSelect(string project)
    {
        DomainException.Require(Identity.IsValidText(project), "research select requires a valid project identity");
        Project = project;
    }

    public string Project { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewResearchSelectAction(ActionId id, ResearchSelect value) => Create(id, ActionKind.ResearchSelect, value);

    public ResearchSelect? ResearchSelect => PayloadOf<ResearchSelect>(ActionKind.ResearchSelect);
}
