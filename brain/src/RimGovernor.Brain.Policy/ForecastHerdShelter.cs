namespace RimGovernor.Brain.Policy;

/// <summary>
/// The Barn credit (RG-HABITAT-001, task 2.3): the exposure deaths a planned barn stops. A penned animal of the barn's herd
/// (HerdShelterRules.UnitAnimals) that the map's seasonal climate kills outside its comfort range (HerdShelterRules.DaysToDeath
/// over the outdoor means) and that survives at the barn's own steady temperature each twelfth (the shared shelter model with its
/// owed work complete, its supplied heat sources only) gains its market value over its days to death, as the pen credit prices a
/// roamer. The barn earns only through a link into a pen of its herd, and only when its ring door is one a
/// fence-blocked roamer can open (HerdShelterRules.AnimalEntry): a barn apart from the pen holds no penned animal and would
/// part it from its grazing. With several barns of a herd the lowest id takes the rows. An unheated barn settles at the outdoor
/// temperature, so it stops no death: a real state that earns nothing.
/// </summary>
internal static class ForecastHerdShelter
{
    /// <summary>The ledger finding a served barn names: the hediff stages short of death and the rest comfort its animals gain are not priced.</summary>
    internal const string FindingNonlethalUnpriced = "barn_nonlethal_exposure_unpriced";

    internal const string FindingRestUnpriced = "barn_rest_comfort_unpriced";

    /// <summary>The barn's credit (<see cref="RoomCredit"/>): one row per penned animal of its herd it saves, under the animal's id.</summary>
    internal static IReadOnlyList<RoomGain> BarnCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        if (!context.LayoutRooms.TryGetValue(room.Room, out var barn) || barn.Role != PlannedRole.Barn)
        {
            return [];
        }
        var herd = barn.Herd;
        if (context.LayoutRooms.Values.Where(r => r.Role == PlannedRole.Barn && r.Herd == herd).Min(r => r.Id) != room.Room || !JoinsPen(room.Room, herd, context))
        {
            return [];
        }
        var input = context.HerdShelter;
        var animals = HerdShelterRules.UnitAnimals(input, herd);
        if (animals.Count == 0 || !HerdShelterRules.AnimalEntry(input))
        {
            return [];
        }
        var climate = HerdShelterRules.Climate(input);
        HerdShelterRules.RequireThermalTables(input.Penalties);
        var id = room.Room.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var shelter = context.Shelters.FirstOrDefault(s => s.PlannedRoom == room.Room || s.Room.Source == ForecastShelterSource.Planned && s.Room.Value == id)
            ?? throw new RequiredFactException("barn shelter:" + room.Room);
        if (shelter.Thermal is not ForecastShelterThermal.Estimated estimated)
        {
            throw new RequiredFactException("barn thermal:" + room.Room);
        }
        // Its service day already owes its shell. A source with no supply heats nothing.
        var closed = shelter with
        {
            Prerequisites = [], AlternativePrerequisites = [],
            Sources = EquatableArray.From(shelter.Sources.Where(s => HeatSupplies.Supplied(s.Supply, out _))),
        };
        var inside = Enumerable.Range(0, 12)
            .Select(t => ForecastShelterPolicy.SteadyTemperature(closed with { Thermal = estimated with { InitialTemperature = climate.Curve[t], Outdoors = climate.Curve[t] } }))
            .ToArray();
        var rows = new List<RoomGain>();
        foreach (var animal in animals)
        {
            var (min, max) = HerdShelterRules.ComfortOf(animal);
            if (HerdShelterRules.DaysToDeath(climate, t => climate.Curve[t], min, max, input.Penalties) is not { } days
                || HerdShelterRules.DaysToDeath(climate, t => inside[t], min, max, input.Penalties) is not null)
            {
                continue;
            }
            rows.Add(new RoomGain(animal.Id.Value, "", HerdShelterRules.LossPerDay(HerdShelterRules.MarketValueOf(animal), days)));
        }
        return rows;
    }

    /// <summary>The mechanics a served barn's credit leaves out (<see cref="RoomFindings"/>), and the state of each resting arrangement of its herd's unit (HerdShelterRules.StateOf).</summary>
    internal static IEnumerable<string> Findings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        string[] unpriced = [FindingNonlethalUnpriced, FindingRestUnpriced];
        if (!context.LayoutRooms.TryGetValue(room.Room, out var barn) || barn.Role != PlannedRole.Barn)
        {
            return unpriced;
        }
        var states = HerdShelterRules.Assess(context.HerdShelter).Where(s => s.Herd == barn.Herd)
            .SelectMany(s => s.Alternatives.Select(a => HerdShelterRules.StateOf(s.Herd, a)));
        return [.. unpriced, .. states];
    }

    /// <summary>Whether a link of the plan joins the barn to a pen of its herd.</summary>
    private static bool JoinsPen(int barn, string herd, ForecastCoupledContext context)
    {
        bool Pen(int room) => context.PenHerds.TryGetValue(room, out var key) && key == herd;
        return context.PlannedLinks.Any(l => (l.A == barn && Pen(l.B)) || (l.B == barn && Pen(l.A)));
    }
}
