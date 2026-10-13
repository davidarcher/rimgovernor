using Google.Protobuf;
using Google.Protobuf.Reflection;
using RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ColonyRow</c>: one colony facts row, a message part or a keyed list's element keys in native order.
/// </summary>
public sealed record ColonyRow(IMessage? Part, IReadOnlyList<string> Order) : IEquatable<ColonyRow>
{
    /// <summary>Go's <c>Equal</c>: compares two rows' facts.</summary>
    public bool Equals(ColonyRow? other)
    {
        if (other is null || (Part is null) != (other.Part is null))
        {
            return false;
        }
        return Part is not null ? Part.Equals(other.Part) : Order.SequenceEqual(other.Order, StringComparer.Ordinal);
    }

    public override int GetHashCode() => Part?.GetHashCode() ?? Order.Count;
}

// Go colony_rows.go: a ColonyFactsSnapshot splits into sections of native rows, one section per sub-section that
// holds keyed rows, and "colony" for the rest. SplitColonyFacts, ParseColonyRow, JoinColonyFacts and
// ColonyRow.MarshalJSON are the mirror's wire (de)serialisation and are not ported; the section names and the row
// type are.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>ColonySection</c>: the root colony facts section.</summary>
    public const string ColonySection = "colony";

    private static readonly string[] ColonyFallbackKeys = ["def_name"];

    private static readonly Lazy<string[]> ColonyLayout = new(() =>
    {
        var sections = new List<string>();
        foreach (var field in ColonyFactsSnapshot.Descriptor.Fields.InFieldNumberOrder())
        {
            if (OwnSection(field))
            {
                sections.Add(ColonySection + "." + field.Name);
            }
        }
        sections.Sort(StringComparer.Ordinal);
        return [ColonySection, .. sections];
    });

    /// <summary>Go's <c>ColonySections</c>: names every colony facts section, the root first.</summary>
    public static IReadOnlyList<string> ColonySections() => [.. ColonyLayout.Value];

    private static bool OwnSection(FieldDescriptor field)
    {
        if (field.FieldType != FieldType.Message || field.IsMap || field.Name == "delta")
        {
            return false;
        }
        return field.IsRepeated ? Keyable(field.MessageType) : HoldsKeyed(field.MessageType, 0);
    }

    /// <summary>Whether a list of <paramref name="message"/> can carry element keys.</summary>
    private static bool Keyable(MessageDescriptor message)
    {
        if (IdField(message) is not null)
        {
            return true;
        }
        foreach (var field in message.Fields.InDeclarationOrder())
        {
            if (field.FieldType == FieldType.Message && !field.IsRepeated && !field.IsMap && IdField(field.MessageType) is not null)
            {
                return true;
            }
        }
        foreach (var name in ColonyFallbackKeys)
        {
            if (message.FindFieldByName(name) is { FieldType: FieldType.String, IsRepeated: false })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether <paramref name="message"/>, through singular message fields, holds a keyable list.</summary>
    private static bool HoldsKeyed(MessageDescriptor message, int depth)
    {
        if (depth > 8)
        {
            return false;
        }
        foreach (var field in message.Fields.InDeclarationOrder())
        {
            if (field.FieldType != FieldType.Message || field.IsMap)
            {
                continue;
            }
            if ((field.IsRepeated && Keyable(field.MessageType)) || (!field.IsRepeated && HoldsKeyed(field.MessageType, depth + 1)))
            {
                return true;
            }
        }
        return false;
    }

    private static FieldDescriptor? IdField(MessageDescriptor message) =>
        message.FindFieldByName("id") is { FieldType: FieldType.String, IsRepeated: false } field ? field : null;
}
