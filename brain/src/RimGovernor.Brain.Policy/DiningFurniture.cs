namespace RimGovernor.Brain.Policy;

/// <summary>
/// The furniture the dining and recreation planners place, as the catalog rows name it
/// (DefinitionCatalog.DiningFurniture): the chair and the table the dining template lays out, the recreation
/// foothold it puts against the back wall, and how many cells of floor the foothold's throw lane takes (the pin
/// cell and the cells a thrower stands on toward the entrance). It is a threaded value on the observation,
/// never a global.
/// </summary>
public sealed record DiningFurniture
{
    public InteriorPieceDef Chair { get; init; } = new();

    public InteriorPieceDef Table { get; init; } = new();

    public InteriorPieceDef Pin { get; init; } = new();

    public int Lane { get; init; }

    /// <summary>Reports whether the catalog filled the value in.</summary>
    public bool Known() => Chair.Def != "" && Table.Def != "" && Pin.Def != "";

    /// <summary>Reports whether the definition is the dining chair.</summary>
    public bool IsChair(string def) => def != "" && def == Chair.Def;

    /// <summary>Reports whether the definition is the dining table.</summary>
    public bool IsTable(string def) => def != "" && def == Table.Def;

    /// <summary>The comfort method that builds the table; a census without it cannot name the build.</summary>
    internal ComfortMethod TableMethod()
    {
        DomainException.Require(Table.Def != "", "comfort census names no dining table");
        return new ComfortMethod(Table.Def);
    }

    /// <summary>The comfort method that builds the chair; a census without it cannot name the build.</summary>
    internal ComfortMethod ChairMethod()
    {
        DomainException.Require(Chair.Def != "", "comfort census names no dining chair");
        return new ComfortMethod(Chair.Def);
    }

    /// <summary>The table, the chair and the foothold, in that order.</summary>
    public IReadOnlyList<string> Definitions() => [Table.Def, Chair.Def, Pin.Def];

    /// <summary>Checks the shapes the dining template lays out: a one-cell chair and pin, a table one cell wide and two deep, and a lane that holds the pin.</summary>
    public void Validate()
    {
        DomainException.Require(Known(), "dining furniture names no chair, table or recreation foothold");
        var cell = new Cell(1, 1);
        var table = new Cell(1, 2);
        foreach (var (role, def, size) in new[] { ("chair", Chair, cell), ("table", Table, table), ("recreation foothold", Pin, cell) })
        {
            DomainException.Require(
                def.Size == size,
                $"dining {role} {def.Def} is {def.Size.X}x{def.Size.Z}, the dining template lays out {size.X}x{size.Z}");
        }
        DomainException.Require(Lane >= 1, $"dining recreation foothold {Pin.Def} has a lane of {Lane} cells");
    }
}
