using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using DomainCell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Brain.Bridge.Contracts;

// The small lifting helpers every wire-to-policy projection shares.
public static class FactHelpers
{
    /// <summary>An optional wire field as a fact, unknown when absent.</summary>
    internal static Fact<T> OptionalFact<T>(bool present, T value) => present ? Known(value) : Unknown<T>();

    /// <summary>An optional 32-bit wire field as a fact, unknown when absent.</summary>
    internal static Fact<int> OptionalInt(bool present, int value) => present ? Known(value) : Unknown<int>();

    /// <summary>An absent cell or coordinate reads as zero.</summary>
    internal static DomainCell ToDomainCell(Cell? cell) => new(cell?.X ?? 0, cell?.Z ?? 0);

    /// <summary>The cell, false when it or a coordinate is absent.</summary>
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

    /// <summary>The ids of the references, in order.</summary>
    public static IReadOnlyList<string> RefIds(IEnumerable<Ref> references) => [.. references.Select(reference => (reference.Id ?? ""))];

    /// <summary>Rejects a missing request message.</summary>
    internal static void BuildingUnknown(object? message)
    {
        if (message is null)
        {
            throw BridgeContractException.Contract("missing building message");
        }
    }

    /// <summary>Whether a read issue names the field.</summary>
    internal static bool IssueNames(IEnumerable<ReadIssue> issues, string field) => issues.Any(issue => (issue.Field ?? "") == field);

    /// <summary>The fields the read issues name.</summary>
    internal static HashSet<string> FailedFields(IEnumerable<ReadIssue> issues) => new(issues.Select(issue => (issue.Field ?? "")), StringComparer.Ordinal);
}
