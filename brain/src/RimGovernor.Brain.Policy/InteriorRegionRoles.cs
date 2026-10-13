using System.Globalization;

namespace RimGovernor.Brain.Policy;

// The wants of each role the region placer furnishes. A want's count is the capacity its template had on the
// same room (a bench row along the entrance wall, an aisle with a piece each side); the placer seats as many
// as the floor holds, and a want's minimum is what the role cannot do without. Pieces worked from the floor in
// front of them (a bench, a stove) stand against a wall, as their template stood them on the back wall.
public static partial class PolicyConstants
{
    /// <summary>The input shelf beside a bench. It is no facility (no row links it), so the catalog has no rule that names it.</summary>
    internal const string WorkshopShelfDef = "ShelfSmall";

    /// <summary>Names the analyzer's slot.</summary>
    internal const string LabAnalyzerSlot = "analyzer";

    /// <summary>A table unit: end chair, 1x2 table, end chair along the centre line, with side chairs either side of the table.</summary>
    internal const int DiningUnitDepth = 4;

    /// <summary>Space stat term: each interior tile adds this.</summary>
    internal const double BedroomSpacePerTile = 1.4;

    /// <summary>Space stat term: each tile a blocking piece stands on takes this off again.</summary>
    internal const double BedroomSpacePerBlocked = 0.9;

    internal const double BedroomMinSpace = 12.5;

    /// <summary>The smallest interior that holds the full set; smaller rooms get the bed and end table only.</summary>
    internal const int BedroomFullSetTiles = 16;
}

public static partial class PolicyFunctions
{
    private static readonly IReadOnlyList<RegionWant> NoWants = [];

    /// <summary>
    /// Benches, a tool cabinet between each two, an input shelf per bench. A cabinet feeds the two benches
    /// beside it (the facility's maxSimultaneous 2), and shelves score 1 each toward Storeroom against 27 per
    /// bench toward Workshop, so one shelf per bench never flips the room's role.
    /// </summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) WorkshopWants(InteriorRoom room, InteriorPieceDef piece, RegionFrame f)
    {
        var (bench, ok) = room.BenchDef(piece, RoomRole.Workshop);
        var (cabinet, cok) = room.Shapes.Get(room.Shapes.Furniture.Cabinet.Def);
        var (shelf, sok) = room.Shapes.Get(InteriorDefs().ShelfDef);
        var n = BenchRowCapacity(f, bench, 1);
        if (!ok || !cok || !sok || n < 1)
        {
            return (NoWants, false);
        }
        var benches = ShapeWant("benches", bench, n, 1, Numbered("bench", 0)) with { AgainstWall = true };
        List<RegionWant> wants = [benches, .. room.WorkChair(benches, bench)];
        wants.Add(ShapeWant("cabinets", cabinet, n - 1, 0, Numbered("cabinet", 0)));
        wants.Add(ShapeWant("shelves", shelf, n, 0, Numbered("shelf", 0)));
        return (wants, true);
    }

    /// <summary>
    /// The want for the work chair chosen for a bench definition (InteriorRoom.WorkChairs): one on each
    /// bench's interaction cell, optional. It is empty when none is chosen, the catalog has no 1x1 row for the
    /// chair or the bench is not worked from a cell of its own, which leaves the room as it was.
    /// </summary>
    internal static IReadOnlyList<RegionWant> WorkChair(this InteriorRoom r, RegionWant benches, InteriorPieceDef bench)
    {
        var (chair, ok) = r.Shapes.Get(r.WorkChairs.GetValueOrDefault(bench.Def) ?? string.Empty);
        if (!ok || bench.Interaction is null || chair.Size != new Cell(1, 1))
        {
            return NoWants;
        }
        return [new RegionWant { Slot = benches.Slot + "_chair", Def = chair.Def, Size = chair.Size, Count = benches.Count, OnInteraction = benches.Slot }];
    }

