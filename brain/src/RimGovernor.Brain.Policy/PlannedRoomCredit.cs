namespace RimGovernor.Brain.Policy;

/// <summary>
/// A planned room's service day in one schedule (#3796, epic #3650 P2): the tick its one reconcile owes nothing, in the
/// ring stuff it is built in ("" for a room without a closure prediction). Inside the horizon the schedule's own, the
/// latest finish of its owed work; past it the projection (<see cref="PlannedRoomCredits.ServiceDays"/>).
/// </summary>
public sealed record ForecastRoomService(int Room, string Stuff, double ServedAt);

/// <summary>
/// A placed site or standing removal a planned room owes (#3796, epic #3650 P4): its thing or designation id, the planned
/// rooms whose interior or ring cells hold it (a shared wall cell is both rooms') and the activities that finish it (any one
/// does). Only the generated ones are read; one a builder cannot work yet is owed nothing.
/// </summary>
public sealed record ForecastRoomItem(string Item, EquatableArray<int> Rooms, EquatableArray<string> Finishes);

/// <summary>
/// One row of a role's credit (#3796): its subject (a bedroom's occupant), the pawn it is booked to ("" none), the capability-days a
/// served day gains, and the capability-days the room gains once when it serves (#3804: a queued surgery it hosts).
/// </summary>
public readonly record struct RoomGain(string Subject, string Pawn, double PerDay)
{
    public double Once { get; init; }
}

/// <summary>
/// A planned room role's credit (#3796): the gains a day of <paramref name="room"/> serving earns in
/// <paramref name="outcome"/> of the portfolio <paramref name="commitments"/> (the roots it chose, #3805: the work it leaves
/// unscheduled is the outcome's own), over the tier-1 <paramref name="horizonTicks"/>. Empty earns nothing: each credit names the
/// real states that earn nothing. A fact it needs unread throws <see cref="RequiredFactException"/> (epic #3650 P10 as
/// revised, #3835): there is no unassessed credit.
/// </summary>
public delegate IReadOnlyList<RoomGain> RoomCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizonTicks);

/// <summary>The named findings a served room's credit adds to the ledger (#3811): what it leaves out, or a part it could not price.</summary>
public delegate IEnumerable<string> RoomFindings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizonTicks);

