namespace RimGovernor.Brain.Policy;

/// <summary>
/// One sleeper's capability-days a day of sleeping in a planned room (#3748), per Impressiveness stage (the index): in a
/// bedroom and in a barracks, each its ForecastHousing.BedroomGain at that stage.
/// </summary>
public sealed record SleeperGains(EquatableArray<double> Bedroom, EquatableArray<double> Barracks);

/// <summary>
/// What the colony reads for the bed merge's price (#3658, #3748), read whole or naming the fact it could not read. Each
/// is a required planner input (#3747): a chain with a free-bed pool fails naming the one unread. A read absence of
/// obtainable enclosure stuff is a separate state, which keeps future geometry without claiming enclosure benefits.
/// </summary>
public abstract record BedMergeFacts
{
    private protected BedMergeFacts() { }

    /// <summary>
    /// Read: each pool sleeper's gains per stage (in id order); the game's Impressiveness stages the stage is read in (the
    /// catalog's RoomStatDef); and per wall cell and door of a planned bedroom's base ring, in the stuff the ring's sites are
    /// placed in, the capability-days it costs to build (labor plus material) and its work to build (ticks).
    /// </summary>
    public sealed record Read : BedMergeFacts
    {
        public required EquatableArray<SleeperGains> Sleepers { get; init; }

        public required ImpressivenessLevels Levels { get; init; }

        public required double WallCell { get; init; }

        public required double Door { get; init; }

        /// <summary>The work to build one wall cell of the ring, in ticks.</summary>
        public required double WallWork { get; init; }

        /// <summary>The work to build the ring's door, in ticks.</summary>
        public required double DoorWork { get; init; }
    }

    /// <summary>A fact the price needs is unread: <c>sleeper_mood</c> (a sleeper's gain at a stage), <c>impressiveness_levels</c> (no catalog stages), <c>wall_price</c> or <c>door_price</c> (the catalog shows no work or cost list for the ring's piece).</summary>
    public sealed record Unread(string Fact) : BedMergeFacts;

    /// <summary>The catalog, stock and map sources are read and offer no shared wall/door stuff. Rooms retain minimal
    /// future geometry, with no obtainable enclosure and no housing or heat credit.</summary>
    public sealed record NoStuffObtainable : BedMergeFacts;
}

/// <summary>Everything the clustering prices a bed merge with (#3658): the colony's facts, the climate the room's heat is read in, the builders, and the barracks the plan holds.</summary>
public sealed record BedMergePrice
{
    public required BedMergeFacts Facts { get; init; }

    /// <summary>The seasonal outdoor curve; the colony projection's read to harden (#3790).</summary>
    public EquatableArray<double> OutdoorCurve { get => ClimateRules.RequireOutdoorCurve(field); init; }

    /// <summary>A colonist's work-day (LayoutLightRules.StationWorkDayOf); a day with no price fails the heat it prices.</summary>
    public required StationWorkDay WorkDay { get; init; }

    /// <summary>The colonists able to build (LayoutClusterRules.BuildersOf); zero when none can build.</summary>
    public int Builders { get; init; }

    /// <summary>The bed slots of the barracks the plan holds (BedSlotsOf), 0 for none: the incumbent a new size must beat by more than the tie resolution.</summary>
    public int Incumbent { get; init; }

    /// <summary>
    /// The plan's committed barracks and bedrooms, each with what of its shell stands (#3661, #3675): a room standing or
    /// under construction. A size that leaves fewer rooms than these gives up the least spent of them and pays what was
    /// spent on each, so a split being built is not merged back by the next review; a room on paper is free to drop.
    /// </summary>
    public EquatableArray<ShellPieces> Committed { get; init; }
}

/// <summary>The free-bed pool's priced choice (#3748): the barracks size (1 keeps every bed single) and the interior areas of that room and of each single bedroom.</summary>
public sealed record BedMergeChoice(int Size, int Area, int SingleArea);

public static partial class LayoutClusterRules
{
    /// <summary>The free-bed pool's size was priced (#3658): the decision's verdict is merge for a barracks of more than one bed, split otherwise.</summary>
    public const string ReasonClusterBedMerge = "cluster_bed_merge_priced";