    /// <summary>
    /// Research benches and the analyzer they share. The analyzer is optional: it links only the benches
    /// within its reach, which the search prices as links.
    /// </summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) LaboratoryWants(InteriorRoom room, InteriorPieceDef piece, RegionFrame f)
    {
        var (bench, ok) = room.BenchDef(piece, RoomRole.Laboratory);
        var n = BenchRowCapacity(f, bench, 1);
        if (!ok || n < 1)
        {
            return (NoWants, false);
        }
        var benches = ShapeWant("benches", bench, n, 1, Numbered("bench", 1)) with { AgainstWall = true };
        List<RegionWant> wants = [benches, .. room.WorkChair(benches, bench)];
        var (analyzer, aok) = room.Shapes.Get(room.Shapes.Furniture.Analyzer.Def);
        if (aok)
        {
            wants.Add(ShapeWant("analyzer", analyzer, 1, 0, _ => LabAnalyzerSlot));
        }
        return (wants, true);
    }

    /// <summary>
    /// Stoves, at most InteriorTable.KitchenStoves. A kitchen's second door is its freezer's; where the stoves
    /// stand against the walls is the search's to price by the walks between stove, freezer door and storage.
    /// </summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) KitchenWants(InteriorRoom room, InteriorPieceDef piece, RegionFrame f)
    {
        var (stove, ok) = room.BenchDef(piece, RoomRole.Kitchen);
        if (!ok || !FrontInteraction(stove) || Math.Min(f.Width, f.Depth) < stove.Size.Z + 2)
        {
            return (NoWants, false);
        }
        var n = Math.Min(RowCapacity(Math.Max(f.Width, f.Depth), stove.Size.X, 0), InteriorDefs().KitchenStoves);
        if (n < 1)
        {
            return (NoWants, false);
        }
        var stoves = ShapeWant("stoves", stove, n, 1, i => string.Create(CultureInfo.InvariantCulture, $"stove.{(char)('1' + i)}")) with { AgainstWall = true };
        return ([stoves, .. room.WorkChair(stoves, stove)], true);
    }

    /// <summary>
    /// A double-sided room (the tomb, the jail): one piece on each side of a 1-cell aisle straight in from the
    /// door, one every piece-depth, the piece's heads against the side walls. The names are the rows' and
    /// sides' (prefix.w1 ... prefix.e1 ...).
    /// </summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) AisleWants(string slot, string prefix, InteriorPieceDef d, RegionFrame f)
    {
        // Turned to face the aisle a piece is Size.Z cells wide and Size.X deep.
        var reach = d.Size.Z;
        var names = new List<string>();
        foreach (var v in AisleRows(f.Depth, d.Size.X))
        {
            if (v + d.Size.X > f.Depth)
            {
                continue;
            }
            if (f.Entrance >= reach)
            {
                names.Add($"{prefix}.w{v + 1}");
            }
            if (f.Entrance + 1 + reach <= f.Width)
            {
                names.Add($"{prefix}.e{v + 1}");
            }
        }
        if (names.Count == 0)
        {
            return (NoWants, false);
        }
        var w = ShapeWant(slot, d, names.Count, 1, i => names[i]) with { AgainstWall = true };
        return ([w], true);
    }

    /// <summary>Sarcophagi along both sides of the aisle.</summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) TombWants(InteriorRoom room, InteriorPieceDef _, RegionFrame f)
    {
        var (sarcophagus, ok) = room.Shapes.Get(room.Shapes.Furniture.Sarcophagus);
        return ok ? AisleWants("sarcophagi", "sarcophagus", sarcophagus, f) : (NoWants, false);
    }

    /// <summary>Prisoner beds along both sides of the aisle.</summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) JailWants(InteriorRoom room, InteriorPieceDef _, RegionFrame f)
    {
        var (bed, ok) = room.Shapes.Get(room.Shapes.Furniture.PrimaryBed());
        return ok ? AisleWants("beds", "bed", bed, f) : (NoWants, false);
    }

