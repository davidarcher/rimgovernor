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

    [WireName("temperature_target")]
    SetTarget,
}

/// <summary>A campfire the temperature family built as room heat (its construction claim carries the shelter/temperature filing key): its native room and auto-refuel toggle.</summary>
public sealed record HeatCampfire
{
    public required string Id { get; init; }

    public required string Room { get; init; }

    public required bool AutoRefuel { get; init; }

    /// <summary>The campfire with its native room and toggle; no room heat source outdoors or before the toggle is read.</summary>
    public static HeatCampfire? Of(string id, string? room, RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl autoRefuel) =>
        room is { } r && autoRefuel is RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.Switch { Allowed: var on } ? new HeatCampfire { Id = id, Room = r, AutoRefuel = on } : null;
}

public sealed record TemperatureProposal
{
    public required TemperatureMethod Method { get; init; }

    public string Definition { get; init; } = "";

    public IReadOnlyList<TemperatureDefinition> Options { get; init; } = [];

    public double TargetC { get; init; }

    public MethodId Key { get; init; }

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    /// <summary>Every observed vented wall in deterministic order, with its cold side facing the room.</summary>
    public IReadOnlyList<(Cell Cell, Rotation Rotation)> WallSites { get; init; } = [];

    /// <summary>The heat campfire a refuel switch targets.</summary>
    public string Thing { get; init; } = "";
}

/// <summary>The measured candidates, services and eligibility used by temperature policy.</summary>
public sealed record TemperatureCooling
{
    public required IReadOnlyList<TemperatureDefinition> Candidates { get; init; }
    public required IReadOnlyList<PlanningDefinition> Definitions { get; init; }
    public required IReadOnlyList<CurrentBuilding> Buildings { get; init; }
    public required IReadOnlyList<WorkPawn> Builders { get; init; }
    public required IReadOnlyDictionary<Resource, long> Stock { get; init; }
    public required IReadOnlySet<Resource> RenewableFuels { get; init; }
    public required ClimatePower Power { get; init; }
    public required IReadOnlyList<SiteCell> Cells { get; init; }
    public required IReadOnlyList<HeatCampfire> HeatCampfires { get; init; }
    public required IReadOnlyList<SleepingPerson> Sleepers { get; init; }