    /// <summary>
    /// The choice the input's free-bed pool takes (#3748, Clusterer.ChooseBeds): null when the graph has no free pool or
    /// the input prices no merge. A required fact unread fails it with a RequiredFactException (#3747).
    /// </summary>
    public static BedMergeChoice? BedMergeChoiceOf(ClusterInput input)
    {
        var c = new Clusterer(input, MaxRoomArea(input.RoofDistance));
        for (var i = 0; i < input.Graph.Nodes.Count; i++)
        {
            var n = input.Graph.Nodes[i];
            if (n.Purpose == StationPurpose.Sleep && n.Floor == 0 && n.Count > 0 && input.BedMerge is { } price)
            {
                return c.ChooseBeds(i, n.Count, price);
            }
        }
        return null;
    }

    /// <summary>
    /// The bed merge's facts (#3658, #3748): the sleepers' gains (null while any is unread, SleeperGainsOf), the catalog's
    /// stages (read when the dull stage is positive), and one wall cell and one door of a bedroom's ring, each priced by
    /// PiecePrice from its work and cost list (null when the catalog shows none), or the first of them unread.
    /// </summary>
    public static BedMergeFacts BedMergeFactsOf(EquatableArray<SleeperGains>? sleepers, ImpressivenessLevels levels,
        (double Work, IReadOnlyList<Amount> Costs)? wall, (double Work, IReadOnlyList<Amount> Costs)? door, IReadOnlyDictionary<Resource, ConstructionMaterialValue> values)
    {
        if (sleepers is not { } gains)
        {
            return new BedMergeFacts.Unread("sleeper_mood");
        }
        if (levels.Dull <= 0)
        {
            return new BedMergeFacts.Unread("impressiveness_levels");
        }
        if (wall is not { } w)
        {
            return new BedMergeFacts.Unread("wall_price");
        }
        if (door is not { } d)
        {
            return new BedMergeFacts.Unread("door_price");
        }
        return new BedMergeFacts.Read
        {
            Sleepers = gains, Levels = levels,
            WallCell = PiecePrice(w, values), Door = PiecePrice(d, values),
            WallWork = w.Work, DoorWork = d.Work,
        };
    }

    /// <summary>
    /// The sleepers' gains (#3658, #3748): per pawn in id order, the capability-days a day it gains in a bedroom and in a
    /// barracks at each of the game's Impressiveness stages (the clustering computes a room's stage from its size). Null
    /// while any gain of any pawn at any stage is unread (#3747), so the chain waits for it.
    /// </summary>
    public static EquatableArray<SleeperGains>? SleeperGainsOf(BedroomGainFacts gains, ImpressivenessLevels levels)
    {
        var sleepers = new List<SleeperGains>();
        foreach (var pawn in gains.Pawns.Keys.Order(StringComparer.Ordinal))
        {
            var bedroom = new double[levels.StageCount];
            var barracks = new double[levels.StageCount];
            for (var s = 0; s < levels.StageCount; s++)
            {
                // The stage argument is the housing forecast's to harden (#3897).
                if (gains.Gain(pawn, s) is not { } b || gains.Gain(pawn, s, barracks: true) is not { } k)
                {
                    return null;
                }
                (bedroom[s], barracks[s]) = (b, k);
            }
            sleepers.Add(new SleeperGains(EquatableArray.From(bedroom), EquatableArray.From(barracks)));
        }
        return EquatableArray.From(sleepers);
    }

    /// <summary>The capability-days of building one shell piece (#3658): its work to build over a day's ticks plus its cost list at the materials' values (ForecastValue.MaterialValueOf).</summary>
    public static double PiecePrice((double Work, IReadOnlyList<Amount> Costs) piece, IReadOnlyDictionary<Resource, ConstructionMaterialValue> values) =>
        (piece.Work / GameTime.TicksPerDay) + piece.Costs.Sum(a => a.Count * values[a.Resource].PerUnit);