    /// <summary>
    /// Beds, heads against a wall, in pairs that share a vitals monitor between them (a monitor must stand
    /// adjacent to the bed it serves and links every adjacent bed).
    /// </summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) HospitalWants(InteriorRoom room, InteriorPieceDef _, RegionFrame f)
    {
        var (bed, bok) = room.Shapes.Get(room.Shapes.Furniture.PrimaryBed());
        var (monitor, mok) = room.Shapes.Get(room.Shapes.Furniture.Monitor.Def);
        if (!bok || !mok || f.Depth < bed.Size.Z + 1)
        {
            return (NoWants, false);
        }
        var pairs = RowCapacity(f.Width, (2 * bed.Size.X) + monitor.Size.X, 1);
        if (pairs < 1)
        {
            return (NoWants, false);
        }
        var beds = ShapeWant("beds", bed, 2 * pairs, 1, Numbered("bed", 1)) with { AgainstWall = true };
        return ([beds, ShapeWant("monitors", monitor, pairs, 0, Numbered("monitor", 1))], true);
    }

    /// <summary>A want for one piece called name.</summary>
    internal static RegionWant NamedPiece(string name, InteriorPieceDef d, int min) => ShapeWant(name, d, 1, min, _ => name);

    /// <summary>The bed a bedroom plans around: the requested bed, else the room's standing one, else the primary bed.</summary>
    internal static (InteriorPieceDef Def, bool Ok) SleepingBed(this InteriorRoom r, InteriorPieceDef piece)
    {
        if (piece.Family == RoomRole.Bedroom)
        {
            return (piece, true);
        }
        foreach (var d in r.Standing)
        {
            var s = r.Shapes.Standing(d);
            if (s.Family == RoomRole.Bedroom)
            {
                return (s, true);
            }
        }
        return r.Shapes.Get(r.Shapes.Furniture.PrimaryBed());
    }

    /// <summary>
    /// The levers beside a bed or throne: an end table, a dresser and a standing lamp, each in its slot and
    /// each optional (the room keeps its bed or throne when one does not fit); skip names the ones a room
    /// leaves out. A catalog without a row for one has no plan.
    /// </summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) FurnishingWants(InteriorRoom room, Func<string, InteriorPieceDef, bool>? skip)
    {
        var (table, tok) = room.Shapes.Get(room.Shapes.Furniture.EndTable.Def);
        var (dresser, dok) = room.Shapes.Get(room.Shapes.Furniture.Dresser.Def);
        var (lamp, lok) = room.Shapes.Get(InteriorDefs().LampDef);
        if (!tok || !dok || !lok)
        {
            return (NoWants, false);
        }
        var output = new List<RegionWant>();
        foreach (var (slot, d) in new[] { ("end_table", table), ("dresser", dresser), ("lamp", lamp) })
        {
            if (skip is null || !skip(slot, d))
            {
                output.Add(NamedPiece(slot, d, 0));
            }
        }
        return (output, true);
    }

    /// <summary>
    /// The bed, then an end table, a dresser and a lamp while the room keeps the Space stat the bedroom's
    /// quality needs (BedroomSpace). The small rooms of the first tiers hold the bed and end table only
    /// (BedroomFullSetTiles). Only the bed is required: a cramped room gets a bare bed rather than no plan. The
    /// end table links only beside the bed's head and the dresser within 6 cells: the search prices both as
    /// facility links.
    /// </summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) BedroomWants(InteriorRoom room, InteriorPieceDef piece, RegionFrame _)
    {
        var (bed, ok) = room.SleepingBed(piece);
        if (!ok)
        {
            return (NoWants, false);
        }
        var area = room.Floor().Area();
        var blocked = bed.Size.X * bed.Size.Z;
        var (furniture, fok) = FurnishingWants(room, (slot, d) =>
        {
            var cells = d.Size.X * d.Size.Z;
            if ((slot != "end_table" && area < BedroomFullSetTiles) || BedroomSpace(area, 1, blocked + cells) < BedroomMinSpace)
            {
                return true;
            }
            blocked += cells;
            return false;
        });
        if (!fok)
        {
            return (NoWants, false);
        }
        return ([NamedPiece("bed", bed, 1), .. furniture], true);
    }

