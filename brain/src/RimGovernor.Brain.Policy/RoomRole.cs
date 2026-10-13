namespace RimGovernor.Brain.Policy;

/// <summary>One station standing in a room, as the role workers see it: its definition and the instance state the definition cannot decide.</summary>
public sealed record RoomPiece
{
    public string Def { get; init; } = "";

    /// <summary>
    /// The instance state a role worker tests: for a humanlike bed BedFree (also the meaning of the default),
    /// BedMedical, BedPrisoner or BedPrisonerMedical; IdeoAltar for an altar that carries an ideo structure
    /// meme. Other stations leave it Always.
    /// </summary>
    public RoleCondition State { get; init; }

    /// <summary>The pawns a bed is assigned to; empty for an unassigned bed.</summary>
    public EquatableArray<BedOwner> Owners { get; init; }
}

/// <summary>What RoomRoleWorker_Bedroom.IsBedroom reads of a bed's owner.</summary>
public sealed record BedOwner
{
    public string Pawn { get; init; } = "";

    public bool Juvenile { get; init; }

    /// <summary>The pawn's love cluster (Pawn.GetLoveCluster), the pawn included; read only for adults.</summary>
    public EquatableArray<string> Cluster { get; init; }

    /// <summary>The pawn's mother and father, read only for juveniles.</summary>
    public EquatableArray<string> Parents { get; init; }
}

/// <summary>Names a gameplay effect a native room role carries.</summary>
public enum RoleEffect
{
    [WireName("")]
    None = 0,

    /// <summary>The sleeping mood and room ownership of a bedroom or barracks.</summary>
    [WireName("sleep_mood")]
    SleepMood,

    /// <summary>The hospital's patient mood.</summary>
    [WireName("patient_mood")]
    PatientMood,

    /// <summary>The prison's prisoner mood.</summary>
    [WireName("prison_mood")]
    PrisonMood,

    /// <summary>The throne room's title requirement.</summary>
    [WireName("throne")]
    Throne,

    /// <summary>The worship room's ritual requirement.</summary>
    [WireName("worship")]
    Worship,

    /// <summary>A work table's rate times its NotInRoomRoleFactor (StatPart_WorkTableRoomRole).</summary>
    [WireName("bench_speed")]
    BenchSpeed,
}

/// <summary>A role effect a room does not get.</summary>
public sealed record LostEffect
{
    public RoleEffect Effect { get; init; }

    /// <summary>The role the effect needs.</summary>
    public RoomRole Role { get; init; }

    /// <summary>The role the room is scored as instead (the mood effects only).</summary>
    public RoomRole Got { get; init; }

    /// <summary>The bench that is slowed (BenchSpeed only).</summary>
    public string Def { get; init; } = "";

    /// <summary>The rate multiplier applied (BenchSpeed only).</summary>
    public double Factor { get; init; }
}

/// <summary>A room's predicted native role and every role's score.</summary>
public sealed record RolePrediction
{
    public RoomRole Role { get; init; }

    public IReadOnlyDictionary<RoomRole, double> Scores { get; init; } = new Dictionary<RoomRole, double>();
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The roles that carry a gameplay effect in the game, found by reading every consumer of Room.Role in the
    /// decompiled 1.6 (mood thought stages, room ownership, title and ritual requirements). The game offers no
    /// list of consumers, so this is a DefTables entry; every other role (dining, recreation, storeroom, barn,
    /// nursery, classroom, playroom, tomb, ...) has no native gameplay consumer and is ignored. The workbench
    /// penalty is not here: it comes from each bench's own NeedRole.
    /// </summary>
    internal static readonly IReadOnlyDictionary<RoomRole, RoleEffect> RoleEffects = new Dictionary<RoomRole, RoleEffect>
    {
        [RoomRole.Bedroom] = RoleEffect.SleepMood,
        [RoomRole.Barracks] = RoleEffect.SleepMood,
        [RoomRole.Hospital] = RoleEffect.PatientMood,
        [RoomRole.PrisonCell] = RoleEffect.PrisonMood,
        [RoomRole.PrisonBarracks] = RoleEffect.PrisonMood,
        [RoomRole.ThroneRoom] = RoleEffect.Throne,
        [RoomRole.WorshipRoom] = RoleEffect.Worship,
    };