    /// <summary>
    /// The colonists able to build (#3748): each available pawn with an enabled Construction work row it is capable of.
    /// With <paramref name="free"/>, only the pawns it
    /// names free count (#3744: the needs census's colonists no running method claims).
    /// </summary>
    public static int BuildersOf(WorkPawnsRead fact, Func<string, bool>? free = null)
    {
        var pawns = fact.Pawns;
        var n = 0;
        foreach (var pawn in pawns)
        {
            if (free is not null && !free(pawn.Id.Value))
            {
                continue;
            }
            if (!pawn.Available || pawn.Work is not PawnWorkTable.Works { Rows: var work })
            {
                continue;
            }
            var profile = PawnRules.BuildProfile(pawn);
            if (work.Any(row => row.Work == WorkType.Construction && !row.Disabled && profile.Capable(row.Work, 0)))
            {
                n++;
            }
        }
        return n;
    }
}

internal sealed partial class Clusterer
{
    /// <summary>The interior area the priced bed merge chose per free-bed unit (node, beds), which Eval sizes the unit's room by (#3748).</summary>
    private readonly Dictionary<(int Node, int Count), int> _bedArea = [];

    /// <summary>
    /// Splits the free-bed pool (the Sleep node that is no suite) by its priced bed merge (#3658, #3748): a barracks of the
    /// chosen size and the rest single, each room at its chosen area; a lone bed is sized alone. Null for any other node,
    /// or while the input prices no merge, which halves it as before.
    /// </summary>
    private List<ClusterUnit>? BedMergeSplit(int n, int count, List<ClusterDecision> decisions)
    {
        var node = _in.Graph.Nodes[n];
        if (_in.BedMerge is not { } price || node.Purpose != StationPurpose.Sleep || node.Floor > 0)
        {
            return null;
        }
        var choice = ChooseBeds(n, count, price);
        if (count > 1)
        {
            decisions.Add(new ClusterDecision
            {
                A = node.Id,
                Verdict = choice.Size > 1 ? LayoutClusterRules.ClusterMerge : LayoutClusterRules.ClusterSplit,
                Reason = LayoutClusterRules.ReasonClusterBedMerge,
            });
        }
        _bedArea[(n, choice.Size)] = choice.Area;
        _bedArea[(n, 1)] = choice.SingleArea;
        var output = new List<ClusterUnit> { new(n, choice.Size) };
        for (var i = choice.Size; i < count; i++)
        {
            output.Add(new ClusterUnit(n, 1));
        }
        return output;
    }

    /// <summary>
    /// Whether the placer furnishes the units in an empty block of the given sides (#3748): every station that stands on
    /// the floor, a barracks bed with the cell past its end kept clear as the tiler wants it (LayoutTile Wants), placed
    /// behind a door at the middle of one of its sides. The tiler likewise takes the exterior door the room is furnished
    /// behind (LayoutTile Furnishes).
    /// </summary>
    internal bool Furnishes(IReadOnlyList<ClusterUnit> us, (int W, int H) block, bool barracks)
    {
        var wants = new List<RegionWant>();
        foreach (var g in us.GroupBy(u => u.Node).OrderBy(g => g.Key))
        {
            var node = _in.Graph.Nodes[g.Key];
            var st = _in.Catalog.Stations[node.Def];
            if (st.Access == StationAccess.Wall)
            {
                continue;
            }
            var count = g.Sum(u => u.Count);
            var clear = barracks && st.Capacity.Kind == CapacityKind.Sleepers ? 1 : 0;
            wants.Add(new RegionWant { Slot = node.Id, Def = node.Def, Size = st.Shape.Size, Interaction = st.Shape.Interaction, Count = count, Min = count, Clear = clear });
        }
        var (w, h) = block;
        var region = RegionRules.RectRegion(new Rectangle(0, 0, w, h));
        bool Behind(Cell door)
        {
            try
            {
                RegionRules.PlaceInRegion(new RegionPlacement { Region = region, Doors = EquatableArray.From([door]), Wants = EquatableArray.From(wants) });
                return true;
            }
            catch (DomainException)
            {
                return false;
            }
        }
        return new[] { new Cell(w / 2, -1), new Cell(w / 2, h), new Cell(-1, h / 2), new Cell(w, h / 2) }.Any(Behind);
    }