    /// <summary>
    /// The throne when one is being placed, the title's required pieces (each in its slot, left out when no
    /// wall cell fits), then the bedroom's furnishing levers. Asked for no piece, the plan is the furniture
    /// alone, around the cell the throne stands on.
    /// </summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) ThroneWants(InteriorRoom room, InteriorPieceDef piece, RegionFrame _)
    {
        var wants = new List<RegionWant>();
        if (piece.Def != string.Empty && piece.Size.X > 0 && piece.Size.Z > 0)
        {
            wants.Add(NamedPiece(ThroneSlot, piece, 1) with { AgainstWall = true });
        }
        foreach (var req in room.Required)
        {
            if (req.Piece.Size.X > 0 && req.Piece.Size.Z > 0)
            {
                wants.Add(NamedPiece(req.Slot, req.Piece, 0) with { AgainstWall = true });
            }
        }
        var (furniture, ok) = FurnishingWants(room, null);
        wants.AddRange(furniture);
        return (wants, ok);
    }

    /// <summary>As many of the piece as the template's bands held on this room, one slot each ("child.0", ...); a room asked for no piece plans nothing.</summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) ChildRoomWants(InteriorRoom _, InteriorPieceDef piece, RegionFrame f)
    {
        var n = ChildSlots(f.Width, f.Depth, f.Entrance, piece.Size).Count;
        if (piece.Def == string.Empty || n == 0)
        {
            return (NoWants, false);
        }
        return ([ShapeWant("child", piece, n, 1, Numbered("child", 0))], true);
    }

    /// <summary>
    /// The research table, a crafting spot, the campfires and coolers the climate asks for, then one bunk per
    /// occupant (every bunk that fits for 0). A piece is planned only for a capability the room hosts, and
    /// only the bunks matter: a cramped room, or a catalog without a row for a piece, gets the bunks alone
    /// rather than no plan. A bunk is a 1x2 whatever it is made of (a sleeping spot, a bed, a bedroll), the
    /// slot a spot and its bed share.
    /// </summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) ShelterWants(InteriorRoom room, InteriorPieceDef piece, RegionFrame f)
    {
        bool Hosts(Capability c) => room.Capabilities.Contains(c);
        var wants = new List<RegionWant>();
        var (bench, bok) = room.BenchDef(piece, RoomRole.Laboratory);
        if (bok && Hosts(Capability.Research) && BenchRowCapacity(f, bench, 1) >= 1)
        {
            var research = NamedPiece("research", bench, 0) with { AgainstWall = true };
            wants.Add(research);
            wants.AddRange(room.WorkChair(research, bench));
        }
        void Single(string slot, string def, int count, Func<int, string> name)
        {
            var (shape, ok) = room.Shapes.Get(def);
            if (ok && shape.Size == new Cell(1, 1) && count > 0)
            {
                wants.Add(ShapeWant(slot, shape, count, 0, name));
            }
        }
        if (Hosts(Capability.Craft))
        {
            Single("craft", ShelterCraftingDef, 1, _ => "craft");
        }
        if (Hosts(Capability.Heat))
        {
            Single("campfire", ShelterHeatDef, room.Campfires, Numbered("campfire", 1));
        }
        Single("cooler", ShelterCoolDef, room.Coolers, Numbered(ShelterCoolerSlotPrefix[..^1], 1));
        var bunkDef = room.Shapes.Furniture.PrimaryBed();
        if (piece.Family == RoomRole.Bedroom && piece.Size == ShelterBunkSize)
        {
            bunkDef = piece.Def;
        }
        if (bunkDef == string.Empty)
        {
            bunkDef = SleepingSpotDefinition;
        }
        var n = room.Occupants;
        if (n <= 0)
        {
            n = room.Floor().Area() / 2;
        }
        var bunk = new InteriorPieceDef(bunkDef, ShelterBunkSize, default, null);
        wants.Add(ShapeWant("bunk", bunk, n, 0, Numbered(ShelterBunkSlotPrefix[..^1], 1)));
        return (wants, true);
    }

