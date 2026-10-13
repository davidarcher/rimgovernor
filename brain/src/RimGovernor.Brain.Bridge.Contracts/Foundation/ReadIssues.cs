using Google.Protobuf;
using Google.Protobuf.Reflection;
using RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go pawns.go: the read-issue check every observation row shares.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>pawnsIssues</c>: issues name distinct, valid field paths, carry a valid unavailable reason, and never
    /// name a field the row also reports as known.
    /// </summary>
    internal static void PawnsIssues(IEnumerable<ReadIssue> issues, IMessage message)
    {
        if (PawnsIssueError(issues, message) is { } error)
        {
            throw Contract(error);
        }
    }

    /// <summary>Whether <see cref="PawnsIssues"/> accepts the issues.</summary>
    internal static bool PawnsIssuesValid(IEnumerable<ReadIssue> issues, IMessage message) => PawnsIssueError(issues, message) is null;

    private static string? PawnsIssueError(IEnumerable<ReadIssue> issues, IMessage message)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            if (!IsValidId(issue.Field) || !seen.Add(issue.Field))
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
            if (PawnsFieldPresent(message, issue.Field))
            {
                return $"{message.Descriptor.Name} field \"{issue.Field}\" both known and unavailable";
            }
        }
        return null;
    }

    /// <summary>Go's <c>pawnsFieldPresent</c>: whether the dotted proto field path is populated on the message.</summary>
    internal static bool PawnsFieldPresent(IMessage message, string path)
    {
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            var parent = message.Descriptor.FindFieldByName(path[..dot]);
            if (parent is null || parent.FieldType != FieldType.Message || parent.IsRepeated || !FieldPopulated(parent, message))
            {
                return false;
            }
            return PawnsFieldPresent((IMessage)parent.Accessor.GetValue(message), path[(dot + 1)..]);
        }
        var field = message.Descriptor.FindFieldByName(path);
        return field is not null && FieldPopulated(field, message);
    }

    private static bool FieldPopulated(FieldDescriptor field, IMessage message)
    {
        var accessor = field.Accessor;
        if (field.IsRepeated)
        {
            return accessor.GetValue(message) is System.Collections.ICollection { Count: > 0 };
        }
        if (field.HasPresence)
        {
            return accessor.HasValue(message);
        }
        return accessor.GetValue(message) switch
        {
            null => false,
            string s => s.Length != 0,
            bool b => b,
            ByteString bytes => bytes.Length != 0,
            Enum e => Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture) != 0,
            IConvertible n => Convert.ToDouble(n, System.Globalization.CultureInfo.InvariantCulture) != 0,
            _ => true,
        };
    }
}