    /// <summary>The generic Room role's constant score (RoomRoleWorker_Room.GetScore): any role scoring above it wins.</summary>
    internal const double GenericRoleScore = 0.99;

    /// <summary>
    /// Mirrors Room.UpdateRoomStatsAndRole for a proper room holding pieces: the role whose worker scores
    /// highest wins and the generic Room scores 0.99. Weights and which stations earn them come from the
    /// catalog (RoomRoleWorker.GetScore per building); the bed roles, whose workers test the whole set of
    /// beds, follow their decompiled logic below. A tie breaks to the lexically smaller role name: the game
    /// takes the first role in its def order, which the catalog does not carry. A piece whose definition is
    /// not in the catalog throws, never a silent zero.
    /// </summary>
    public static RolePrediction PredictRole(StationCatalog catalog, IReadOnlyList<RoomPiece> pieces)
    {
        var (beds, rest) = SplitBeds(catalog, pieces);
        var scores = new Dictionary<RoomRole, double> { [RoomRole.Room] = GenericRoleScore };
        var add = new Dictionary<RoomRole, double>();
        var flat = new Dictionary<RoomRole, double>();
        var floor = new Dictionary<RoomRole, double>();
        foreach (var p in rest)
        {
            var station = catalog.Stations.TryGetValue(p.Def, out var st) ? st : new Station();
            foreach (var c in station.Contributions)
            {
                if (!Contributes(c, p))
                {
                    continue;
                }
                if (c.Additive)
                {
                    add[c.Role] = add.GetValueOrDefault(c.Role) + c.Weight;
                    floor[c.Role] = Math.Max(floor.GetValueOrDefault(c.Role), c.Floor);
                }
                else
                {
                    flat[c.Role] = Math.Max(flat.GetValueOrDefault(c.Role), c.Weight);
                }
            }
        }
        foreach (var (role, v) in add)
        {
            scores[role] = Math.Max(v, floor.GetValueOrDefault(role));
        }
        foreach (var (role, v) in flat)
        {
            scores[role] = Math.Max(scores.GetValueOrDefault(role), v);
        }
        foreach (var (role, v) in BedScores(beds))
        {
            scores[role] = v;
        }
        var best = RoomRole.Room;
        var bestScore = scores[RoomRole.Room];
        foreach (var role in scores.Keys.OrderBy(r => Wire.Name(r), StringComparer.Ordinal))
        {
            if (scores[role] > bestScore)
            {
                best = role;
                bestScore = scores[role];
            }
        }
        return new RolePrediction { Role = best, Scores = scores };
    }

    /// <summary>Whether a piece earns a contribution: the definition alone for Always and Bindable (a trait of the def), else the piece's instance state.</summary>
    internal static bool Contributes(RoleContribution c, RoomPiece p) =>
        c.When is RoleCondition.Always or RoleCondition.Bindable || p.State == c.When;

    /// <summary>A humanlike bed as the bed role workers see it.</summary>
    internal sealed record BedForRole
    {
        public IReadOnlyList<BedOwner> Owners { get; init; } = [];

        /// <summary>bed_countsForBedroomOrBarracks.</summary>
        public bool Counts { get; init; }

        /// <summary>A bed too small for a child (nursery).</summary>
        public bool Baby { get; init; }

        /// <summary>bed_emptyCountsForBarracks.</summary>
        public bool EmptyCounts { get; init; }

        public bool Medical { get; init; }

        public bool Prisoner { get; init; }
    }

