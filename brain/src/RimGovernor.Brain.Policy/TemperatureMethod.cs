using SleepingTemperature = RimGovernor.Contracts.Wire.Observations.SleepingTemperature;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

public enum TemperatureMethod
{
    [WireName("no_deficit")]
    NoMethod,

    [WireName("enclosed_sleeping_room_needed")]
    ShelterNeeded,

    /// <summary>An eligible sleeping bed stands in no room the census read, so no room temperature judges it.</summary>
    [WireName("sleeping_bed_unroomed")]
    BedsUnroomed,

    [WireName("waiting_for_native_temperature")]
    Wait,

    [WireName("Campfire")]
    Heat,

    [WireName("PassiveCooler")]
    Cool,

    /// <summary>
    /// Places one powered Heater in the coldest sleeping room instead of a campfire when the campfire's fuel is
    /// a finite stock and a network has the spare capacity for the heater's draw (spike #2769: 44 to 164 W
    /// average, a campfire burns 10 wood a day whatever the load).
    /// </summary>
    [WireName("electric_heat")]
    HeatElectric,

    /// <summary>
    /// Places one powered Cooler through a vented wall of the hottest sleeping room, its cold side facing the
    /// room; it outranks the passive cooler when the research is done and a power network has the spare
    /// capacity to run it.
    /// </summary>
    [WireName("Cooler")]
    CoolPowered,

    /// <summary>
    /// Switches a heat campfire's auto-refuel off once its sleeping room reaches its sleepers' comfort
    /// maximum, so the fire burns out.
    /// </summary>
    [WireName("campfire_refuel_off")]
    RefuelOff,

    /// <summary>Switches a heat campfire's auto-refuel on again below its sleepers' comfort minimum (sleepersBand).</summary>
    [WireName("campfire_refuel_on")]
    RefuelOn,
}

/// <summary>A campfire the temperature family built as room heat (its construction claim carries the shelter/temperature filing key): its native room and auto-refuel toggle.</summary>
public sealed record HeatCampfire
{
    public required string Id { get; init; }

    public required string Room { get; init; }

    public required bool AutoRefuel { get; init; }

    /// <summary>The campfire with its native room and toggle; no room heat source outdoors or before the toggle is read.</summary>
    public static HeatCampfire? Of(string id, string? room, RimGovernor.Contracts.Wire.Observations.BenchAutoRefuel autoRefuel) =>
        room is { } r && autoRefuel is RimGovernor.Contracts.Wire.Observations.BenchAutoRefuel.Refuelable { Allowed: var on } ? new HeatCampfire { Id = id, Room = r, AutoRefuel = on } : null;
}

public sealed record TemperatureProposal
{
    public required TemperatureMethod Method { get; init; }

    public MethodId Key { get; init; }

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    /// <summary>The exact wall cell of a powered cooler (CoolPowered); the passive methods search Cells.</summary>
    public Cell Cell { get; init; }

    /// <summary>The facing of a powered cooler (CoolPowered).</summary>
    public Rotation Rotation { get; init; }

    /// <summary>The heat campfire a refuel switch targets.</summary>
    public string Thing { get; init; } = "";
}

/// <summary>
/// The evidence the powered cooler method reads beside the room census: the Cooler planning definition's
/// placeability and draw, the colony power topology and the site cells the wall search walks.
/// </summary>
public sealed record TemperatureCooling
{
    /// <summary>The Cooler planning definition: placeable at a nominal draw, or not placeable.</summary>
    public required ClimateGear Cooler { get; init; }

    /// <summary>The colony power census, or the named state that none was read (the powered methods are then not offered).</summary>
    public required ClimatePower Power { get; init; }

    public required IReadOnlyList<SiteCell> Cells { get; init; }

    /// <summary>The campfires standing as room heat, whose auto-refuel the method switches.</summary>
    public required IReadOnlyList<HeatCampfire> HeatCampfires { get; init; }

    /// <summary>The colonists and slaves whose owned beds and comfortable ranges band each sleeping room.</summary>
    public required IReadOnlyList<SleepingPerson> Sleepers { get; init; }

    /// <summary>The catalog's heater definition (RoomFurniture.Heater); empty when the catalog has none.</summary>
    public required string Heater { get; init; }

    /// <summary>The Heater planning definition: placeable at a nominal draw, or not placeable.</summary>
    public required ClimateGear HeaterGear { get; init; }

    /// <summary>True when the campfire's fuel (wood) is a finite stock here: nothing sows it. A renewable fuel keeps campfire heat.</summary>
    public required bool HeatFuelFinite { get; init; }

