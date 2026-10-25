using RimGovernor.Contracts.Wire.Common;
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

}