    /// <summary>
    /// The animal beds, as many as the barn's bed grid held (an aisle from the door and a bed row between free
    /// rows, HerdGridBeds sizes a barn by it), each tended from a free cell beside it, and the climate heater.
    /// A bed is tended from the cell in front of it (an interaction cell, which the placer keeps open floor and
    /// which beds may share), so every animal can be reached whatever the placer does with the rest of the floor.
    /// </summary>
    internal static (IReadOnlyList<RegionWant> Wants, bool Ok) BarnWants(InteriorRoom room, InteriorPieceDef piece, RegionFrame f)
    {
        var size = piece.Size;
        if (piece.Def == string.Empty || size.X < 1 || size.Z < 1)
        {
            return (NoWants, false);
        }
        var rows = 0;
        for (var v = size.Z; v + size.Z <= f.Depth; v += 2 * size.Z)
        {
            rows++;
        }
        var n = rows * ((f.Entrance / size.X) + ((f.Width - f.Entrance - 1) / size.X));
        if (n == 0)
        {
            return (NoWants, false);
        }
        var beds = ShapeWant("bed", piece, n, 1, Numbered("bed", 1));
        if (beds.Interaction is null)
        {
            beds = beds with { Interaction = new Cell(0, -1) };
        }
        // The heater places first: the beds' tended cells use every cell the beds leave, so the heater would
        // find none after them.
        var wants = new List<RegionWant>();
        var (heater, ok) = room.Shapes.Get(room.Shapes.Furniture.Heater);
        if (ok && heater.Size == new Cell(1, 1))
        {
            wants.Add(NamedPiece(HerdHeaterSlot, heater, 0));
        }
        wants.Add(beds);
        return (wants, true);
    }

    /// <summary>
    /// The throw pin with its clear lane, then tables each ringed by six chairs that face them, then a lamp.
    /// The counts are the template's: a table unit is end chair, 1x2 table, end chair along a line with a side
    /// chair each side of the table, one cell between units, row 0 left for the entrance. A dining room drops
    /// the pin before its first table and a rec room drops tables before its pin, so the first of the two is
    /// the minimum.
    /// </summary>
    internal static RegionRole DiningWants(bool recFirst) => (room, _, f) =>
    {
        var d = room.Dining;
        if (!d.IsValid() || f.Width < 5)
        {
            return (NoWants, false);
        }
        int Units(int depth) => RowCapacity(depth - 1, DiningUnitDepth, 1);
        var pin = f.Depth >= d.Lane + 1;
        var tables = Units(f.Depth);
        if (pin)
        {
            var withPin = Units(f.Depth - d.Lane);
            if (withPin > 0 || recFirst)
            {
                tables = withPin;
            }
            else
            {
                pin = false;
            }
        }
        if (tables == 0 && !pin)
        {
            return (NoWants, false);
        }
        var wants = new List<RegionWant>();
        if (pin)
        {
            var w = NamedPiece("horseshoes", d.Pin, 0) with { Clear = d.Lane - 1, AgainstWall = true };
            if (recFirst)
            {
                w = w with { Min = 1 };
            }
            wants.Add(w);
        }
        for (var i = 0; i < tables; i++)
        {
            var table = NamedPiece($"table.{i}", d.Table, 0) with { Open = true };
            if (i == 0 && !recFirst)
            {
                table = table with { Min = 1 };
            }
            var chairs = ShapeWant($"chair.{i}", d.Chair, 6, 0, Numbered($"chair.{i}", 0)) with { Beside = table.Slot };
            wants.Add(table);
            wants.Add(chairs);
        }
        var (lamp, ok) = room.Shapes.Get(InteriorDefs().LampDef);
        if (ok)
        {
            wants.Add(NamedPiece("lamp", lamp, 0));
        }
        return (wants, true);
    };

    /// <summary>The planned space of a width x depth room with blocked tiles under furniture.</summary>
    internal static double BedroomSpace(int width, int depth, int blocked) =>
        (BedroomSpacePerTile * (width * depth)) - (BedroomSpacePerBlocked * blocked);
}