    /// <summary>
    /// Whether room holds a working climate piece: a powered heater of the catalog's heater definition standing
    /// on its floor, or a wall cooler beside it. A room that holds one needs no
    /// second.
    /// </summary>
    public bool Conditioned(IReadOnlyList<Cell> cells) => WallCoolerBeside(cells) || HeaterIn(cells);

    /// <summary>Whether a powered heater of the catalog's heater definition stands in room.</summary>
    internal bool HeaterIn(IReadOnlyList<Cell> cells)
    {
        if (Power is not ClimatePower.Census { Topology: var topology } || Heater.Length == 0)
        {
            return false;
        }
        var inside = new HashSet<Cell>(cells);
        foreach (var b in topology.Buildings)
        {
            if (b.Definition != Heater)
            {
                continue;
            }
            if (b.Powered && inside.Contains(b.Cell))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A Cooler in the power census standing on a cell four-adjacent to one of the room's cells: a cooler placed through the room's wall.</summary>
    internal bool WallCoolerBeside(IReadOnlyList<Cell> cells)
    {
        if (Power is not ClimatePower.Census { Topology: var topology })
        {
            return false;
        }
        var inside = new HashSet<Cell>(cells);
        foreach (var b in topology.Buildings)
        {
            if (b.Definition != "Cooler")
            {
                continue;
            }
            foreach (var rotation in new[] { Rotation.North, Rotation.East, Rotation.South, Rotation.West })
            {
                if (inside.Contains(RefrigerationRules.Step(b.Cell, RefrigerationRules.Facing(rotation), 1)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Whether a powered cooler can be proposed at all: research finished, draw known and a network with that much spare.</summary>
    internal bool PoweredCoolerReady() => SpareCovers(Cooler);

    /// <summary>Whether a powered heater is preferred over a campfire: the campfire's fuel is finite, the heater is placeable and a network has the spare capacity for its draw.</summary>
    internal bool ElectricHeatReady() => HeatFuelFinite && Heater.Length != 0 && SpareCovers(HeaterGear);

    private bool SpareCovers(ClimateGear gear) =>
        gear is ClimateGear.Placeable { DrawW: var draw }
        && Power is ClimatePower.Census { Topology: var topology }
        && !topology.Blackout
        && topology.SpareW() >= draw;
}

/// <summary>A climate piece's planning definition: Placeable at a nominal DrawW (researched and available), or NotPlaceable.</summary>
public abstract record ClimateGear
{
    private protected ClimateGear() { }

    public sealed record NotPlaceable : ClimateGear;

    public sealed record Placeable(double DrawW) : ClimateGear;

    /// <summary>The gear a planning definition describes: not placeable while unavailable, else placeable at its nominal draw. An unread availability, or draw of an available piece, raises <see cref="RequiredFactException"/>.</summary>
    public static ClimateGear Of(string name, bool available, double drawW) =>
        !available ? new NotPlaceable() : double.IsFinite(drawW) ? new Placeable(drawW) : throw new RequiredFactException(name + " power draw");
}

/// <summary>The colony power census the powered climate methods read: Census, or NoCensus when the native power section was not read this review.</summary>
public abstract record ClimatePower
{
    private protected ClimatePower() { }

    public sealed record NoCensus : ClimatePower;

    public sealed record Census(PowerTopology Topology) : ClimatePower;

    public static ClimatePower Of(PowerTopology power) => new Census(power);
}

public static partial class TemperatureMethodRules
{
    /// <summary>
    /// The intersection of the comfortable ranges of the people owning a bed in room: apparel, traits and genes
    /// shift each one. Without a sleeper whose range is known banded is false and the band is unknown: nothing is
    /// judged against a range the game did not state.
    /// </summary>
    private static (double Low, double High, bool Banded) SleepersBand(RoomState room, IReadOnlyList<SleepingPerson> sleepers)
    {
        var beds = new HashSet<string>(room.BedIds(), StringComparer.Ordinal);
        var low = double.NegativeInfinity;
        var high = double.PositiveInfinity;
        var banded = false;
        foreach (var p in sleepers)
        {
            if (!beds.Contains(p.OwnedBed))
            {
                continue;
            }
            low = Math.Max(low, p.ComfortableMin);
            high = Math.Min(high, p.ComfortableMax);
            banded = true;
        }
        return banded ? (low, high, true) : (0, 0, false);
    }

    /// <summary>
    /// Holds shelter/temperature unmet past the fixed thresholds: a heat campfire owes its refuel switch, or a
    /// sleeping room with no campfire or heater sits below its sleepers' comfort minimum.
    /// </summary>
    public static bool TemperatureOwed(RoomObservation rooms, SleepingObservation s, IReadOnlyList<HeatCampfire> heatCampfires) =>
        TemperatureOwed(rooms, heatCampfires, [.. s.People, .. s.Slaves]);

    private static bool TemperatureOwed(RoomObservation v, IReadOnlyList<HeatCampfire> heatCampfires, IReadOnlyList<SleepingPerson> sleepers)
    {
        if (CampfireRefuel(v, heatCampfires, sleepers) is not null)
        {
            return true;
        }
        foreach (var room in v.Rooms)
        {
            var temperature = room.TemperatureC;
            var (low, _, banded) = SleepersBand(room, sleepers);
            if (!banded || temperature >= low)
            {
                continue;
            }
            var heated = false;
            foreach (var q in room.Contents)
            {
                heated = heated || (q.Units > 0 && (q.DefName == "Campfire" || q.DefName == "Heater"));
            }
            if (!heated)
            {
                return true;
            }
        }
        return false;
    }

    private static MethodId ThermalKey(string seed)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new MethodId("thermal-" + Convert.ToHexStringLower(digest.AsSpan(0, 12)));
    }

    /// <summary>
    /// The auto-refuel switch a heat campfire in a sleeping room owes: off at its sleepers' comfort maximum or
    /// warmer while it refuels, on below their comfort minimum while it does not (SleepersBand). Null when no
    /// campfire owes a switch.
    /// </summary>
    public static TemperatureProposal? CampfireRefuel(RoomObservation v, IReadOnlyList<HeatCampfire> heatCampfires, IReadOnlyList<SleepingPerson> sleepers)
    {
        var sorted = heatCampfires.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        foreach (var campfire in sorted)
        {
            var on = campfire.AutoRefuel;
            var (room, found) = v.Room(campfire.Room);
            if (!found || room!.Beds.Count == 0)
            {
                continue;
            }
            var temperature = room.TemperatureC;
            var (low, high, banded) = SleepersBand(room, sleepers);
            if (!banded)
            {
                continue;
            }
            var method = TemperatureMethod.NoMethod;
            if (on && temperature >= high)
            {
                method = TemperatureMethod.RefuelOff;
            }
            else if (!on && temperature < low)
            {
                method = TemperatureMethod.RefuelOn;
            }
            if (method == TemperatureMethod.NoMethod)
            {
                continue;
            }
            return new TemperatureProposal
            {
                Method = method,
                Key = ThermalKey(campfire.Id + "/" + Wire.Name(method)),
                Thing = campfire.Id,
            };
        }
        return null;
    }

    /// <summary>
    /// The largest surplus of nominal producer capacity over consumer demand across the topology's connected
    /// power networks, the same capacity-versus-demand reading the power family sizes its budget by; zero when no
    /// network has a producer.
    /// </summary>
    public static double SpareW(this PowerTopology t)
    {
        var nets = new Dictionary<string, (double Capacity, double Demand)>(StringComparer.Ordinal);
        foreach (var b in t.Buildings)
        {
            if (!b.Connected)
            {
                continue;
            }
            var w = b.BaseW;
            nets.TryGetValue(b.Network, out var n);
            if (w > 0)
            {
                n.Capacity += w;
            }
            else
            {
                n.Demand -= w;
            }
            nets[b.Network] = n;
        }
        var spare = 0.0;
        var have = false;
        foreach (var n in nets.Values)
        {
            if (n.Capacity <= 0)
            {
                continue;
            }
            if (!have || n.Capacity - n.Demand > spare)
            {
                spare = n.Capacity - n.Demand;
            }
            have = true;
        }
        return have ? spare : 0;
    }

    public static void Validate(this RoomObservation v)
    {
        var seenBed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in v.EligibleBeds)
        {
            DomainException.Require(FoodForecastRules.FoodId(id) && seenBed.Add(id), "invalid sleeping bed census");
        }
        var rooms = new HashSet<string>(StringComparer.Ordinal);
        var beds = new HashSet<string>(StringComparer.Ordinal);
        var cells = new HashSet<Cell>();
        foreach (var room in v.Rooms)
        {
            DomainException.Require(FoodForecastRules.FoodId(room.Id) && rooms.Add(room.Id), "invalid temperature room");
            DomainException.Require(double.IsFinite(room.TemperatureC), "invalid room temperature");
            foreach (var id in room.BedIds())
            {
                DomainException.Require(FoodForecastRules.FoodId(id) && beds.Add(id), "ambiguous sleeping room");
            }
            foreach (var c in v.CellsOf(room))
            {
                DomainException.Require(c.X >= 0 && c.Z >= 0 && c.X < 4096 && c.Z < 4096 && cells.Add(c), "invalid or overlapping room footprint");
            }
            var seen = new HashSet<Resource>();
            foreach (var q in room.ContentAmounts())
            {
                DomainException.Require(AdmissionRules.ValidResource(q.Resource) && q.Count >= 0 && seen.Add(q.Resource), "invalid room contents");
            }
        }
    }

    /// <summary>
    /// Whether the room's native temperature is evidence of recovery: the room is enclosed and every cell is
    /// roofed. UsesOutdoorTemperature holds at 25% open roof and Indoors does not mean roofed, so enclosure alone
    /// proves nothing.
    /// </summary>
    public static bool ProvesTemperature(this RoomState r) => r.Enclosed() && r.Roofed();

    /// <summary>
    /// Uses actual sleeping rooms, including beds whose safe reachability disappears during a temperature
    /// emergency. Missing room evidence cannot become a comfortable temperature or a reason to claim recovery.
    /// </summary>
    public static SleepingTemperature TemperatureRange(RoomObservation fact)
    {
        SleepingTemperature none = new SleepingTemperature.NoIndoorBed();
        var v = fact;
        v.Validate();
        var eligible = v.EligibleBeds;
        if (eligible.Count == 0)
        {
            return none;
        }
        var wanted = new HashSet<string>(eligible, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double low = 0.0;
        double high = 0.0;
        var have = false;
        foreach (var room in v.Rooms)
        {
            var selected = false;
            foreach (var id in room.BedIds())
            {
                if (wanted.Contains(id))
                {
                    selected = true;
                    seen.Add(id);
                }
            }
            if (!selected)
            {
                continue;
            }
            var temperature = room.TemperatureC;
            if (!room.ProvesTemperature())
            {
                return none;
            }
            if (!have || temperature < low)
            {
                low = temperature;
            }
            if (!have || temperature > high)
            {
                high = temperature;
            }
            have = true;
        }
        if (seen.Count == wanted.Count && have)
        {
            return new SleepingTemperature.Indoor(low, high);
        }
        return none;
    }

    private sealed class ThermalChoice(RoomState room, IReadOnlyList<Cell> cells, string bed, TemperatureMethod method, double temperature)
    {
        public RoomState Room { get; } = room;

        public IReadOnlyList<Cell> Cells { get; } = cells;

        public string Bed { get; } = bed;

        public TemperatureMethod Method { get; } = method;

        public double Temperature { get; } = temperature;
    }

    /// <summary>
    /// First switches a heat campfire's auto-refuel (CampfireRefuel), then reuses existing thermal facilities
    /// before proposing one ordinary campfire, or one cooler for the hottest sleeping room: a powered Cooler
    /// through a vented wall when cooling reports the research done and a network with spare capacity for its
    /// draw, otherwise a passive cooler inside the room. Native temperature proves recovery.
    /// </summary>
    public static TemperatureProposal SelectTemperatureMethod(RoomObservation v, EquatableArray<string> eligible, TemperatureCooling cooling, RoundsPolicy limits)
    {
        limits.Validate();
        v.Validate();
        var refuel = CampfireRefuel(v, cooling.HeatCampfires, cooling.Sleepers);
        if (refuel is not null)
        {
            return refuel;
        }
        if (eligible.Count == 0)
        {
            return new TemperatureProposal { Method = TemperatureMethod.ShelterNeeded };
        }
        var wanted = new HashSet<string>(eligible, StringComparer.Ordinal);
        var choices = new List<ThermalChoice>();
        var missing = false;
        var unproven = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in v.Rooms)
        {
            var beds = new List<string>();
            foreach (var id in room.BedIds())
            {
                if (wanted.Contains(id))
                {
                    beds.Add(id);
                    seen.Add(id);
                }
            }
            if (beds.Count == 0)
            {
                continue;
            }
            beds.Sort(StringComparer.Ordinal);
            var temperature = room.TemperatureC;
            var roomCells = v.CellsOf(room);
            // Placement is not gated on enclosure or a roof; only a room with no cells has nowhere to place.
            if (roomCells.Count == 0)
            {
                missing = true;
                continue;
            }
            var method = TemperatureMethod.NoMethod;
            // A plain threshold at the comfort band's exit bars (no hysteresis, #3257). Known sleepers' comfort
            // minimum replaces the fixed cold threshold: a parka wearer needs no heat at 10 C.
            var cold = temperature < limits.ColdExit;
            var (low, _, banded) = SleepersBand(room, cooling.Sleepers);
            if (banded)
            {
                cold = temperature < low;
            }
            if (cold)
            {
                method = TemperatureMethod.Heat;
            }
            else if (temperature > limits.HotExit)
            {
                method = TemperatureMethod.Cool;
            }
            if (method != TemperatureMethod.NoMethod)
            {
                choices.Add(new ThermalChoice(room, roomCells, beds[0], method, temperature));
            }
            else if (!room.ProvesTemperature())
            {
                unproven = true;
            }
        }
        // Heat before cool; the coldest room first among the cold, the hottest first among the hot; the lowest
        // bed breaks ties.
        choices.Sort(
            (a, b) =>
            {
                if (a.Method != b.Method)
                {
                    return a.Method == TemperatureMethod.Heat ? -1 : 1;
                }
                if (a.Temperature != b.Temperature)
                {
                    var less = a.Method == TemperatureMethod.Heat ? a.Temperature < b.Temperature : a.Temperature > b.Temperature;
                    return less ? -1 : 1;
                }
                return string.CompareOrdinal(a.Bed, b.Bed);
            }
        );
        var powered = cooling.PoweredCoolerReady();
        var electric = cooling.ElectricHeatReady();
        var cellRoom = new Dictionary<Cell, string>();
        var cells = new Dictionary<Cell, SiteCell>();
        if (powered)
        {
            foreach (var room in v.Rooms)
            {
                foreach (var c in v.CellsOf(room))
                {
                    cellRoom[c] = room.Id;
                }
            }
            foreach (var c in cooling.Cells)
            {
                cells[c.Cell] = c;
            }
        }
        foreach (var choice in choices)
        {
            var method = choice.Method;
            var exists = false;
            foreach (var q in choice.Room.Contents)
            {
                if (q.Units == 0)
                {
                    continue;
                }
                var name = q.DefName;
                if (method == TemperatureMethod.Heat)
                {
                    // With a finite wood stock and power to spare a standing campfire does not hold the room:
                    // the heater joins it.
                    exists = exists || (name == "Campfire" && !electric) || name == "Heater";
                }
                else
                {
                    // A campfire in a hot room is the heat's cause, not a cooling target: its refuel switch
                    // lets it burn out.
                    exists = exists || name == "PassiveCooler" || name == "Cooler" || name == "Campfire";
                }
            }
            // A wall cooler stands in the room's boundary, outside its cell set and its contents: the power
            // census's Cooler rows beside the room are the facility the room already has.
            if (method == TemperatureMethod.Cool && !exists)
            {
                exists = cooling.WallCoolerBeside(choice.Cells);
            }
            exists = exists || (method == TemperatureMethod.Heat && cooling.HeaterIn(choice.Cells));
            if (exists)
            {
                continue;
            }
            if (method == TemperatureMethod.Cool && powered)
            {
                // A vented wall cell hosts the powered cooler; a room with no such wall (rock-bound, or ringed
                // by other rooms) keeps the passive cooler.
                var (cell, rotation, ok) = RefrigerationRules.VentedWall(choice.Room.Id, choice.Cells, cellRoom, cells, new HashSet<Cell>());
                if (ok)
                {
                    return new TemperatureProposal
                    {
                        Method = TemperatureMethod.CoolPowered,
                        Key = ThermalKey(choice.Bed + "/" + Wire.Name(TemperatureMethod.CoolPowered)),
                        Cells = [.. choice.Cells],
                        Cell = cell,
                        Rotation = rotation,
                    };
                }
            }
            if (method == TemperatureMethod.Heat && electric)
            {
                method = TemperatureMethod.HeatElectric;
            }
            return new TemperatureProposal
            {
                Method = method,
                Key = ThermalKey(choice.Bed + "/" + Wire.Name(method)),
                Cells = [.. choice.Cells],
            };
        }
        if (seen.Count != wanted.Count)
        {
            return new TemperatureProposal { Method = TemperatureMethod.BedsUnroomed };
        }
        if (missing)
        {
            return new TemperatureProposal { Method = TemperatureMethod.ShelterNeeded };
        }
        if (choices.Count > 0 || unproven)
        {
            return new TemperatureProposal { Method = TemperatureMethod.Wait };
        }
        return new TemperatureProposal { Method = TemperatureMethod.NoMethod };
    }
}