/// <summary>
/// The per-role planned-room credit seam (#3796, epic #3650): a role is priced when it has a credit
/// (<see cref="ForecastCoupledContext.RoomCredits"/>, <see cref="Shipped"/> by default). No planned room's units count as
/// development (#3828), so a room is selected only on the ledger. A priced room's floors and removals leave the development
/// count, its planned deliveries are charged their material, its placed ring sites are its own roots and a
/// standing one is offered wall swaps. Each served room is credited perDay × <see cref="RingClosure.CreditDays"/> of its
/// service day, in the Investment term under <see cref="Key"/>.
/// </summary>
public static class PlannedRoomCredits
{
    /// <summary>The shipped credits: Bedroom by its occupant's mood gain (#3655), Barracks by each sleeper's (#3658), Lab by
    /// the research speed its room gives the bench (#3800), Workshop by useful bill output its stations gain (#3801), Throne and Suite by the title room thoughts their colonist loses once
    /// the room meets its title's requirement (#3807), Kitchen and Butchery by the food poisoning they prevent (#3802); Storage,
    /// Meal closet, Reserve and Yard by the stock they keep from deteriorating (#3798); Morgue and Tomb by the corpse moods they
    /// remove (#3806); Worship by the ideoligion effects it meets for each believer (#3808); Pen by the roamers it keeps from walking off the map (#3812); DeathrestChamber by the work days and
    /// chamber mood its caskets and accelerators give each deathrester (#3810); Hospital by the infection, illness, sleep mood
    /// and queued surgery failures its bed relieves (#3804); ContainmentCell by the escapes its strength stops and the study it adds
    /// for the entity it holds, or the study less the escapes of the entity it captures (#3811); SparringRing by the raid harm its
    /// trainees' Melee saves (#3813); TrainingRange by the raid harm its trainees' Shooting saves (#3846); IsolationRoom by the first agony pulse a held creepjoiner keeps from the other colonists (#3844); Prison by the days sooner each prisoner it beds is recruited, at its value as a colonist (#3805); Classroom by the lesson XP each child it seats is taught, less the teacher's lesson ticks (#3809); Dining and Rec by the impressive-room memory each colonist's meals or joy activities gain in them, Dining also by the AteWithoutTable its tables lift (#3803); Nursery by the cries and giggles of its babies it keeps from the colonists who hear them now (#3854).</summary>
    public static readonly IReadOnlyDictionary<PlannedRole, RoomCredit> Shipped = new Dictionary<PlannedRole, RoomCredit>
    {
        [PlannedRole.Bedroom] = ForecastHousing.BedroomCredit,
        [PlannedRole.Barracks] = ForecastHousing.BedroomCredit,
        [PlannedRole.Lab] = ForecastLaboratory.LaboratoryCredit,
        [PlannedRole.Workshop] = ForecastWorkshop.Credit,
        [PlannedRole.Throne] = ForecastHousing.RoyalRoomCredit,
        [PlannedRole.Suite] = ForecastHousing.RoyalRoomCredit,
        [PlannedRole.Kitchen] = KitchenCredits.KitchenCredit,
        [PlannedRole.Butchery] = KitchenCredits.ButcheryCredit,
        [PlannedRole.Storage] = ForecastStorage.StorageCredit,
        [PlannedRole.MealCloset] = ForecastStorage.StorageCredit,
        [PlannedRole.Reserve] = ForecastStorage.StorageCredit,
        [PlannedRole.Yard] = ForecastStorage.StorageCredit,
        [PlannedRole.Morgue] = ForecastCorpses.MorgueCredit,
        [PlannedRole.Tomb] = ForecastCorpses.TombCredit,
        [PlannedRole.Worship] = ForecastWorship.WorshipCredit,
        [PlannedRole.Pen] = ForecastPens.PenCredit,
        [PlannedRole.DeathrestChamber] = ForecastDeathrest.ChamberCredit,
        [PlannedRole.Hospital] = ForecastHospital.HospitalCredit,
        [PlannedRole.ContainmentCell] = ForecastContainment.CellCredit,
        [PlannedRole.SparringRing] = ForecastSparring.RingCredit,
        [PlannedRole.TrainingRange] = ForecastTrainingRange.RangeCredit,
        [PlannedRole.IsolationRoom] = ForecastIsolation.IsolationCredit,
        [PlannedRole.Prison] = ForecastPrison.PrisonCredit,
        [PlannedRole.Classroom] = ForecastClassroom.ClassroomCredit,
        [PlannedRole.Dining] = ForecastRoomThoughts.DiningCredit,
        [PlannedRole.Rec] = ForecastRoomThoughts.RecCredit,
        [PlannedRole.Nursery] = ForecastNursery.NurseryCredit,
        [PlannedRole.Freezer] = ForecastFreezer.Credit,
    };

    /// <summary>
    /// The mechanics a role's credit leaves out, named with each room of it served (<see cref="RoomFindings"/>): the hospital's
    /// (#3804); the containment cell's, with its held entity's unpriced knowledge category (#3811); the worship room's demand
    /// requirements and ritual outcomes it cannot predict (#3835); the sparring ring's (#3813); the training range's (#3846);
    /// the isolation room's, or the named state it earns nothing in (#3844); the prison's, the classroom's, the dining and rec rooms' and
    /// the nursery's, with the named states they earn nothing in (#3805, #3809, #3803, #3854).
    /// </summary>
    public static readonly IReadOnlyDictionary<PlannedRole, RoomFindings> Unmodelled = new Dictionary<PlannedRole, RoomFindings>
    {
        [PlannedRole.Hospital] = (_, _, _, _, _) => ForecastHospital.Unmodelled,
        [PlannedRole.Worship] = ForecastWorship.Unmodelled,
        [PlannedRole.ContainmentCell] = ForecastContainment.CellFindings,
        [PlannedRole.SparringRing] = (_, _, _, _, _) => ForecastSparring.Unmodelled,
        [PlannedRole.TrainingRange] = (_, _, _, _, _) => ForecastTrainingRange.Unmodelled,
        [PlannedRole.IsolationRoom] = ForecastIsolation.Findings,
        [PlannedRole.Prison] = ForecastPrison.Findings,
        [PlannedRole.Classroom] = ForecastClassroom.Findings,
        [PlannedRole.Dining] = ForecastRoomThoughts.DiningFindings,
        [PlannedRole.Rec] = ForecastRoomThoughts.RecFindings,
        [PlannedRole.Nursery] = ForecastNursery.Findings,
    };

