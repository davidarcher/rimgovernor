using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

public enum TemperatureMethod
{
    [WireName("")]
    None,

    [WireName("unknown")]
    Unknown,

    [WireName("no_deficit")]
    NoMethod,

    [WireName("enclosed_sleeping_room_needed")]
    ShelterNeeded,

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

/// <summary>A campfire the temperature family built as room heat (its construction claim carries EnsureTemperatureSafety): its native room and auto-refuel toggle.</summary>
public sealed record HeatCampfire
{
    public string Id { get; init; } = "";

    public Fact<string> Room { get; init; }

    public Fact<bool> AutoRefuel { get; init; }
}

public sealed record TemperatureProposal
{
    public TemperatureMethod Method { get; init; }

    public MethodId Key { get; init; }

    public string Room { get; init; } = "";

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
/// availability and draw, the colony power topology and the site cells the wall search walks. The default value
/// (everything unknown) confines the method to passive coolers.
/// </summary>
public sealed record TemperatureCooling
{
    public Fact<bool> CoolerAvailable { get; init; }

    public Fact<double> CoolerDrawW { get; init; }

    public Fact<PowerTopology> Power { get; init; }

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    /// <summary>The campfires standing as room heat, whose auto-refuel the method switches.</summary>
    public IReadOnlyList<HeatCampfire> HeatCampfires { get; init; } = [];

    /// <summary>The colonists and slaves whose owned beds and comfortable ranges band each sleeping room.</summary>
    public IReadOnlyList<SleepingPerson> Sleepers { get; init; } = [];

    /// <summary>The catalog's heater definition (RoomFurniture.Heater), the climate piece of the barn; empty when the catalog has none.</summary>
    public string Heater { get; init; } = "";

    /// <summary>The Heater planning definition's availability.</summary>
    public Fact<bool> HeaterAvailable { get; init; }

    /// <summary>The Heater planning definition's nominal draw.</summary>
    public Fact<double> HeaterDrawW { get; init; }

    /// <summary>True when the campfire's fuel (wood) is a finite stock here: nothing sows it. A renewable fuel keeps campfire heat.</summary>
    public bool HeatFuelFinite { get; init; }

    /// <summary>
    /// The layout plan's shelter rooms, read from the planned interior alone: a shelter template holds the
    /// sleeping places (spots, bedrolls, beds), so it is the room people will sleep in before any bed stands.
    /// </summary>
    public IReadOnlyList<ThermalShelter> PlannedShelters { get; init; } = [];

    /// <summary>
    /// Whether room holds a working climate piece: a powered heater of the catalog's heater definition standing
    /// on its floor, or a wall cooler beside it. The barn's heater is the first; a room that holds one needs no
    /// second.
    /// </summary>
    public bool Conditioned(Room room) => WallCoolerBeside(room) || HeaterIn(room);

