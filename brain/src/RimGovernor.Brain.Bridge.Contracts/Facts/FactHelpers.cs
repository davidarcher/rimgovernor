using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using DomainCell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go care_conditions.go (fact), policy_inputs.go (optionalFact), surgery_facts.go (surgeryFact, surgeryInt),
// biotech.go (optionalInt), protobuf.go (validRef, optionalRef, RefIDs), combat_pawns.go (combatNumber),
// defense_site.go (protoCell, fraction): the small lifting helpers every wire-to-policy projection shares.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>fact</c>, <c>optionalFact</c> and <c>surgeryFact</c>: an optional wire field as a fact, unknown when absent.</summary>
    internal static Fact<T> OptionalFact<T>(bool present, T value) => present ? Known(value) : Unknown<T>();

    /// <summary>Go's <c>optionalInt</c> and <c>surgeryInt</c>: an optional 32-bit wire field as a fact, unknown when absent.</summary>
    internal static Fact<int> OptionalInt(bool present, int value) => present ? Known(value) : Unknown<int>();

    /// <summary>Go's <c>domain.Cell{X: c.GetX(), Z: c.GetZ()}</c>: an absent cell or coordinate reads as zero.</summary>
    internal static DomainCell ToDomainCell(Cell? cell) => new(cell?.X ?? 0, cell?.Z ?? 0);

    /// <summary>Go's <c>protoCell</c>: the cell, false when it or a coordinate is absent.</summary>
    internal static bool TryProtoCell(Cell? cell, out DomainCell value)
    {
        if (cell is null)
        {
            value = default;
            return false;
        }
        value = new DomainCell(cell.X, cell.Z);
        return true;
    }

    /// <summary>Go's <c>RefIDs</c>: the ids of the references, in order.</summary>
    public static IReadOnlyList<string> RefIds(IEnumerable<Ref> references) => [.. references.Select(reference => (reference.Id ?? ""))];

    /// <summary>Go's <c>buildingUnknown</c>: rejects a missing request message.</summary>
    internal static void BuildingUnknown(object? message)
    {
        if (message is null)
        {
            throw Contract("missing building message");
        }
    }

    /// <summary>Go's <c>healthIssue</c> and <c>surgeryIssue</c>: whether a read issue names the field.</summary>
    internal static bool IssueNames(IEnumerable<ReadIssue> issues, string field) => issues.Any(issue => (issue.Field ?? "") == field);

    /// <summary>Go's <c>failedFields</c>: the fields the read issues name.</summary>
    internal static HashSet<string> FailedFields(IEnumerable<ReadIssue> issues) => new(issues.Select(issue => (issue.Field ?? "")), StringComparer.Ordinal);
}
