using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go pawns.go: the read-issue check every observation row shares.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>pawnsIssues</c>: issues name distinct, valid field paths, carry a valid unavailable reason, and never
    /// name a field the row also reports as known.
    /// </summary>
    internal static void PawnsIssues(IEnumerable<ReadIssue> issues, object message)
    {
        if (PawnsIssueError(issues, message) is { } error)
        {
            throw Contract(error);
        }
    }

    /// <summary>Whether <see cref="PawnsIssues"/> accepts the issues.</summary>
    internal static bool PawnsIssuesValid(IEnumerable<ReadIssue> issues, object message) => PawnsIssueError(issues, message) is null;

    private static string? PawnsIssueError(IEnumerable<ReadIssue> issues, object message)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            if (!IsValidId((issue.Field ?? "")) || !seen.Add((issue.Field ?? "")))
            {
                return "invalid or duplicate pawn issue";
            }
            try
            {
                ValidateUnavailable(issue.Unavailable);
            }
            catch (BridgeContractException)
            {
                return "invalid unavailable";
            }
            // Check the exact path only: an unavailable nested field does not erase its parent's other known facts.
            if (PawnsFieldPresent(message, (issue.Field ?? "")))
            {
                return $"{message.GetType().Name} field \"{(issue.Field ?? "")}\" both known and unavailable";
            }
        }
        return null;
    }

    /// <summary>Go's <c>pawnsFieldPresent</c>: whether the dotted proto field path is populated on the message.</summary>
    internal static bool PawnsFieldPresent(object message, string path)
    {
        var schema = WireMessage.Of(message);
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            var parent = schema.Find(path[..dot]);
            if (parent is null || parent.Kind != WireKind.Message || !parent.IsPopulated(message))
            {
                return false;
            }
            return PawnsFieldPresent(parent.Get(message)!, path[(dot + 1)..]);
        }
        return schema.Find(path) is { } field && FieldPopulated(field, message);
    }

    // A populated field is a set optional (presence, whatever its value) or a non-empty repeated field.
    private static bool FieldPopulated(WireField field, object message) => field.IsPopulated(message);
}