    public IReadOnlyList<TemperatureDefinition> Options(bool heat, double target)
    {
        var definitions = Definitions.ToDictionary(row => row.Name, StringComparer.Ordinal);
        var options = new List<TemperatureDefinition>();
        foreach (var candidate in Candidates)
        {
            candidate.Validate();
            if (!definitions.TryGetValue(candidate.Name, out var definition))
                throw new RequiredFactException("thermal definition:" + candidate.Name);
            if (candidate.Heats != heat || candidate.MinimumC > target || candidate.MaximumC < target || !definition.Available
                || !FacilityPlanningRules.BuilderAvailable(Builders, definition.ConstructionSkill)) continue;
            if (candidate.PowerW > 0 && (Power is not ClimatePower.Census { Topology: var topology }
                || topology.Blackout || topology.SpareW() < candidate.PowerW)) continue;
            if (candidate.Fuels.Count > 0 && !candidate.Fuels.Any(fuel => Stock.GetValueOrDefault(fuel) > 0)) continue;
            if (definition.FixedConstruction is not { } construction)
                throw new RequiredFactException("thermal construction:" + candidate.Name);
            if (construction.Costs.Any(cost => Stock.GetValueOrDefault(cost.Resource) < cost.Count)) continue;
            options.Add(candidate);
        }
        return [.. options.OrderBy(candidate => heat && candidate.Fuels.Any(RenewableFuels.Contains) ? 0 : candidate.Controlled ? 1 : 2)
            .ThenBy(candidate => candidate.BuildValue / Math.Abs(candidate.HeatPerSecond))
            .ThenBy(candidate => candidate.PowerW / Math.Abs(candidate.HeatPerSecond))
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)];
    }

    public IEnumerable<(CurrentBuilding Building, TemperatureDefinition Definition)> Existing(IReadOnlyList<Cell> cells, bool heat)
    {
        var inside = cells.ToHashSet();
        var definitions = Candidates.Where(candidate => candidate.Heats == heat).ToDictionary(candidate => candidate.Name, StringComparer.Ordinal);
        foreach (var building in Buildings.OrderBy(building => building.Id, StringComparer.Ordinal))
        {
            if (!definitions.TryGetValue(building.Building.Definition, out var definition)) continue;
            var cell = definition.Wall ? RefrigerationRules.ColdSide(building.Building.Cell, building.Building.Rotation) : building.Building.Cell;
            if (inside.Contains(cell)) yield return (building, definition);
        }
    }
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
            if (p.OwnedBed is null || !beds.Contains(p.OwnedBed))
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
    /// Holds shelter/temperature unmet when a claimed fuel heater owes its switch or a sleeping room
    /// remains outside its sleepers' comfortable range. A standing facility does not prove recovery.
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
            var (low, high, banded) = SleepersBand(room, sleepers);
            if (!banded || (temperature >= low && temperature <= high))
            {
                continue;
            }
            return true;
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
    public static TemperatureProposal SelectTemperatureMethod(RoomObservation v, EquatableArray<string> eligible, TemperatureCooling cooling, PlannerSettings limits)
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
            var (low, high, banded) = SleepersBand(room, cooling.Sleepers);
            if (banded)
            {
                cold = temperature < low;
            }
            if (cold)
            {
                method = TemperatureMethod.Heat;
            }
            else if (temperature > (banded ? high : limits.HotExit))
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
        var cellRoom = new Dictionary<Cell, string>();
        foreach (var room in v.Rooms)
        foreach (var cell in v.CellsOf(room)) cellRoom[cell] = room.Id;
        var cells = cooling.Cells.ToDictionary(site => site.Cell);
        foreach (var choice in choices)
        {
            var heat = choice.Method == TemperatureMethod.Heat;
            var (low, high, banded) = SleepersBand(choice.Room, cooling.Sleepers);
            if (!banded) { low = limits.ColdExit; high = limits.HotExit; }
            var target = heat ? low : high;
            var options = cooling.Options(heat, target);
            var existing = cooling.Existing(choice.Cells, heat).ToList();
            foreach (var (building, definition) in existing)
            {
                if (!definition.Controlled) continue;
                if (building.Observed.Settings.Control is not RimGovernor.Brain.GameFacts.Observations.BuildingControl.Temperature control
                    || !double.IsFinite(control.TargetC)) throw new RequiredFactException("thermal control:" + building.Id);
                var desired = Math.Clamp(control.TargetC, low, high);
                if (desired != control.TargetC)
                    return new TemperatureProposal
                    {
                        Method = TemperatureMethod.SetTarget, Definition = definition.Name, Thing = building.Id, TargetC = desired,
                        Key = ThermalKey(building.Id + "/target/" + desired.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                    };
            }
            // Keep a working facility; finite fuel heat may be joined by a powered thermostat.
            if (existing.Any(pair => pair.Definition.Controlled || !heat || options.Count == 0 || !options[0].Controlled)) continue;
            // A claimed heat source in a hot room must finish burning out before adding cooling.
            if (!heat && cooling.HeatCampfires.Any(source => source.Room == choice.Room.Id)) continue;
            var walls = new List<(Cell Cell, Rotation Rotation)>();
            if (options.Any(option => option.Wall))
            {
                var taken = new HashSet<Cell>();
                while (RefrigerationRules.VentedWall(choice.Room.Id, choice.Cells, cellRoom, cells, taken) is { Ok: true } site)
                {
                    walls.Add((site.Cell, site.Rotation));
                    taken.Add(site.Cell);
                }
            }
            options = [.. options.Where(option => !option.Wall || walls.Count > 0)];
            if (options.Count == 0) continue;
            var selected = options[0];
            var method = heat ? selected.Controlled ? TemperatureMethod.HeatElectric : TemperatureMethod.Heat
                : selected.Controlled ? TemperatureMethod.CoolPowered : TemperatureMethod.Cool;
            return new TemperatureProposal
            {
                Method = method, Definition = selected.Name, Options = options,
                Key = ThermalKey(choice.Bed + "/" + Wire.Name(method)), Cells = [.. choice.Cells], WallSites = walls,
            };
        }        if (seen.Count != wanted.Count)
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