    /// <summary>
    /// Separates the humanlike beds, which the bed roles score as a set, from every other piece. An animal bed
    /// contributes through the catalog (Barn) like any other piece.
    /// </summary>
    internal static (List<BedForRole> Beds, List<RoomPiece> Others) SplitBeds(StationCatalog catalog, IReadOnlyList<RoomPiece> pieces)
    {
        var beds = new List<BedForRole>();
        var rest = new List<RoomPiece>();
        foreach (var p in pieces)
        {
            if (!catalog.Stations.TryGetValue(p.Def, out var st))
            {
                throw new DomainException($"room piece \"{p.Def}\" is not in the station catalog");
            }
            if (!HumanlikeBed(st))
            {
                rest.Add(p);
                continue;
            }
            var b = new BedForRole { Owners = p.Owners, EmptyCounts = st.EmptyCountsForBarracks };
            switch (p.State)
            {
                case RoleCondition.Always:
                case RoleCondition.BedFree:
                    break;
                case RoleCondition.BedMedical:
                    b = b with { Medical = true };
                    break;
                case RoleCondition.BedPrisoner:
                    b = b with { Prisoner = true };
                    break;
                case RoleCondition.BedPrisonerMedical:
                    b = b with { Medical = true, Prisoner = true };
                    break;
                default:
                    throw new DomainException($"bed \"{p.Def}\" has state \"{Wire.Name(p.State)}\", not a bed state");
            }
            foreach (var c in st.Contributions)
            {
                switch (c.Role)
                {
                    case RoomRole.Bedroom:
                        b = b with { Counts = true };
                        break;
                    case RoomRole.Nursery:
                        b = b with { Baby = true };
                        break;
                }
            }
            beds.Add(b);
        }
        return (beds, rest);
    }

    /// <summary>The catalog gives a humanlike bed the hospital and prison contributions and no other building those.</summary>
    internal static bool HumanlikeBed(Station st) =>
        st.Contributions.Any(c => c.When is RoleCondition.BedMedical or RoleCondition.BedPrisoner);

    /// <summary>
    /// The six bed-set workers (Bedroom, Barracks, Nursery, Hospital, PrisonCell, PrisonBarracks), each as its
    /// GetScore decompiles. The hospital worker stops at the first medical bed and so, scanning in an order
    /// the room does not give, a prisoner bed after it goes unseen; here any prisoner bed vetoes the hospital.
    /// </summary>
    internal static Dictionary<RoomRole, double> BedScores(IReadOnlyList<BedForRole> beds)
    {
        var output = new Dictionary<RoomRole, double>();
        if (beds.Count == 0)
        {
            return output;
        }
        // Bedroom and Barracks read only beds that count for them.
        var counted = new List<BedForRole>();
        bool bedroomVeto = false, barracksVeto = false;
        var free = 0;
        foreach (var b in beds)
        {
            if (!b.Counts)
            {
                continue;
            }
            counted.Add(b);
            bedroomVeto = bedroomVeto || b.Medical || b.Prisoner;
            barracksVeto = barracksVeto || b.Prisoner;
            if (!b.Medical)
            {
                free++;
            }
        }
        if (counted.Count > 0)
        {
            var single = IsBedroom(counted);
            if (!bedroomVeto && single)
            {
                output[RoomRole.Bedroom] = 100000;
            }
            if (!barracksVeto && !single)
            {
                output[RoomRole.Barracks] = free * 100100.0;
            }
        }
        bool hospital = false, anyPrisoner = false, nonPrisoner = false;
        int cell = 0, medicalCell = 0;
        foreach (var b in beds)
        {
            anyPrisoner = anyPrisoner || b.Prisoner;
            nonPrisoner = nonPrisoner || !b.Prisoner;
            hospital = hospital || (b.Medical && !b.Prisoner);
            if (b.Prisoner && b.Medical)
            {
                medicalCell++;
            }
            else if (b.Prisoner)
            {
                cell++;
            }
        }
        if (hospital && !anyPrisoner)
        {
            output[RoomRole.Hospital] = 100000;
        }
        if (!nonPrisoner)
        {
            if (cell == 1)
            {
                output[RoomRole.PrisonCell] = 170000;
            }
            else if (medicalCell == 1)
            {
                output[RoomRole.PrisonCell] = 100000;
            }
            if (cell + medicalCell > 1)
            {
                output[RoomRole.PrisonBarracks] = (cell * 100100.0) + (medicalCell * 50001.0);
            }
        }
        // Nursery: only baby beds, no prisoner bed, at least two (MinBabyBeds).
        int babies = 0;
        var nursery = true;
        foreach (var b in beds)
        {
            if (b.Medical)
            {
                continue;
            }
            if (b.Prisoner || !b.Baby)
            {
                nursery = false;
                break;
            }
            babies++;
        }
        if (nursery && babies >= 2)
        {
            output[RoomRole.Nursery] = babies * 100200.0;
        }
        return output;
    }

