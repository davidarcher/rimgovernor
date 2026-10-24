namespace RimGovernor.Brain.Policy;

/// <summary>
/// The one way a planner says an input it needs was not read (#3753 D3): the message and <see cref="Fact"/> name the
/// fact. The runtime skips that planner's run with an error-level <c>required_fact</c> decision; any xUnit or
/// acceptance run that sees one fails.
/// </summary>
public sealed class RequiredFactException(string fact) : DomainException("required fact unread: " + fact)
{
    /// <summary>The fact the planner needs and the colony has not read ("resource demand", "outdoor_curve").</summary>
    public string Fact { get; } = fact;
}
