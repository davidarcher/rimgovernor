using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>storage/yard_shelf</c> (#3240): a shelf on unroofed ground, configured like the materials yard, holds an
/// outdoor-safe and a deteriorating stack, the census counts both, and the shelf stops the deteriorating stack wearing
/// while forbidden controls of the same def on the open ground beside it wear. A unit test cannot prove it: native
/// deterioration (<c>preventDeteriorationOnTop</c> under an open sky) is simulation. The stacks are staged on the shelf;
/// vanilla hauling them there is the game's.
/// </summary>
internal static class StorageYardShelf
{
    // The deteriorating def: Cloth wears 4 HP a day outdoors (80 max HP, so one HP is a visible 0.0125 step).
    private const string Wearing = "Cloth";

    private const string Safe = "Steel";

    // The wear window: 12 game hours. Three forbidden open-ground controls at about 2 HP each all staying sound has odds
    // near e^-6, so the window is long enough to see wear where nothing protects.
    private const int WearWindow = 12 * Wait.TicksPerHour;

    private const int WearStep = 2 * Wait.TicksPerHour;

    // The 2x1 shelf facing north covers (2, 5) and (3, 5).
    private static readonly Fx.StageCell[] ShelfCells = [new(2, 5), new(3, 5)];

    internal static TypedCase Case() => new()
    {
        Name = "storage/yard_shelf",
        Scope = "Native shelf in the unroofed yard (#3240, epic #3227): a staged wooden Shelf on open unroofed ground holding a Steel and a "
            + "Cloth stack takes the materials yard's settings (outdoorSafeWaste preset at Low priority, plus Cloth); the cells read reports "
            + "the shelf unroofed, 3 stacks of capacity per cell and 2 stacks held (#3233); over 12 game hours the shelved Cloth stays on "
            + "the shelf without wearing while at least one of three forbidden Cloth controls on open ground wears.",
        Ticks = WearWindow,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 10,
                Height = 8,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 8,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed, Fx.StageGround.Unzoned],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 10, 8), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Roofs, Fx.StageClearKind.Zones] }],
            Buildings = [new Fx.StageBuilding { Name = "shelf", Def = "Shelf", Stuff = "WoodLog", Cells = [ShelfCells[0]] }],
            Things =
            [
                new Fx.StageThing { Name = "steel", Def = Safe, Count = 50, Cell = ShelfCells[0] },
                new Fx.StageThing { Name = "cloth", Def = Wearing, Count = 50, Cell = ShelfCells[1] },
                new Fx.StageThing { Name = "control-0", Def = Wearing, Count = 50, Cell = new(1, 1), Forbidden = true },
                new Fx.StageThing { Name = "control-1", Def = Wearing, Count = 50, Cell = new(4, 1), Forbidden = true },
                new Fx.StageThing { Name = "control-2", Def = Wearing, Count = 50, Cell = new(7, 1), Forbidden = true },
            ],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("stage: no site");
        var shelf = staged.Buildings["shelf"].Ids.Single();
        var cloth = Number(staged.Things["cloth"].Ids.Single(), Wearing);
        var steel = Number(staged.Things["steel"].Ids.Single(), Safe);
        var controls = Enumerable.Range(0, 3).Select(i => Number(staged.Things["control-" + i.ToString(CultureInfo.InvariantCulture)].Ids.Single(), Wearing)).ToList();
        var shelfCells = ShelfCells.Select(c => new Cell(site.X + c.X, site.Z + c.Z)).ToArray();

        // The yard's settings: its filter preset and priority (StoreStorage's yard), plus Cloth so the shelf admits a deteriorating stack.
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "yard-shelf-settings", new RimGovernor.Brain.GameOrders.Operations.ZoneIntent
        {
            Zone = new() { Id = shelf },
            Stockpile = new RimGovernor.Brain.GameOrders.Operations.StockpileSettings
            {
                Priority = new RimGovernor.Brain.GameOrders.Operations.StockpilePriorityChange.Selected(StockpilePriority.Low),
                Preset = new RimGovernor.Brain.GameOrders.Operations.StockpilePresetChange.Selected(FilterBase.OutdoorSafeWaste),
                Filter = new RimGovernor.Brain.GameOrders.Operations.FilterPatch { Allow = [new() { Definition = new RimGovernor.Brain.GameOrders.Operations.FilterSelectorDefinition.ThingDefCase(Wearing) }], HitPoints = new RimGovernor.Brain.GameOrders.Operations.HitPointRange.Preserve() },
            }, Delete = false, RequireCoveredEmpty = false, Kind = RimGovernor.Brain.GameOrders.Operations.ZoneType.Existing }, cancellationToken).ConfigureAwait(false);

        var grid = await TypedApply.CellsAsync(s, "yard-shelf-census", site, cancellationToken).ConfigureAwait(false);
        var stacks = 0;
        foreach (var at in shelfCells)
        {
            var cell = grid.At(at) ?? throw new AcceptanceException($"the cells read holds no shelf cell {at}");
            if (cell.Roofed || cell.StorageCapacity != 3)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"shelf cell {at}: want unroofed with capacity 3, got roofed={cell.Roofed} capacity={cell.StorageCapacity}"));
            }

            stacks += cell.StorageStacks;
        }

        if (stacks != 2)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the census counts {stacks} stacks on the shelf, want 2 (Steel and Cloth)"));
        }

        // The wear window: the shelved Cloth keeps its hit points while the forbidden controls on open ground wear.
        var items = Items(grid);
        var start = Deterioration(items, cloth, "cloth");
        var controlStart = controls.ToDictionary(id => id, id => Deterioration(items, id, "control"));
        for (var advanced = WearStep; advanced <= WearWindow; advanced += WearStep)
        {
            var label = "yard-shelf-wear-" + advanced.ToString(CultureInfo.InvariantCulture);
            await s.RunTicksAsync(label, WearStep, cancellationToken).ConfigureAwait(false);
            items = Items(await TypedApply.CellsAsync(s, label, site, cancellationToken).ConfigureAwait(false));
            if (!items.TryGetValue(cloth, out var shelved) || !shelfCells.Contains(shelved.Cell) || !items.TryGetValue(steel, out var safe) || !shelfCells.Contains(safe.Cell))
            {
                throw new AcceptanceException($"{label}: a shelved stack left the shelf");
            }

            if (shelved.Deterioration > start)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: the shelved Cloth wore from {start} to {shelved.Deterioration} on the unroofed shelf: preventDeteriorationOnTop does not protect it unroofed (report to #3237/#3238)"));
            }
        }

        var worn = controls.Count(id => items.TryGetValue(id, out var c) && c.Deterioration > controlStart[id]);
        s.Record("controls_worn", worn);
        if (worn == 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"inconclusive: none of the forbidden Cloth controls on open ground wore in {WearWindow} ticks, so the window does not show the shelf protecting anything"));
        }
    }

    /// <summary>The thing id number of a staged load id (<c>Thing_</c>, the def, then the number), the cells read's thing id.</summary>
    private static ulong Number(string thingId, string def) =>
        thingId.StartsWith("Thing_" + def, StringComparison.Ordinal) && ulong.TryParse(thingId.AsSpan(6 + def.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new AcceptanceException($"staged {def}: no thing id number in {thingId}");

    private static double Deterioration(Dictionary<ulong, (Cell Cell, double Deterioration)> items, ulong id, string what) =>
        items.TryGetValue(id, out var item) ? item.Deterioration : throw new AcceptanceException($"the {what} stack {id} is not on the read");

    /// <summary>The site's items by thing id, from one cells read.</summary>
    private static Dictionary<ulong, (Cell Cell, double Deterioration)> Items(MapCellGrid grid)
    {
        var items = new Dictionary<ulong, (Cell, double)>();
        var cells = grid.Cells();
        var things = grid.Things();
        for (var i = 0; i < cells.Count; i++)
        {
            foreach (var thing in things[i].Where(t => t.Category == ThingCategory.Item))
            {
                items[thing.Id] = (cells[i].Cell, thing.ItemDeterioration);
            }
        }

        return items;
    }
}
