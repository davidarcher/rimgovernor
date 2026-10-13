using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Observations;
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
        if (cell is null || !cell.HasX || !cell.HasZ)
        {
            value = default;
            return false;
        }
        value = new DomainCell(cell.X, cell.Z);
        return true;
    }

    /// <summary>Go's <c>validRef</c>: a reference with a valid id and no unknown fields.</summary>
    internal static bool ValidRef(Ref? reference) =>
        reference is not null && reference.HasId && IsValidId(reference.Id) && !UnknownFields.Present(reference);

    /// <summary>Go's <c>optionalRef</c>: an absent reference or a valid one.</summary>
    internal static bool OptionalRef(Ref? reference) => reference is null || ValidRef(reference);

    /// <summary>Go's <c>uniqueRef</c>: a valid reference not yet in <paramref name="seen"/>, which it joins.</summary>
    internal static bool UniqueRef(Ref? reference, ISet<string> seen) => ValidRef(reference) && seen.Add(reference!.Id);

    /// <summary>Go's <c>RefIDs</c>: the ids of the references, in order.</summary>
    public static IReadOnlyList<string> RefIds(IEnumerable<Ref> references) => [.. references.Select(reference => reference.Id)];

    /// <summary>Go's <c>combatNumber</c>: an absent number or a finite one, non-negative when asked.</summary>
    internal static bool CombatNumber(double? value, bool nonnegative) =>
        value is not { } v || (double.IsFinite(v) && (!nonnegative || v >= 0));

    /// <summary>Go's <c>fraction</c>: a present finite number in 0..1.</summary>
    internal static bool Fraction(double? value) => value is { } v && double.IsFinite(v) && v is >= 0 and <= 1;

    /// <summary>Go's <c>validQuality</c>.</summary>
    internal static bool ValidQuality(int quality) => quality is >= 0 and <= 6;

    /// <summary>Go's <c>buildingUnknown</c>: rejects a message, or one inside it, that carries fields its type lacks.</summary>
    internal static void BuildingUnknown(Google.Protobuf.IMessage? message)
    {
        if (message is null)
        {
            throw Contract("missing building message");
        }
        UnknownFields.Reject(message, "building");
    }

    /// <summary>Go's <c>healthIssue</c> and <c>surgeryIssue</c>: whether a read issue names the field.</summary>
    internal static bool IssueNames(IEnumerable<ReadIssue> issues, string field) => issues.Any(issue => issue.Field == field);

    /// <summary>Go's <c>failedFields</c>: the fields the read issues name.</summary>
    internal static HashSet<string> FailedFields(IEnumerable<ReadIssue> issues) => new(issues.Select(issue => issue.Field), StringComparer.Ordinal);
}