    /// <summary>
    /// The least area from a free-bed group's own (Eval) up to the area limit whose block the placer furnishes the group
    /// in (#3748): a bed pool merged with other stations (a dining table) takes their clear cells too. The group's own
    /// area when no block up to the limit fits.
    /// </summary>
    internal int FurnishedArea(IReadOnlyList<ClusterUnit> us, GroupEval e)
    {
        var depth = LayoutClusterRules.RoomLimits(_in.RoofDistance).Depth;
        var barracks = e.PlannedRole() == PlannedRole.Barracks;
        (int W, int H) last = default;
        for (var a = e.Area; a <= MaxArea; a++)
        {
            var block = LayoutEmbedRules.BlockShapes(a, depth)[0];
            if (block == last)
            {
                continue;
            }
            last = block;
            if (Furnishes(us, block, barracks))
            {
                return a;
            }
        }
        return e.Area;
    }

    /// <summary>The cells the priced bed merge adds to a free-bed unit's room over its footprint minimum (#3748); 0 for any other unit.</summary>
    private int BedAreaExtra(ClusterUnit u, int minimum) => _bedArea.TryGetValue((u.Node, u.Count), out var area) ? Math.Max(0, area - minimum) : 0;

    /// <summary>
    /// The free-bed pool's barracks size and room areas (#3748): the (size k, interior area) whose value less cost is
    /// largest. The pool's beds take the sleepers, the spare beds gaining nothing: the k with the least loss sleeping in
    /// the barracks over a bedroom sleep in a barracks of k beds, the rest each in a bedroom (k = 1 builds every bed a
    /// bedroom). A room of interior area A takes the squarest block within the roof's depth (LayoutEmbedRules.BlockShapes);
    /// its Space (RoomSpaceRules.Space) at the bare-room baseline gives its Impressiveness and so its stage
    /// (ImpressivenessLevels.Stage), at which each sleeper gains its bedroom or barracks gain a day, less the room's heat a
    /// day (HeatPerDay). That value accrues from the room's ring closure to the horizon (ColonyModelPolicy.HorizonDays):
    /// the colony builds the rooms in the order that loses the least value (the most value a day per tick of ring work
    /// first), each ring's work its wall cells and door at the catalog's work, and a ring closes when the cumulative work
    /// over the builders (at least one) is done. Each room costs its ring's wall cells and door at their prices and its
    /// interior cells at the embedding's footprint and plain soil weights, in work-days at a walked cell's
    /// (LayoutRegroupRules.CellWalkDays). Interior areas run from the footprint minimum (Eval) to the area limit, a block
    /// counting only when the placer furnishes its beds (Furnishes); the
    /// singles' area is the one all singles are best at, and each larger size takes its best area beside them. The
    /// incumbent (the plan's barracks, else every bed single) holds unless the best beats it by more than
    /// ForecastValuePolicy.TieResolution. Every input is required (#3747): one unread throws a RequiredFactException naming it.
    /// </summary>
    /// <remarks>
    /// Switching (#3661). A split of a standing barracks keeps the barracks for the beds that stay (the embedding holds it
    /// where it stands for the smaller group) and builds a bedroom per bed taken out. The reverse pays what was spent
    /// (#3675): the plan's barracks and bedrooms that stand or are under construction (Committed) are its rooms, and a size
    /// that leaves fewer rooms gives up the least spent of them and pays the walls and doors standing on each, so a mood
    /// swinging about the switch line switches once; a bedroom on paper is free to drop, its flapping held by the tie
    /// resolution and the replan key.
    /// </remarks>
    internal BedMergeChoice ChooseBeds(int n, int count, BedMergePrice price)
    {
        if (price.Facts is BedMergeFacts.NoStuffObtainable)
        {
            // No enclosure can be built: retain one footprint-sized room for the bed pool, without buying walls or
            // valuing a hypothetical room's mood/heat. The normal tiler still proves that the beds fit.
            var single = FurnishedArea([new ClusterUnit(n, 1)], Eval([new ClusterUnit(n, 1)]));
            var choice = new BedMergeChoice(1, single, single);
            for (var k = 2; k <= count; k++)
            {
                var units = new[] { new ClusterUnit(n, k) };
                if (Eval(units).Area > MaxArea) break;
                var area = FurnishedArea(units, Eval(units));
                var roofDepth = LayoutClusterRules.RoomLimits(_in.RoofDistance).Depth;
                if (area <= MaxArea && Furnishes(units, LayoutEmbedRules.BlockShapes(area, roofDepth)[0], true))
                    choice = new BedMergeChoice(k, area, single);
            }
            return choice;
        }
        if (price.Facts is BedMergeFacts.Unread unread)
        {
            throw new RequiredFactException(unread.Fact);
        }
        var f = (BedMergeFacts.Read)price.Facts;
        var (sleepers, levels, wall, door) = (f.Sleepers, f.Levels, f.WallCell, f.Door);
        var builders = price.Builders;
        var walk = LayoutRegroupRules.CellWalkDays(_in.Catalog.Climate.TicksPerSecond);
        if (!QuestCapacityForecast.FinitePositive(walk))
        {
            throw new RequiredFactException("cell_walk");
        }
        if (sleepers.Any(s => s.Bedroom.Count < levels.StageCount || s.Barracks.Count < levels.StageCount))
        {
            throw new RequiredFactException("sleeper_mood");
        }
        _stageGains = new StageGains(levels, sleepers);
        var rooms = new BedRooms(this, price, levels, sleepers, count, wall, door, f.WallWork, f.DoorWork, Math.Max(1, builders),
            (PlanWeights.Footprint + (PlanWeights.Soil * BuildGround.SoilCostNormal)) * walk / PlanWeights.Walk);
        var depth = LayoutClusterRules.RoomLimits(_in.RoofDistance).Depth;
        var spent = price.Committed.Select(p => (p.Walls * wall) + (p.Doors * door)).Order().ToList();
        // The spend on the committed rooms a size gives up, least spent first: a barracks of k beds and count - k bedrooms
        // are count - (k - 1) rooms.
        double GivenUp(int k) => spent.Take(Math.Max(0, spent.Count - (count - (k - 1)))).Sum();

        // Each distinct block from the footprint minimum to the area limit, at the least area that takes it.
        IEnumerable<(int Area, (int W, int H) Block)> Areas(int minimum)
        {
            (int W, int H) last = default;
            for (var a = minimum; a <= MaxArea; a++)
            {
                var block = LayoutEmbedRules.BlockShapes(a, depth)[0];
                if (block != last)
                {
                    last = block;
                    yield return (a, block);
                }
            }
        }

        var singleMin = Eval([new ClusterUnit(n, 1)]).Area;
        var best1 = (Value: double.NegativeInfinity, Area: singleMin, Block: LayoutEmbedRules.BlockShapes(singleMin, depth)[0]);
        foreach (var (area, block) in Areas(singleMin))
        {
            var v = rooms.Value(1, block, block);
            if (v > best1.Value && Furnishes([new ClusterUnit(n, 1)], block, false))
            {
                best1 = (v, area, block);
            }
        }
        var values = new double[count + 1];
        var areas = new int[count + 1];
        values[1] = best1.Value - GivenUp(1);
        areas[1] = best1.Area;
        var best = 1;
        var top = count;
        for (var k = 2; k <= count; k++)
        {
            var minimum = Eval([new ClusterUnit(n, k)]).Area;
            if (minimum > MaxArea)
            {
                top = k - 1;
                break;
            }
            values[k] = double.NegativeInfinity;
            foreach (var (area, block) in Areas(minimum))
            {
                var v = rooms.Value(k, block, best1.Block) - GivenUp(k);
                if (v > values[k] && Furnishes([new ClusterUnit(n, k)], block, true))
                {
                    (values[k], areas[k]) = (v, area);
                }
            }
            if (values[k] > values[best])
            {
                best = k;
            }
        }
        var incumbent = Math.Max(1, price.Incumbent);
        if (incumbent <= top && best != incumbent && values[best] - values[incumbent] <= new ForecastValuePolicy().TieResolution)
        {
            best = incumbent;
        }
        return new BedMergeChoice(best, areas[best], best1.Area);
    }

