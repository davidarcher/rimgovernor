using System.Diagnostics.CodeAnalysis;

namespace RimGovernor.Brain.Domain;

/// <summary>One packed cargo row: a native definition and a count.</summary>
public readonly record struct CargoItem(string Definition, ulong Count);

/// <summary>
/// Explicit intent to form and send one already-selected crew, carrying an already-composed pack (journey food
/// included, by definition), toward one already-scouted world tile. It is a FormCaravanIntent on Actions/Apply:
/// native checks the crew, cargo, home staffing, route, mass and a day of food when it applies. Crew and cargo
/// are canonical: sorted ordinally and free of duplicates.
/// </summary>
public sealed record CaravanDeparture : IActionPayload
{
    /// <summary>Go <c>NewCaravanDeparture</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public CaravanDeparture(IEnumerable<PawnId> crew, IEnumerable<CargoItem> cargo, int destinationTile)
    {
        var error = Build(crew, cargo, destinationTile, out var crewRows, out var cargoRows);
        if (error is not null)
        {
            throw new DomainException(error);
        }
        Crew = crewRows;
        Cargo = cargoRows;
        DestinationTile = destinationTile;
    }

    /// <summary>The sorted, deduplicated crew.</summary>
    public EquatableArray<PawnId> Crew { get; }

    /// <summary>The sorted, deduplicated cargo.</summary>
    public EquatableArray<CargoItem> Cargo { get; }

    public int DestinationTile { get; }

    /// <summary>The non-throwing form of the constructor (Go's <c>err != nil</c> as a predicate).</summary>
    public static bool TryCreate(IEnumerable<PawnId> crew, IEnumerable<CargoItem> cargo, int destinationTile, [NotNullWhen(true)] out CaravanDeparture? departure)
    {
        if (Build(crew, cargo, destinationTile, out _, out _) is not null)
        {
            departure = null;
            return false;
        }
        departure = new CaravanDeparture(crew, cargo, destinationTile);
        return true;
    }

    private static string? Build(
        IEnumerable<PawnId> crew,
        IEnumerable<CargoItem> cargo,
        int destinationTile,
        out EquatableArray<PawnId> crewRows,
        out EquatableArray<CargoItem> cargoRows
    )
    {
        crewRows = default;
        cargoRows = default;
        var rows = crew.OrderBy(p => p.Value, StringComparer.Ordinal).ToList();
        if (rows.Count == 0 || destinationTile < 0)
        {
            return "caravan departure requires a nonempty crew and a valid destination tile";
        }
        var seenCrew = new HashSet<PawnId>();
        foreach (var pawn in rows)
        {
            if (!pawn.IsValid || !seenCrew.Add(pawn))
            {
                return "invalid or duplicate caravan crew pawn";
            }
        }
        var items = cargo.OrderBy(i => i.Definition, StringComparer.Ordinal).ToList();
        var seenCargo = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!Identity.IsValidText(item.Definition) || item.Count == 0 || item.Count > int.MaxValue || !seenCargo.Add(item.Definition))
            {
                return "invalid or duplicate caravan cargo item";
            }
        }
        crewRows = EquatableArray.From(rows);
        cargoRows = EquatableArray.From(items);
        return null;
    }
}

public sealed partial record DomainAction
{
    public static DomainAction NewCaravanDepartureAction(ActionId id, CaravanDeparture departure) => Create(id, ActionKind.CaravanDeparture, departure);

    public CaravanDeparture? CaravanDeparture => PayloadOf<CaravanDeparture>(ActionKind.CaravanDeparture);
}
