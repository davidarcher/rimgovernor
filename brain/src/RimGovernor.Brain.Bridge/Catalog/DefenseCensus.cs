using RimGovernor.Brain.Bridge.Contracts;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The defense census read off the cell mirror and the definition catalog.

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// The def name the game gives a frame (ThingDefGenerator_Buildings): the mirror lists a frame as the building it
    /// will become, the census as its own def, so a tier never reads as built while only its frame stands.
    /// </summary>
    private const string FrameDefPrefix = "Frame_";

    // A frame's fillPercent (ThingDefGenerator_Buildings.NewFrameDef_Thing); a blueprint has none.
    private const double FrameFill = 0.2;

    // The least fillPercent CoverGrid registers.
    private const double CoverFloor = 0.001;

    // The thing category the game's chunks sit within.
    private const string ChunkCategory = "Chunks";

    /// <summary>
    /// The defense census of <paramref name="region"/> read off the cell mirror (the planning window) and this catalog,
    /// the facts observations_read_defense_site answered natively: the edifice and terrain on each cell, the cell's cover
    /// (the thing of highest fillPercent, as the game's CoverGrid keeps it) with the designator that removes it, whether
    /// a player door stands, and EdgeReachable as a flood over the mirror from the map edge. A cell the mirror does not
    /// hold, or holds without its terrain or walkability, is fogged: unknown. The cells are the projection's own tick.
    /// </summary>
    public IReadOnlyList<Obs.DefenseCell> DefenseSiteFromCells(IReadOnlyList<P.SiteCell> cells, P.Bounds bounds, CellRect region)
    {
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            throw new CatalogException("defense census needs the map bounds");
        }
        if (!CellRect.IsValidDefenseRegion(region) || region.Max.X >= bounds.Width || region.Max.Z >= bounds.Height)
        {
            throw new CatalogException("invalid defense site region");
        }
        int width = bounds.Width, height = bounds.Height;
        var grid = new P.SiteCell?[width * height];
        foreach (var cell in cells)
        {
            var c = cell.Cell;
            if (c.X >= 0 && c.X < width && c.Z >= 0 && c.Z < height)
            {
                grid[(c.Z * width) + c.X] = cell;
            }
        }
        var census = new CensusReader(this);
        var reach = CensusReader.EdgeReachable(grid, width, height);
        var rows = new List<Obs.DefenseCell>((int)region.Cells);
        for (var z = region.Min.Z; z <= region.Max.Z; z++)
        {
            for (var x = region.Min.X; x <= region.Max.X; x++)
            {
                var i = (z * width) + x;
                rows.Add(census.Cell(grid[i], new Domain.Cell(x, z), reach[i]));
            }
        }
        return rows;
    }

    // The traversal of one mirror cell as the census and its flood read it.
    private readonly record struct CensusCell(bool Known, bool Walkable, bool Passable, bool Door);

    // Decodes mirror cells against the catalog; _chunks caches the per-def "is a stone chunk" answer.
    private sealed class CensusReader(DefinitionCatalog catalog)
    {
        private readonly Dictionary<string, bool> _chunks = new(StringComparer.Ordinal);

        // Decodes the traversal facts of one mirror cell. A held cell with an unknown walkability or terrain is not known.
        private static CensusCell Read(P.SiteCell? cell)
        {
            if (cell is null || !cell.Walkable.TryGetValue(out var walkable) || !cell.Terrain.TryGetValue(out var terrain) || !Domain.IdText.IsValidText(terrain))
            {
                return default;
            }
            var passable = true;
            var door = false;
            foreach (var t in cell.Things)
            {
                if (t.Has(P.ThingMarks.Impassable))
                {
                    passable = false;
                }
                if (t.Has(P.ThingMarks.Edifice) && !t.Has(P.ThingMarks.Frame) && t.Category == P.ThingCategory.Building && cell.Doorway.ValueOrDefault)
                {
                    door = true;
                }
            }
            return new CensusCell(true, walkable && passable, passable, door);
        }

        // The native "can reach a map edge without opening doors" (Reachability.CanReachMapEdge with NoPassClosedDoors): a
        // flood over walkable cells from the walkable cells on the map edge, 4-connected as the game's region links are, a
        // door read closed. A cell the mirror does not know conducts: the game's pathing has no fog, so raiders walk the real
        // map through cells we have not seen, and treating them as blocked would call a base sealed that raiders can enter.
        // The fogged cell's own row still asserts nothing: EdgeReachable stays false there and the planner reads it unknown.
        public static bool[] EdgeReachable(P.SiteCell?[] grid, int width, int height)
        {
            var open = new bool[grid.Length];
            for (var i = 0; i < grid.Length; i++)
            {
                var c = Read(grid[i]);
                open[i] = !c.Known || (c.Walkable && !c.Door);
            }
            var reach = new bool[grid.Length];
            var queue = new List<int>();
            void Push(int i)
            {
                if (open[i] && !reach[i])
                {
                    reach[i] = true;
                    queue.Add(i);
                }
            }
            for (var x = 0; x < width; x++)
            {
                Push(x);
                Push(((height - 1) * width) + x);
            }
            for (var z = 0; z < height; z++)
            {
                Push(z * width);
                Push((z * width) + width - 1);
            }
            for (var head = 0; head < queue.Count; head++)
            {
                var i = queue[head];
                int x = i % width, z = i / width;
                if (x > 0)
                {
                    Push(i - 1);
                }
                if (x < width - 1)
                {
                    Push(i + 1);
                }
                if (z > 0)
                {
                    Push(i - width);
                }
                if (z < height - 1)
                {
                    Push(i + width);
                }
            }
            // A door is its own one-cell region and the query's start region is never tested against the traverse mode, so
            // a closed door's own cell reaches the edge when it lies on it or any side neighbour does; the flood never
            // passes through it.
            var doors = new List<int>();
            for (var i = 0; i < grid.Length; i++)
            {
                var c = Read(grid[i]);
                if (!c.Known || !c.Walkable || !c.Door)
                {
                    continue;
                }
                int x = i % width, z = i / width;
                if (x == 0 || z == 0 || x == width - 1 || z == height - 1 || reach[i - 1] || reach[i + 1] || reach[i - width] || reach[i + width])
                {
                    doors.Add(i);
                }
            }
            foreach (var i in doors)
            {
                reach[i] = true;
            }
            return reach;
        }

        // One census row: fogged when the mirror does not know the cell.
        public Obs.DefenseCell Cell(P.SiteCell? cell, Domain.Cell at, bool edgeReachable)
        {
            var view = Read(cell);
            if (!view.Known || cell is null)
            {
                return new Obs.DefenseCell.Fogged { Cell = at };
            }
            P.Thing? edifice = null;
            P.Thing? cover = null;
            var fill = CoverFloor;
            foreach (var t in cell.Things)
            {
                if (t.Has(P.ThingMarks.Edifice))
                {
                    edifice = t;
                }
                if (Fill(t) is var f && f > fill)
                {
                    (cover, fill) = (t, f);
                }
            }
            var kind = cover is null ? null : CoverKindOf(cover);
            return new Obs.DefenseCell.Seen
            {
                Cell = at,
                Terrain = cell.Terrain.ValueOrDefault ?? "",
                Walkable = view.Walkable,
                Passable = view.Passable,
                CoverFill = cover is null ? 0 : Math.Min(fill, 1),
                EdgeReachable = edgeReachable,
                FoundationRemovalDesignated = cell.FoundationRemoval.ValueOrDefault,
                Roofed = cell.Roofed.ValueOrDefault,
                Edifice = edifice is null
                    ? null
                    : new Obs.DefenseEdifice
                    {
                        DefName = EdificeName(edifice),
                        Stuff = edifice.Building?.Stuff,
                        PlayerOwned = edifice.Faction == P.ThingFaction.Player,
                        NaturalRock = edifice.Has(P.ThingMarks.NaturalRock) || catalog.FindThing(edifice.Def)?.mineable == true,
                        Door = view.Door,
                    },
                Cover = cover is null || kind is not { } k
                    ? null
                    : new Obs.DefenseCover { ThingId = ThingLoadId(cover), DefName = EdificeName(cover), Kind = k, Designated = cover.Has(P.ThingMarks.Designated) },
            };
        }

        // The def the game names the thing by: a frame is its own def.
        private static string EdificeName(P.Thing t) => t.Has(P.ThingMarks.Frame) ? FrameDefPrefix + t.Def : t.Def;

        // The game's Thing.GetUniqueLoadID of a listed thing.
        private static string ThingLoadId(P.Thing t) => (t with { Def = EdificeName(t) }).LoadId();

        // The fillPercent CoverGrid sees on a thing: a frame's own, none for a blueprint, else its def's.
        private double Fill(P.Thing t) =>
            t.Has(P.ThingMarks.Blueprint) ? 0 : t.Has(P.ThingMarks.Frame) ? FrameFill : catalog.FindThing(t.Def)?.fillPercent ?? 0;

        // NativeClearCover.KindOf: how the game's designators remove the thing, null for one they cannot (a corpse, filth).
        private Obs.CoverKind? CoverKindOf(P.Thing t)
        {
            if (t.Category == P.ThingCategory.Plant)
            {
                return Obs.CoverKind.Plant;
            }
            if (t.Category == P.ThingCategory.Building && (t.Has(P.ThingMarks.NaturalRock) || catalog.FindThing(t.Def)?.mineable == true) && !t.Has(P.ThingMarks.Frame) && !t.Has(P.ThingMarks.Blueprint))
            {
                return Obs.CoverKind.Mineable;
            }
            if (t.Category == P.ThingCategory.Item)
            {
                return IsChunk(t.Def) ? Obs.CoverKind.Chunk : null;
            }
            return t.Category == P.ThingCategory.Building ? Obs.CoverKind.Building : null;
        }

        private bool IsChunk(string def)
        {
            if (_chunks.TryGetValue(def, out var known))
            {
                return known;
            }
            var chunk = catalog.FindThing(def) is { } row && CategoriesWithin(row).Contains(ChunkCategory);
            _chunks[def] = chunk;
            return chunk;
        }
    }
}