    /// <summary>
    /// The capability-days a day of holding one bedroom of area cells in the living band behind a closed door to the
    /// outdoors. Every input is required on every map (#3747): an unread outdoor curve, work-day or living band, or a load
    /// the climate model cannot give, throws a RequiredFactException naming it.
    /// </summary>
    private double HeatPerDay(int area, BedMergePrice price)
    {
        var m = _in.Catalog.Climate;
        var curve = price.OutdoorCurve;
        if (price.WorkDay is not StationWorkDay.Priced day)
        {
            throw new RequiredFactException("work_day");
        }
        var workDay = day.W;
        if (!m.HasLiving())
        {
            throw new RequiredFactException("living_band");
        }
        try
        {
            var room = new ClimateRoom
            {
                Cells = area,
                Band = m.Living,
                Links = EquatableArray.From(new[] { new ClimateLink { Opening = Opening.DoorClosed, Side = new ClimateSide { Outdoors = true } } }),
            };
            return ClimateRules.ClimateLoad(m, curve, room, 0).MeanW / workDay;
        }
        catch (DomainException e)
        {
            throw new RequiredFactException("bedroom_heat: " + e.Message);
        }
    }

    /// <summary>The value of the pool's rooms for one candidate (ChooseBeds), with each block's heat and stage read once.</summary>
    private sealed class BedRooms(Clusterer c, BedMergePrice price, ImpressivenessLevels levels, EquatableArray<SleeperGains> sleepers, int count,
        double wall, double door, double wallWork, double doorWork, int builders, double floor)
    {
        private readonly Dictionary<int, double> _heat = [];
        private readonly double _horizon = ColonyModelPolicy.DefaultHorizonDays;

        /// <summary>
        /// The pool's value less its rooms' cost: a barracks of k beds in the block barracks (a bedroom when k is 1) and
        /// count - k bedrooms in the block single, each housing its sleepers from its ring's closure to the horizon.
        /// </summary>
        public double Value(int k, (int W, int H) barracks, (int W, int H) single)
        {
            var bedroomStage = Stage(single);
            var shared = k > 1 ? Stage(barracks) : bedroomStage;
            // Each bed's sleeper, the spare beds gaining nothing, by its loss sleeping in this barracks over this bedroom.
            var beds = new List<(double Bedroom, double Barracks)>(Math.Max(count, sleepers.Count));
            for (var i = sleepers.Count; i < count; i++)
            {
                beds.Add((0, 0));
            }
            foreach (var s in sleepers)
            {
                beds.Add((s.Bedroom[bedroomStage], k > 1 ? s.Barracks[shared] : s.Bedroom[shared]));
            }
            var ranked = beds.Select((b, i) => (b, i)).OrderBy(x => x.b.Bedroom - x.b.Barracks).ThenBy(x => x.i).Select(x => x.b).ToList();
            var rooms = new List<(double Rate, double Work, double Cost)> { Room(barracks, ranked.Take(k).Sum(b => b.Barracks)) };
            foreach (var b in ranked.Skip(k).Take(count - k))
            {
                rooms.Add(Room(single, b.Bedroom));
            }
            var total = 0.0;
            var work = 0.0;
            foreach (var r in rooms.OrderByDescending(r => r.Rate / r.Work))
            {
                work += r.Work;
                total += (r.Rate * RingClosure.CreditDays(_horizon, RingClosure.ClosedAt(work, builders))) - r.Cost;
            }
            return total;
        }

        /// <summary>A room of a block whose sleepers gain gained a day: its value a day less its heat, its ring's work and its cost.</summary>
        private (double Rate, double Work, double Cost) Room((int W, int H) block, double gained)
        {
            var cells = block.W * block.H;
            var walls = (2 * (block.W + block.H)) + 4 - 1; // the ring less its door cell
            return (gained - Heat(cells), (walls * wallWork) + doorWork, (walls * wall) + door + (cells * floor));
        }

        private int Stage((int W, int H) block) => levels.Stage(RoomSpaceRules.BareImpressiveness(block.W * block.H));

        private double Heat(int cells)
        {
            if (!_heat.TryGetValue(cells, out var heat))
            {
                heat = c.HeatPerDay(cells, price);
                _heat[cells] = heat;
            }
            return heat;
        }

    }
}
