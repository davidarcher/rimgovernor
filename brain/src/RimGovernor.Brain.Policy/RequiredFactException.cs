using System.Globalization;
using System.Runtime.CompilerServices;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The one way a planner says an input it needs was not read (#3753 D3): the message and <see cref="Fact"/> name the
/// fact, and <see cref="Site"/> the throw that found it unread (#3822: several reads can name one fact). The runtime skips
/// that planner's run with an error-level <c>required_fact</c> decision; any xUnit or acceptance run that sees one fails.
/// </summary>
public sealed class RequiredFactException(string fact, [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    : DomainException("required fact unread: " + fact)
{
    /// <summary>The fact the planner needs and the colony has not read ("resource demand", "outdoor_curve").</summary>
    public string Fact { get; } = fact;

    /// <summary>The throwing site, file:line and member as the compiler records the call ("LayoutBedMerge.cs:267 ChooseBeds").</summary>
    public string Site { get; } = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(file)}:{line} {member}");
}