    /// <summary>RoomRoleWorker_Bedroom.IsBedroom: one unassigned bed, one assigned bed, or owners that are one love cluster of adults with only their own children.</summary>
    internal static bool IsBedroom(IReadOnlyList<BedForRole> beds)
    {
        var adults = new List<BedOwner>();
        var children = new List<BedOwner>();
        IReadOnlyList<string>? cluster = null;
        int empty = 0, assigned = 0, outsiders = 0;
        foreach (var b in beds)
        {
            if (b.Owners.Count == 0 && b.EmptyCounts)
            {
                empty++;
                continue;
            }
            if (b.Owners.Count > 0)
            {
                assigned++;
            }
            var outsider = false;
            foreach (var o in b.Owners)
            {
                if (o.Juvenile)
                {
                    children.Add(o);
                    continue;
                }
                adults.Add(o);
                // Go tested cluster == nil; an empty cluster slice is not nil there only when set non-nil, and
                // the census builds every adult's cluster with the pawn in it, so an empty one reads as unset.
                if (cluster is null)
                {
                    cluster = o.Cluster;
                }
                if (!cluster.Contains(o.Pawn))
                {
                    outsider = true;
                }
            }
            if (outsider)
            {
                outsiders++;
            }
        }
        if ((empty == 1 && assigned == 0) || (empty == 0 && assigned == 1))
        {
            return true;
        }
        if (empty > 0)
        {
            return false;
        }
        if (adults.Count == 0)
        {
            return true;
        }
        if (outsiders > 0)
        {
            return false;
        }
        foreach (var child in children)
        {
            if (!adults.Any(a => child.Parents.Contains(a.Pawn)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The role effects a room holding contents does not get when the game scores it other than intended: the
    /// intended role's own effect (sleep mood, patient mood, prison mood, throne, worship) when the predicted
    /// role differs, and every bench whose NeedRole names a role other than the predicted one, at that need's
    /// factor (the x0.8 workbench penalty). An intended role with no native gameplay consumer loses nothing by itself.
    /// </summary>
    public static List<LostEffect> LostEffects(StationCatalog catalog, RoomRole intended, IReadOnlyList<RoomPiece> pieces)
    {
        var pred = PredictRole(catalog, pieces);
        var output = new List<LostEffect>();
        if (RoleEffects.TryGetValue(intended, out var effect) && pred.Role != intended)
        {
            output.Add(new LostEffect { Effect = effect, Role = intended, Got = pred.Role });
        }
        foreach (var p in pieces)
        {
            var station = catalog.Stations.TryGetValue(p.Def, out var st) ? st : new Station();
            foreach (var n in station.Needs)
            {
                if (n.Kind == NeedKind.Role && n.Role != pred.Role)
                {
                    output.Add(new LostEffect { Effect = RoleEffect.BenchSpeed, Role = n.Role, Def = p.Def, Factor = n.Factor });
                }
            }
        }
        return output;
    }

    internal static List<string> RoleEffectRoles() =>
        RoleEffects.Keys.Select(r => Wire.Name(r)).Order(StringComparer.Ordinal).ToList();
}