    /// <summary>
    /// Per pawn, the distinct shifts (mood delta / 100) its mood timeline integrates breaks at, so each mood credit's
    /// <see cref="ForecastHousing.GainPerDay"/> finds its shifted break ticks: the bedroom moves' (#3655), the corpse
    /// credits' (#3806), the worship credit's (#3808), the deathrest chamber's (#3810) and the hospital's (#3804). Constant per context, so the mood cache key holds.
    /// Nursery heard-fit and combined dining/recreation shifts depend on the outcome and integrate the retained target spans directly.
    /// </summary>
    internal static IReadOnlyDictionary<string, double[]> BreakShifts(ForecastCoupledContext context) => Shifts.GetValue(context, c =>
        ForecastHousing.BreakShifts(c).Concat(ForecastCorpses.BreakShifts(c)).Concat(ForecastWorship.Shifts(c)).Concat(ForecastDeathrest.Shifts(c)).Concat(ForecastHospital.Shifts(c)).GroupBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(p => p.Value).Distinct().Order().ToArray(), StringComparer.Ordinal));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, IReadOnlyDictionary<string, double[]>> Shifts = new();

    /// <summary>
    /// Whether <paramref name="room"/> is priced: its role has a credit, or it is a connector (<see cref="ConnectorsOf"/>) of a
    /// priced room (#3815, epic #3650 P4). A connector has no credit of its own; its work is the rooms' it serves.
    /// </summary>
    public static bool Priced(this ForecastCoupledContext context, int room) =>
        context.PlannedRoomRoles.TryGetValue(room, out var role) && context.RoomCredits.ContainsKey(role)
        || context.Connectors.TryGetValue(room, out var served) && served.Any(context.Priced);

    /// <summary>
    /// The connectors among <paramref name="rooms"/> (#3815, epic #3650 P4), each a prerequisite of the rooms it serves: an
    /// attached airlock seals its parent; a circulation corridor reaches each room a door of <paramref name="links"/> joins
    /// it to, other connectors aside. A corridor serving several rooms counts for each, as a shared wall does.
    /// </summary>
    public static IReadOnlyDictionary<int, EquatableArray<int>> ConnectorsOf(IReadOnlyList<PlannedRoom> rooms, IReadOnlyList<RoomLink> links)
    {
        var connector = rooms.Where(r => r.Attached() || r.Role == PlannedRole.Circulation).Select(r => r.Id).ToHashSet();
        var connectors = new Dictionary<int, EquatableArray<int>>();
        foreach (var room in rooms.Where(r => r.Id > 0 && connector.Contains(r.Id)).DistinctBy(r => r.Id))
        {
            int[] served = room.Attached() ? room.Parent > 0 ? [room.Parent] : []
                : [.. links.Where(l => l.A == room.Id || l.B == room.Id).Select(l => l.A == room.Id ? l.B : l.A).Where(o => !connector.Contains(o)).Distinct().Order()];
            if (served.Length != 0) connectors[room.Id] = EquatableArray.From(served);
        }
        return connectors;
    }

    /// <summary>The Investment key of one credit row: <c>{role}/{room}/{subject}</c>, the role by its wire name.</summary>
    public static string Key(PlannedRole role, int room, string subject) => FormattableString.Invariant($"{Wire.Name(role)}/{room}/{subject}");

    /// <summary>
    /// The closure prediction a credit reads for planned room <paramref name="room"/> served in <paramref name="stuff"/>
    /// (#3835): the one in that stuff, else its first by stuff (a room without ring work is predicted once, in stuff "", while
    /// its service day names its sites' stuff). Every priced room is predicted (PlannedRules.PlannedRoomPredictionRequestOf)
    /// and native reads each row whole (#3834); null for a shape whose ring closes no room, which native answers no row for,
    /// so the room changes nothing a closed room would.
    /// </summary>
    internal static ForecastPlannedClosure? Prediction(ForecastCoupledContext context, int room, string stuff)
    {
        var closures = context.Closures.Where(c => c.Room == room).OrderBy(c => c.Stuff, StringComparer.Ordinal).ToArray();
        return closures.FirstOrDefault(c => c.Stuff == stuff) ?? closures.FirstOrDefault();
    }

    /// <summary>
    /// The capability-days a mood <paramref name="delta"/> of <paramref name="pawn"/> gains a day in <paramref name="outcome"/>
    /// (<see cref="ForecastHousing.GainPerDay"/> over the tier-1 <paramref name="horizon"/>); 0 for no delta. A pawn the
    /// outcome forecasts no mood for, or whose break risk it left unknown, throws <see cref="RequiredFactException"/> (#3835).
    /// </summary>
    internal static double MoodGain(double delta, string pawn, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon) =>
        ForecastHousing.GainPerDay(delta, outcome.Needs.GetValueOrDefault(pawn)?.Mood, context.MoodBreaks, context.Value.WellbeingWeight, horizon / GameTime.TicksPerDay)
            ?? throw new RequiredFactException("mood forecast:" + pawn);

    /// <summary>
    /// The credit rows of <paramref name="outcome"/>: per served priced room, its role's gains times the days from its
    /// service day to tier 2's horizon (<see cref="ForecastCoupledContext.InvestmentHorizonDays"/>; none before tier 2
    /// ran) plus its once gain (<see cref="RoomGain.Once"/>), each a gain (negative), and its role's <see cref="Unmodelled"/> mechanics named.
    /// </summary>
    internal static void Credit(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon,
        List<ForecastInvestmentContribution> rows, SortedSet<string> findings)
    {
        var days = context.InvestmentHorizonDays;
        if (days <= 0) return;
        foreach (var served in outcome.ServedRooms)
        {
            if (!context.PlannedRoomRoles.TryGetValue(served.Room, out var role) || !context.RoomCredits.TryGetValue(role, out var credit)) continue;
            var units = RingClosure.CreditDays(days, served.ServedAt);
            if (units <= 0) continue;
            var gains = credit(served, outcome, commitments, context, horizon);
            if (Unmodelled.TryGetValue(role, out var named)) findings.UnionWith(named(served, outcome, commitments, context, horizon));
            foreach (var gain in gains)
                rows.Add(new("", gain.Pawn, Key(role, served.Room, gain.Subject), units, -((gain.PerDay * units) + gain.Once)));
        }
    }

    /// <summary>
    /// The development units of the planned rooms, priced or not, which the count skips (#3828): each planned site of a room
    /// and each room's <see cref="ForecastPlannedSite.RoofUnit"/>. An unplaced shelf candidate (room 0) is no room's. Cached per context.
    /// </summary>
    internal static IReadOnlySet<string> PlannedUnits(ForecastCoupledContext context) => Cache.GetValue(context, OwedOf).PlannedUnits;

    /// <summary>
    /// The flooring and removal relief of each placed site or removal a priced room owns (<see cref="Priced"/>, a priced room's
    /// connectors included, #3815), which the development count skips: the room's credit counts it (#3651, #3796). Cached per context.
    /// </summary>
    internal static IReadOnlySet<string> PricedRelief(ForecastCoupledContext context) => Cache.GetValue(context, OwedOf).PricedRelief;

    /// <summary>One way a room is built: its ring stuff and every item (planned site, placed ring site, room item) it then owes.</summary>
    private sealed record Way(string Stuff, string[] Items);

    private sealed record Owed(IReadOnlyList<(int Room, Way[] Ways)> Rooms, IReadOnlyDictionary<string, EquatableArray<string>[]> Finishes,
        IReadOnlySet<string> PlannedUnits, IReadOnlySet<string> PricedRelief);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Owed> Cache = new();

    private static Owed OwedOf(ForecastCoupledContext context)
    {
        var finishes = new Dictionary<string, List<EquatableArray<string>>>(StringComparer.Ordinal);
        void Finished(string item, EquatableArray<string> by)
        {
            if (!finishes.TryGetValue(item, out var list)) finishes[item] = list = [];
            list.Add(by);
        }
        foreach (var site in context.PlannedSites) Finished(site.Site, site.Finishes);
        foreach (var site in context.RoomPrerequisites.Values.SelectMany(s => s)) Finished(site.Id, []);
        foreach (var piece in context.Closures.SelectMany(c => c.Placed).DistinctBy(p => p.Site)) Finished(piece.Site, piece.Finishes);
        foreach (var item in context.RoomItems) Finished(item.Item, item.Finishes);
        var sites = context.PlannedSites.ToLookup(s => s.Room);
        var items = context.RoomItems.SelectMany(i => i.Rooms.Select(r => (Room: r, i.Item))).ToLookup(p => p.Room, p => p.Item);
        // A prediction without ring work (a priced room's, read for its credit, #3802, #3835) closes nothing and names no stuff.
        var closures = context.Closures.Where(c => c.Ring.Count + c.Placed.Count > 0).ToLookup(c => c.Room);
        Way[] OwnWays(int room)
        {
            var stuffs = closures[room].Any() ? closures[room].Select(c => c.Stuff).Distinct().ToArray()
                : sites[room].Select(s => s.Stuff).Where(s => s.Length != 0).Distinct().DefaultIfEmpty("").ToArray();
            // A blocked piece (#3827) is owed nothing: the room serves once the rest is done.
            return [.. stuffs.Select(stuff => new Way(stuff, [.. sites[room].Where(s => s.Stuff.Length == 0 || s.Stuff == stuff).Select(s => s.Site)
                    .Concat(closures[room].Where(c => c.Stuff == stuff).SelectMany(c => c.Placed).Select(p => p.Site))
                    .Concat(items[room]).Concat(context.RoomPrerequisites.TryGetValue(room, out var prerequisites) ? prerequisites.Select(s => s.Id) : []).Distinct(StringComparer.Ordinal).Where(i => !context.BlockedItems.Contains(i))]))];
        }
        // A connector's work is part of each room it serves (#3815, P4): a room's ways are its own, each joined with each way
        // of every connector serving it, the room keeping its own ring stuff.
        var serving = context.Connectors.SelectMany(c => c.Value.Select(s => (Served: s, Connector: c.Key))).ToLookup(p => p.Served, p => p.Connector);
        var resolved = new Dictionary<int, Way[]>();
        Way[] WaysOf(int room)
        {
            if (resolved.TryGetValue(room, out var known)) return known;
            resolved[room] = [];
            IEnumerable<Way> ways = OwnWays(room);
            foreach (var connector in serving[room].Distinct().Order())
                if (WaysOf(connector) is { Length: > 0 } theirs)
                    ways = ways.SelectMany(mine => theirs.Select(their => new Way(mine.Stuff, [.. mine.Items.Concat(their.Items).Distinct(StringComparer.Ordinal)])));
            return resolved[room] = [.. ways.Where(w => w.Items.Length != 0)];
        }
        var rooms = new List<(int, Way[])>();
        foreach (var room in sites.Select(g => g.Key).Concat(closures.Select(g => g.Key)).Concat(items.Select(g => g.Key))
            .Concat(context.Connectors.Values.SelectMany(s => s)).Concat(context.RoomPrerequisites.Keys).Distinct().Order())
            if (WaysOf(room) is { Length: > 0 } ways) rooms.Add((room, ways));
        var planned = new HashSet<string>(context.PlannedSites.Where(s => s.Room > 0).Select(s => s.Site), StringComparer.Ordinal);
        foreach (var shelter in context.Shelters.Where(s => s.PlannedRoom != 0)) planned.Add(ForecastPlannedSite.RoofUnit(shelter.PlannedRoom));
        var pricedRelief = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in context.RoomItems.Where(i => i.Rooms.Any(context.Priced)))
        {
            pricedRelief.Add(Methods.Generation.ConstructionMethodCandidates.FlooringRelief + "/" + item.Item);
            pricedRelief.Add(Methods.Generation.ConstructionMethodCandidates.RemovalRelief + "/" + item.Item);
        }
        return new(rooms, finishes.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal), planned, pricedRelief);
    }

    /// <summary>
    /// The planned rooms one schedule serves, from the activities it completed (id to tick) (#3796, epic #3650 P2, replacing
    /// #3744's ring closure). A room is built by its one
    /// reconcile: per ring stuff (its closure predictions', else its sites'), it owes its planned sites in that stuff or
    /// none, the placed ring sites of that stuff's closure and the room items it holds (<see cref="ForecastCoupledContext.RoomItems"/>),
    /// with every way of each connector serving it (<see cref="ForecastCoupledContext.Connectors"/>, #3815), as one job, less
    /// its blocked pieces (<see cref="ForecastCoupledContext.BlockedItems"/>, #3827: research unfinished or no stuff stocked or
    /// obtainable, as PlannedRules.PlannedRoomDone reads them), which delay nothing. It serves at the latest finish once the schedule finishes all of one stuff's, the earliest stuff
    /// winning. A room the schedule leaves owing is projected when it is open
    /// (<see cref="ForecastCoupledContext.OpenRooms"/>: started, and not done by its latest reconcile, #3754) or the schedule finished one of its items: its remaining work is
    /// the owed work (<see cref="ForecastCoupledContext.OwedWork"/>) of its unfinished items in the stuff that leaves the
    /// least, and a stuff with an unfinished item without a row is not projected. The builders first spend the owed work the
    /// schedule finished, then work the open rooms, then the rooms this schedule opens, each group by its remaining work:
    /// the k-th serves at RingClosure.ClosedAt of the cumulative work (an item two rooms share counted once) over
    /// <see cref="ForecastCoupledContext.PlannedBuilders"/>, never before <paramref name="horizon"/>. A room no builder can
    /// finish (no free builder, or no stuff with every row: no option can do an item's work) is never served, and earns
    /// nothing (#3835); a room needing projecting while the builders are unread throws <see cref="RequiredFactException"/>.
    /// </summary>
    internal static EquatableArray<ForecastRoomService> ServiceDays(ForecastCoupledContext context,
        IReadOnlyDictionary<string, double> finishedAt, double horizon)
    {
        var owed = Cache.GetValue(context, OwedOf);
        if (owed.Rooms.Count == 0) return [];
        double At(string item)
        {
            var at = double.PositiveInfinity;
            foreach (var by in owed.Finishes[item])
                foreach (var finish in by)
                    if (finishedAt.TryGetValue(finish, out var tick)) at = Math.Min(at, tick);
            return at;
        }
        var done = new Dictionary<string, double>(StringComparer.Ordinal);
        bool Done(string item)
        {
            if (!done.TryGetValue(item, out var at)) done[item] = at = At(item);
            return double.IsFinite(at);
        }
        var work = context.OwedWork;
        var served = new List<ForecastRoomService>();
        var queue = new List<(bool Started, double Remaining, int Room, string Stuff, string[] Unfinished)>();
        foreach (var (room, ways) in owed.Rooms)
        {
            ForecastRoomService? finished = null;
            foreach (var way in ways)
                if (way.Items.All(Done) && way.Items.Max(i => done[i]) is var at && (finished is null || at < finished.ServedAt)) finished = new(room, way.Stuff, at);
            if (finished is not null) { served.Add(finished); continue; }
            var started = context.OpenRooms.Contains(room);
            if (!started && !ways.Any(w => w.Items.Any(Done))) continue;
            (double Remaining, Way Way, string[] Unfinished)? best = null;
            foreach (var way in ways)
            {
                var unfinished = way.Items.Where(i => !Done(i)).ToArray();
                if (!unfinished.All(work.ContainsKey)) continue;
                var remaining = unfinished.Sum(i => work[i]);
                if (best is null || remaining < best.Value.Remaining) best = (remaining, way, unfinished);
            }
            if (best is not { } chosen) continue;
            if (context.PlannedBuilders <= 0) continue;
            queue.Add((started, chosen.Remaining, room, chosen.Way.Stuff, chosen.Unfinished));
        }
        var counted = new HashSet<string>(StringComparer.Ordinal);
        var cumulative = 0.0;
        foreach (var item in owed.Rooms.SelectMany(r => r.Ways).SelectMany(w => w.Items))
            if (Done(item) && counted.Add(item) && work.TryGetValue(item, out var spent)) cumulative += spent;
        foreach (var (_, _, room, stuff, unfinished) in queue.OrderBy(q => q.Started ? 0 : 1).ThenBy(q => q.Remaining).ThenBy(q => q.Room))
        {
            foreach (var item in unfinished) if (counted.Add(item)) cumulative += work[item];
            served.Add(new(room, stuff, Math.Max(horizon, RingClosure.ClosedAt(cumulative, context.PlannedBuilders))));
        }
        return EquatableArray.From(served.OrderBy(s => s.Room));
    }
}