    /// <summary>Whether a powered heater of the catalog's heater definition stands in room.</summary>
    internal bool HeaterIn(Room room)
    {
        if (!Power.TryGetValue(out var topology) || Heater.Length == 0)
        {
            return false;
        }
        var inside = new HashSet<Cell>(room.Cells);
        foreach (var b in topology.Buildings)
        {
            if (b.Definition != Heater)
            {
                continue;
            }
            if (b.Powered.TryGetValue(out var powered) && powered && inside.Contains(b.Cell))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A Cooler in the power census standing on a cell four-adjacent to one of the room's cells: a cooler placed through the room's wall.</summary>
    internal bool WallCoolerBeside(Room room)
    {
        if (!Power.TryGetValue(out var topology))
        {
            return false;
        }
        var inside = new HashSet<Cell>(room.Cells);
        foreach (var b in topology.Buildings)
        {
            if (b.Definition != "Cooler")
            {
                continue;
            }
            foreach (var rotation in new[] { Rotation.North, Rotation.East, Rotation.South, Rotation.West })
            {
                if (inside.Contains(PolicyFunctions.Step(b.Cell, PolicyFunctions.Facing(rotation), 1)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Whether a powered cooler can be proposed at all: research finished, draw known and a network with that much spare.</summary>
    internal bool PoweredCoolerReady() => SpareCovers(CoolerAvailable, CoolerDrawW);

    /// <summary>Whether a powered heater is preferred over a campfire: the campfire's fuel is finite, the heater is placeable and a network has the spare capacity for its draw.</summary>
    internal bool ElectricHeatReady() => HeatFuelFinite && Heater.Length != 0 && SpareCovers(HeaterAvailable, HeaterDrawW);

    private bool SpareCovers(Fact<bool> availableFact, Fact<double> drawFact)
    {
        var ak = availableFact.TryGetValue(out var available);
        var dk = drawFact.TryGetValue(out var draw);
        var tk = Power.TryGetValue(out var topology);
        if (!ak || !available || !dk || !tk)
        {
            return false;
        }
        if (topology!.Blackout.TryGetValue(out var blackout) && blackout)
        {
            return false;
        }
        return topology.SpareW().TryGetValue(out var spare) && spare >= draw;
    }
}

/// <summary>One planned shelter room as the temperature method sees it.</summary>
public sealed record ThermalShelter
{
    /// <summary>The planned interior.</summary>
    public IReadOnlyList<Cell> Cells { get; init; } = [];

    /// <summary>The definitions of the player buildings already on it.</summary>
    public IReadOnlyList<string> Standing { get; init; } = [];

    /// <summary>The plan's latched hot climate (the template's cooler slot).</summary>
    public bool Hot { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The intersection of the comfortable ranges of the people owning a bed in room: apparel, traits and genes
    /// shift each one. Without a sleeper whose range is known banded is false and the band is unknown: nothing is
    /// judged against a range the game did not state.
    /// </summary>
    private static (double Low, double High, bool Banded) SleepersBand(Room room, IReadOnlyList<SleepingPerson> sleepers)
    {
        var beds = new HashSet<string>(room.Beds, StringComparer.Ordinal);
        var low = double.NegativeInfinity;
        var high = double.PositiveInfinity;
        var banded = false;
        foreach (var p in sleepers)
        {
            var bk = p.OwnedBed.TryGetValue(out var bed);
            var mk = p.ComfortableMin.TryGetValue(out var min);
            var xk = p.ComfortableMax.TryGetValue(out var max);
            if (!bk || !mk || !xk || !beds.Contains(bed!))
            {
                continue;
            }
            low = Math.Max(low, min);
            high = Math.Min(high, max);
            banded = true;
        }
        return banded ? (low, high, true) : (0, 0, false);
    }

    /// <summary>
    /// Holds EnsureTemperatureSafety open past the fixed thresholds: a heat campfire owes its refuel switch, or a
    /// sleeping room with no campfire or heater sits below its sleepers' comfort minimum.
    /// </summary>
    public static bool TemperatureOwed(Fact<RoomObservation> fact, TemperatureCooling cooling)
    {
        if (CampfireRefuel(fact, cooling).Owed)
        {
            return true;
        }
        if (!fact.TryGetValue(out var v))
        {
            return false;
        }
        foreach (var room in v.Rooms)
        {
            var tk = room.Temperature.TryGetValue(out var temperature);
            var ck = room.Contents.TryGetValue(out var contents);
            var (low, _, banded) = SleepersBand(room, cooling.Sleepers);
            if (!tk || !ck || !banded || temperature >= low)
            {
                continue;
            }
            var heated = false;
            foreach (var q in contents)
            {
                heated = heated || (q.Count > 0 && (q.Resource.Value == "Campfire" || q.Resource.Value == "Heater"));
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
    /// warmer while it refuels, on below their comfort minimum while it does not (SleepersBand). A campfire whose
    /// room, temperature or toggle is unknown owes nothing.
    /// </summary>
    public static (TemperatureProposal Proposal, bool Owed) CampfireRefuel(Fact<RoomObservation> fact, TemperatureCooling cooling)
    {
        if (!fact.TryGetValue(out var v))
        {
            return (new TemperatureProposal(), false);
        }
        var sorted = cooling.HeatCampfires.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        foreach (var campfire in sorted)
        {
            var rk = campfire.Room.TryGetValue(out var id);
            var ok = campfire.AutoRefuel.TryGetValue(out var on);
            var (room, found) = v.Room(id ?? "");
            if (!rk || !ok || !found || room.Beds.Count == 0)
            {
                continue;
            }
            if (!room.Temperature.TryGetValue(out var temperature))
            {
                continue;
            }
            var (low, high, banded) = SleepersBand(room, cooling.Sleepers);
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
            return (
                new TemperatureProposal
                {
                    Method = method,
                    Key = ThermalKey(campfire.Id + "/" + Wire.Name(method)),
                    Room = room.Id,
                    Thing = campfire.Id,
                },
                true
            );
        }
        return (new TemperatureProposal(), false);
    }

    /// <summary>
    /// The largest surplus of nominal producer capacity over consumer demand across the topology's connected
    /// power networks, the same capacity-versus-demand reading the power family sizes its budget by. Unknown when
    /// any connected building's draw or network is unknown, or when no network has a producer.
    /// </summary>
    public static Fact<double> SpareW(this PowerTopology t)
    {
        var nets = new Dictionary<string, (double Capacity, double Demand)>(StringComparer.Ordinal);
        foreach (var b in t.Buildings)
        {
            if (!b.Connected.TryGetValue(out var connected))
            {
                return Unknown<double>();
            }
            if (!connected)
            {
                continue;
            }
            var wk = b.BaseW.TryGetValue(out var w);
            var nk = b.Network.TryGetValue(out var id);
            if (!wk || !nk)
            {
                return Unknown<double>();
            }
            nets.TryGetValue(id!, out var n);
            if (w > 0)
            {
                n.Capacity += w;
            }
            else
            {
                n.Demand -= w;
            }
            nets[id!] = n;
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
        return have ? Known(spare) : Unknown<double>();
    }

    public static void Validate(this RoomObservation v)
    {
        if (v.EligibleBeds.TryGetValue(out var eligible))
        {
            var seenBed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in eligible)
            {
                DomainException.Require(FoodId(id) && seenBed.Add(id), "invalid sleeping bed census");
            }
        }
        var rooms = new HashSet<string>(StringComparer.Ordinal);
        var beds = new HashSet<string>(StringComparer.Ordinal);
        var cells = new HashSet<Cell>();
        foreach (var room in v.Rooms)
        {
            DomainException.Require(FoodId(room.Id) && rooms.Add(room.Id), "invalid temperature room");
            if (room.Temperature.TryGetValue(out var temperature))
            {
                DomainException.Require(!double.IsNaN(temperature) && !double.IsInfinity(temperature), "invalid room temperature");
            }
            foreach (var id in room.Beds)
            {
                DomainException.Require(FoodId(id) && beds.Add(id), "ambiguous sleeping room");
            }
            foreach (var c in room.Cells)
            {
                DomainException.Require(c.X >= 0 && c.Z >= 0 && c.X < 4096 && c.Z < 4096 && cells.Add(c), "invalid or overlapping room footprint");
            }
            if (room.Contents.TryGetValue(out var contents))
            {
                var seen = new HashSet<Resource>();
                foreach (var q in contents)
                {
                    DomainException.Require(ValidResource(q.Resource) && q.Count >= 0 && seen.Add(q.Resource), "invalid room contents");
                }
            }
        }
    }

    /// <summary>
    /// Whether the room's native temperature is evidence of recovery: the room is enclosed and every cell is
    /// roofed. UsesOutdoorTemperature holds at 25% open roof and Indoors does not mean roofed, so enclosure alone
    /// proves nothing.
    /// </summary>
    public static bool ProvesTemperature(this Room r) =>
        r.Enclosed.TryGetValue(out var enclosed) && enclosed && r.Roofed.TryGetValue(out var roofed) && roofed;

    /// <summary>
    /// Uses actual sleeping rooms, including beds whose safe reachability disappears during a temperature
    /// emergency. Missing room evidence cannot become a comfortable temperature or a reason to claim recovery.
    /// </summary>
    public static (Fact<double> Minimum, Fact<double> Maximum) TemperatureRange(Fact<RoomObservation> fact)
    {
        var none = (default(Fact<double>), default(Fact<double>));
        if (!fact.TryGetValue(out var v))
        {
            return none;
        }
        try
        {
            v.Validate();
        }
        catch (DomainException)
        {
            return none;
        }
        if (!v.EligibleBeds.TryGetValue(out var eligible) || eligible.Count == 0)
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
            foreach (var id in room.Beds)
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
            if (!room.Temperature.TryGetValue(out var temperature) || !room.ProvesTemperature())
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
            return (Known(low), Known(high));
        }
        return none;
    }

    private sealed class ThermalChoice(Room room, string bed, TemperatureMethod method, double temperature)
    {
        public Room Room { get; } = room;

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
    public static TemperatureProposal SelectTemperatureMethod(Fact<RoomObservation> fact, TemperatureCooling cooling, RoundsPolicy limits, RoundsLatches latches)
    {
        limits.Validate();
        if (!fact.TryGetValue(out var v))
        {
            return new TemperatureProposal { Method = TemperatureMethod.Unknown };
        }
        v.Validate();
        var (refuel, owed) = CampfireRefuel(fact, cooling);
        if (owed)
        {
            return refuel;
        }
        if (!v.EligibleBeds.TryGetValue(out var eligible))
        {
            return new TemperatureProposal { Method = TemperatureMethod.Unknown };
        }
        if (eligible.Count == 0)
        {
            return PlannedShelterCooler(cooling);
        }
        var wanted = new HashSet<string>(eligible, StringComparer.Ordinal);
        var choices = new List<ThermalChoice>();
        var unknown = false;
        var missing = false;
        var unproven = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in v.Rooms)
        {
            var beds = new List<string>();
            foreach (var id in room.Beds)
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
            if (!room.Temperature.TryGetValue(out var temperature))
            {
                unknown = true;
                continue;
            }
            // Placement is not gated on enclosure or a roof; only a room with no cells has nowhere to place.
            if (room.Cells.Count == 0)
            {
                missing = true;
                continue;
            }
            var method = TemperatureMethod.NoMethod;
            // Known sleepers' comfort minimum replaces the fixed cold thresholds: a parka wearer needs no heat
            // at 10 C.
            var cold = temperature < limits.ColdEnter || (latches.Cold && temperature < limits.ColdExit);
            var (low, _, banded) = SleepersBand(room, cooling.Sleepers);
            if (banded)
            {
                cold = temperature < low;
            }
            if (cold)
            {
                method = TemperatureMethod.Heat;
            }
            else if (temperature > limits.HotEnter || (latches.Hot && temperature > limits.HotExit))
            {
                method = TemperatureMethod.Cool;
            }
            if (method != TemperatureMethod.NoMethod)
            {
                choices.Add(new ThermalChoice(room, beds[0], method, temperature));
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
                foreach (var c in room.Cells)
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
            if (!choice.Room.Contents.TryGetValue(out var contents))
            {
                unknown = true;
                continue;
            }
            var method = choice.Method;
            var exists = false;
            foreach (var q in contents)
            {
                if (q.Count == 0)
                {
                    continue;
                }
                var name = q.Resource.Value;
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
                exists = cooling.WallCoolerBeside(choice.Room);
            }
            exists = exists || (method == TemperatureMethod.Heat && cooling.HeaterIn(choice.Room));
            if (exists)
            {
                continue;
            }
            if (method == TemperatureMethod.Cool && powered)
            {
                // A vented wall cell hosts the powered cooler; a room with no such wall (rock-bound, or ringed
                // by other rooms) keeps the passive cooler.
                var (cell, rotation, ok) = VentedWall(choice.Room, cellRoom, cells, new HashSet<Cell>());
                if (ok)
                {
                    return new TemperatureProposal
                    {
                        Method = TemperatureMethod.CoolPowered,
                        Key = ThermalKey(choice.Bed + "/" + Wire.Name(TemperatureMethod.CoolPowered)),
                        Room = choice.Room.Id,
                        Cells = [.. choice.Room.Cells],
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
                Room = choice.Room.Id,
                Cells = [.. choice.Room.Cells],
            };
        }
        if (seen.Count != wanted.Count || unknown)
        {
            return new TemperatureProposal { Method = TemperatureMethod.Unknown };
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

    /// <summary>
    /// Keys the hot map's cooler on the planned shelter instead of a standing bed room: a tribal start sleeps on
    /// spots and bedrolls first, so waiting for a bed would serialize the cooler behind the housing. The shelter
    /// template always holds sleeping places, which is why the rule reads the shelter role and no other planned
    /// room. Once the cooler stands the method waits; the proof of temperature stays with the roofed, enclosed
    /// room (ProvesTemperature) once beds make it a census room.
    /// </summary>
    private static TemperatureProposal PlannedShelterCooler(TemperatureCooling cooling)
    {
        foreach (var shelter in cooling.PlannedShelters)
        {
            if (!shelter.Hot || shelter.Cells.Count == 0)
            {
                continue;
            }
            var cells = shelter.Cells.OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
            foreach (var d in shelter.Standing)
            {
                if (d == "PassiveCooler" || d == "Cooler")
                {
                    return new TemperatureProposal { Method = TemperatureMethod.Wait };
                }
            }
            return new TemperatureProposal
            {
                Method = TemperatureMethod.Cool,
                Key = ThermalKey(FormattableString.Invariant($"planned-shelter/{cells[0].X}/{cells[0].Z}/{Wire.Name(TemperatureMethod.Cool)}")),
                Cells = cells,
            };
        }
        return new TemperatureProposal { Method = TemperatureMethod.ShelterNeeded };
    }
}
