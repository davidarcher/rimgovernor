using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using DomainWire = RimGovernor.Brain.Domain.Wire;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>storage/yard_shelf</c> (#3240): a shelf on unroofed ground, configured like the materials yard, takes an
/// outdoor-safe and a deteriorating stack, the census counts both, and the shelf stops the deteriorating stack wearing
/// while forbidden controls of the same def on the open ground beside it wear. A unit test cannot prove it: native
/// hauling to the shelf and native deterioration (<c>preventDeteriorationOnTop</c> under an open sky) are simulation.
/// </summary>
internal static class StorageYardShelf
{
    // The deteriorating def: Cloth wears 4 HP a day outdoors (80 max HP, so one HP is a visible 0.0125 step).
    private const string Wearing = "Cloth";

    private const string Safe = "Steel";

    // Forbidden Cloth stacks on open ground: with 3 of them at about 2 HP each in the window, all three staying sound
    // has odds near e^-6, so the window is long enough to see wear where nothing protects.
    private const int Controls = 3;

    // The vanilla haul of two stacks a few cells away by idle colonists.
    private const int HaulWindow = Wait.TicksPerDay / 4;

    private const int HaulStep = 300;

    // The wear window after both stacks are on the shelf: 12 game hours.
    private const int WearWindow = 12 * Wait.TicksPerHour;

    private const int WearStep = 2 * Wait.TicksPerHour;

    internal static AcceptanceCase Case() => new()
    {
        Name = "storage/yard_shelf",
        Scope = "Native shelf in the unroofed yard (#3240, epic #3227): on the lab, a wooden Shelf on open unroofed ground set like the materials "
            + "yard (the outdoorSafeWaste preset at the yard's Low priority, plus Cloth so a deteriorating def is admitted) receives a loose Steel "
            + "and a loose Cloth stack by idle colonists' own hauling; the cells read reports the shelf unroofed, 3 stacks of capacity per cell "
            + "and 2 stacks held (#3233); over 12 game hours the shelved Cloth does not wear while forbidden Cloth controls on open ground do. "
            + "Hauling and deterioration are native simulation no snapshot test covers.",
        Start = new LabStart(),
        RequiredOps = [DebugGame.LabSpawnTool],
        QuietWorld = true,
        VanillaColonists = true,
        Reason = "VanillaColonists: idle colonists' own vanilla hauling carries the stacks onto the shelf, the native path the case checks.",
        Budget = TimeSpan.FromMinutes(6),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private sealed record Item(string Def, uint Count, bool Forbidden, float Deterioration, Cell Cell);

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var (cx, cz) = CaseSupport.Center(s);

        // The 2x1 shelf covers (x, z) and (x + 1, z) facing north; the loose stacks lie a few cells off, the controls further.
        var shelfCells = new[] { new Cell(cx + 4, cz + 6), new Cell(cx + 5, cz + 6) };
        var (shelf, _) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = "Shelf", Stuff = "WoodLog", X = shelfCells[0].X, Z = shelfCells[0].Z }, cancellationToken).ConfigureAwait(false);
        var (_, steelReply) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = Safe, X = cx + 1, Z = cz + 3, Count = 50 }, cancellationToken).ConfigureAwait(false);
        var (_, clothReply) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = Wearing, X = cx + 2, Z = cz + 3, Count = 50 }, cancellationToken).ConfigureAwait(false);
        var steel = ThingNumber(steelReply, Safe);
        var cloth = ThingNumber(clothReply, Wearing);

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "yard-shelf-acquire", s.Identity, cancellationToken).ConfigureAwait(false);
        var controls = new List<ulong>();
        for (var i = 0; i < Controls; i++)
        {
            var (id, reply) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = Wearing, X = cx - 6 + (2 * i), Z = cz - 6, Count = 50 }, cancellationToken).ConfigureAwait(false);
            controls.Add(ThingNumber(reply, Wearing));
            var key = "yard-shelf-forbid-" + CaseSupport.Tag(i);
            var forbid = await ApplyIntents.ApplyOneAsync(
                h,
                key,
                s.Identity,
                key,
                new JsonObject { ["designate"] = new JsonObject { ["thingId"] = id, ["designation"] = "THING_DESIGNATION_FORBID" } },
                cancellationToken).ConfigureAwait(false);
            if (forbid["applied"].AsMap() is null)
            {
                throw new AcceptanceException($"{key}: the control stack was not forbidden: {forbid.ToJson()}");
            }
        }

        // The yard's settings: its filter preset and priority (StoreStorage's yard), plus Cloth so the shelf admits a deteriorating stack.
        var settings = await ApplyIntents.ApplyOneAsync(
            h,
            "yard-shelf-settings",
            s.Identity,
            "yard-shelf-settings",
            new JsonObject
            {
                ["zone"] = new JsonObject
                {
                    ["zone"] = new JsonObject { ["id"] = shelf },
                    ["stockpile"] = new JsonObject
                    {
                        ["priority"] = DomainWire.Name(StockpilePriority.Low),
                        ["preset"] = "FILTER_PRESET_OUTDOOR_SAFE_WASTE",
                        ["filter"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["thingDef"] = Wearing }) },
                    },
                },
            },
            cancellationToken).ConfigureAwait(false);
        if (settings["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"yard-shelf-settings: the shelf did not take the yard settings: {settings.ToJson()}");
        }

        // Both stacks reach the shelf by vanilla hauling.
        Dictionary<ulong, Item> items;
        for (var advanced = 0; ; advanced += HaulStep)
        {
            items = await ReadAsync(s, "yard-shelf-haul-" + CaseSupport.Tag(advanced), cx, cz, cancellationToken).ConfigureAwait(false);
            if (OnShelf(items, steel, shelfCells) && OnShelf(items, cloth, shelfCells))
            {
                s.Report["haul_ticks"] = advanced;
                break;
            }

            if (advanced >= HaulWindow)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the Steel and Cloth stacks were not both hauled onto the unroofed shelf within {advanced} ticks: {Describe(items, steel, cloth)}"));
            }

            _ = await s.AdvanceAsync(HaulStep, null, cancellationToken).ConfigureAwait(false);
        }

        var grid = await CellsAsync(s, "yard-shelf-census", cx, cz, cancellationToken).ConfigureAwait(false);
        var stacks = 0;
        foreach (var at in shelfCells)
        {
            var cell = grid.Cells().FirstOrDefault(c => c.Cell == at) ?? throw new AcceptanceException($"the cells read holds no shelf cell {at}");
            if (cell.Roofed)
            {
                throw new AcceptanceException($"shelf cell {at} is not read as unroofed");
            }

            if (cell.StorageCapacity != 3)
            {
                throw new AcceptanceException($"shelf cell {at} reads capacity {cell.StorageCapacity.ToString(CultureInfo.InvariantCulture)} stacks, want 3");
            }

            stacks += cell.StorageStacks;
        }

        s.Report["shelf_stacks"] = stacks;
        if (stacks != 2)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the census counts {stacks} stacks on the shelf, want 2 (Steel and Cloth)"));
        }

        // The wear window: the shelved Cloth keeps its hit points while the forbidden controls on open ground wear.
        var start = items[cloth].Deterioration;
        var controlStart = controls.ToDictionary(id => id, id => items.TryGetValue(id, out var c) ? c.Deterioration : throw new AcceptanceException($"control stack {id} is not on the read"));
        for (var advanced = 0; advanced < WearWindow;)
        {
            _ = await s.AdvanceAsync(WearStep, null, cancellationToken).ConfigureAwait(false);
            advanced += WearStep;
            items = await ReadAsync(s, "yard-shelf-wear-" + CaseSupport.Tag(advanced), cx, cz, cancellationToken).ConfigureAwait(false);
            if (!OnShelf(items, cloth, shelfCells))
            {
                throw new AcceptanceException($"the Cloth stack left the shelf after {advanced} ticks: {Describe(items, steel, cloth)}");
            }

            if (items[cloth].Deterioration > start)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the shelved Cloth wore from {start} to {items[cloth].Deterioration} in {advanced} ticks on the unroofed shelf: preventDeteriorationOnTop does not protect it unroofed (report to #3237/#3238)"));
            }
        }

        var worn = controls.Where(id => items.TryGetValue(id, out var c) && c.Deterioration > controlStart[id]).ToList();
        s.Report["shelf_cloth"] = new JsonObject { ["start"] = start, ["end"] = items[cloth].Deterioration };
        s.Report["controls"] = new JsonArray([.. controls.Select(id => (JsonNode)new JsonObject
        {
            ["start"] = controlStart[id],
            ["end"] = items.TryGetValue(id, out var c) ? c.Deterioration : null,
        })]);
        if (worn.Count == 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"inconclusive: none of the {Controls} forbidden Cloth controls on open ground wore in {WearWindow} ticks, so the window does not show the shelf protecting anything"));
        }
    }

    /// <summary>The thing id number of a lab spawn reply's ThingID (<c>def</c> followed by the number), the cells read's thing id.</summary>
    private static ulong ThingNumber(JsonObject reply, string def)
    {
        var thingId = reply["thingId"].AsString();
        return thingId.StartsWith(def, StringComparison.Ordinal) && ulong.TryParse(thingId.AsSpan(def.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new AcceptanceException($"lab spawn of {def}: no thing id number in {reply.ToJson()}");
    }

    private static bool OnShelf(Dictionary<ulong, Item> items, ulong id, Cell[] shelfCells) =>
        items.TryGetValue(id, out var item) && shelfCells.Contains(item.Cell);

    private static string Describe(Dictionary<ulong, Item> items, ulong steel, ulong cloth) =>
        $"steel={(items.TryGetValue(steel, out var a) ? a.ToString() : "absent")} cloth={(items.TryGetValue(cloth, out var b) ? b.ToString() : "absent")}";

    /// <summary>The loose and stored items around the lab center, by thing id, from one cells read.</summary>
    private static async Task<Dictionary<ulong, Item>> ReadAsync(ISession s, string label, int cx, int cz, CancellationToken cancellationToken)
    {
        var grid = await CellsAsync(s, label, cx, cz, cancellationToken).ConfigureAwait(false);
        var items = new Dictionary<ulong, Item>();
        var cells = grid.Cells();
        var things = grid.Things();
        for (var i = 0; i < cells.Count; i++)
        {
            foreach (var thing in things[i].Where(t => t.Category == ThingCategory.Item))
            {
                items[thing.Id] = new Item(thing.Def, thing.Count, thing.Has(ThingMarks.Forbidden), thing.ItemDeterioration, cells[i].Cell);
            }
        }

        return items;
    }

    /// <summary>One <c>observations_get_cells</c> read of the square around the lab center that holds the case's things.</summary>
    private static async Task<MapCellGrid> CellsAsync(ISession s, string label, int cx, int cz, CancellationToken cancellationToken)
    {
        var reply = await s.Harness.WireAsync(
            label,
            "observations_get_cells",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() },
                ["rectangle"] = new JsonObject
                {
                    ["minimum"] = new JsonObject { ["x"] = cx - 10, ["z"] = cz - 10 },
                    ["maximum"] = new JsonObject { ["x"] = cx + 10, ["z"] = cz + 10 },
                },
            },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        return CellReads.DecodeCells(observed).Grid;
    }
}
