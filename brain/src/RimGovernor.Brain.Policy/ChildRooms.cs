using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// Staging the Biotech child rooms: the nursery and classroom. A room role is the game's own score
// over the room's contents (Rooms wiki, Roles): a Nursery needs at least two baby beds (a crib or a baby
// sleeping spot) and no other bed, and a Classroom scores each blackboard and school desk only while the room
// holds no humanlike bed. No playroom is planned (#3809): the babies' toy stands in the nursery (#3837). The planner owes a role's room
// while a pawn of the matching developmental stage lives in the colony: the plan grows a room sized to the
// furniture (GrowChildRoom), the sleeping planner reconciles it to the plan and the furniture at the
// template's slots (NextChildRoomStep, ReconcileRoom). Footprints are the native definition catalog's, never
// constants here. Using the rooms (feeding, play, lessons) is the next children's.

/// <summary>
/// A room-role furniture role the definition catalog's def rows give each definition (the PlanningDefinition
/// view's RoomRoles); a room's furniture is whichever catalog definitions carry the role, never a name list
/// here.
/// </summary>
public readonly record struct FurnitureRole(string Value)
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    // The roles the native catalog computes.
    public static readonly FurnitureRole BabyBed = new("BabyBed");
    public static readonly FurnitureRole Toy = new("Toy");
    public static readonly FurnitureRole Board = new("Board");
    public static readonly FurnitureRole Desk = new("Desk");
    public static readonly FurnitureRole DeathrestCasket = new("DeathrestCasket");
    public static readonly FurnitureRole DeathrestAccelerator = new("DeathrestAccelerator");

    /// <summary>Every role a planner places furniture by.</summary>
    public static readonly IReadOnlyList<FurnitureRole> All = [BabyBed, Toy, Board, Desk, DeathrestCasket, DeathrestAccelerator];

    public override string ToString() => Value;
}

/// <summary>
/// <see cref="Count"/> pieces of a role's furniture, each any one definition the catalog gives
/// <see cref="Role"/>, or any of <see cref="Defs"/> (the first available with a known footprint, by name, is
/// placed). An <see cref="Optional"/> entry the catalog has no available definition for is left out; any
/// other leaves the room unowed until the catalog has one.
/// </summary>
public sealed record ChildFurniture
{
    /// <summary>
    /// Selects the catalog definitions carrying the role; <see cref="Defs"/> instead names one definition the
    /// game itself requires (the worship room's buildings, read from the ideoligion).
    /// </summary>
    public FurnitureRole Role { get; init; }

    public EquatableArray<string> Defs { get; init; }

    public int Count { get; init; }

    public bool Optional { get; init; }
}

/// <summary>The room one role is owed and what furnishes it.</summary>
public sealed record ChildRoomNeed
{
    public PlanRole Role { get; init; }

    public PlannedRole Module { get; init; }

    public EquatableArray<ChildFurniture> Furniture { get; init; }

    /// <summary>The least interior cell count the room must have (a presence demand's RoomRequirement_Area, #3808); 0 none.</summary>
    public int MinArea { get; init; }

    /// <summary>The impressiveness the room should reach (a presence demand's RoomRequirement_Impressiveness); 0 none.</summary>
    public int MinImpressiveness { get; init; }

    /// <summary>The terrain tags every floor cell must carry one of (RoomRequirement_TerrainWithTags); none for any floor.</summary>
    public EquatableArray<string> FloorTags { get; init; }

    /// <summary>The building definitions the room must not hold (RoomRequirement_ForbiddenBuildings).</summary>
    public EquatableArray<string> ForbiddenDefs { get; init; }

    /// <summary>
    /// Picks each furniture entry's definition from the catalog: the first by name that carries the role, is
    /// available and has a known footprint. Null when a required entry has none (the room cannot be sized or
    /// furnished from unknown facts).
    /// </summary>
    internal List<ChildPiece>? Resolve(IReadOnlyList<FurnitureDefinition> defs)
    {
        var output = new List<ChildPiece>(Furniture.Count);
        foreach (var f in Furniture.Items)
        {
            if (f.Count <= 0)
            {
                continue;
            }

            var piece = new ChildPiece();
            var found = false;
            foreach (var d in defs)
            {
                if ((!f.Role.IsEmpty && !d.HasRole(f.Role)) || (f.Role.IsEmpty && !f.Defs.Items.Contains(d.Name)))
                {
                    continue;
                }

                piece.Defs.Add(d.Name);
                var available = d.Available;
                var size = d.Size;
                if (available && size.Width > 0 && size.Height > 0 && (!found || string.CompareOrdinal(d.Name, piece.Piece.Def) < 0))
                {
                    piece.Piece = new InteriorPieceDef { Def = d.Name, Size = new Cell(size.Width, size.Height) };
                    found = true;
                }
            }

            if (found)
            {
                piece.Count = f.Count;
                output.Add(piece);
            }
            else if (!f.Optional)
            {
                return null;
            }
        }

        return output;
    }

    internal ChildRoomShape? Shape(IReadOnlyList<FurnitureDefinition> defs)
    {
        var pieces = Resolve(defs);
        if (pieces is null)
        {
            return null;
        }

        var counts = new List<PieceCount>();
        foreach (var p in pieces)
        {
            var i = counts.FindIndex(c => c.Size == p.Piece.Size);
            if (i < 0)
            {
                counts.Add(new PieceCount { Size = p.Piece.Size });
                i = counts.Count - 1;
            }

            counts[i] = counts[i] with { Count = counts[i].Count + p.Count };
        }

        return new ChildRoomShape { Module = Module, Pieces = EquatableArray.From(counts), MinArea = MinArea };
    }
}

/// <summary>
/// One resolved furniture entry: the definition placed (first available with a known footprint), every def
/// that counts towards it, and how many are wanted.
/// </summary>
internal sealed class ChildPiece
{
    public InteriorPieceDef Piece { get; set; } = new();

    public List<string> Defs { get; } = [];

    public int Count { get; set; }
}

/// <summary>A room the plan must grow: its role and the footprints it must hold.</summary>
public sealed record ChildRoomShape
{
    public PlannedRole Module { get; init; }

    public EquatableArray<PieceCount> Pieces { get; init; }

    /// <summary>The least interior cell count the room must have; 0 none.</summary>
    public int MinArea { get; init; }
}

/// <summary><see cref="Count"/> pieces of one footprint.</summary>
public sealed record PieceCount
{
    public Cell Size { get; init; }

    public int Count { get; init; }
}

/// <summary>The next child room step.</summary>
public enum ChildRoomStepKind
{
    /// <summary>Nothing is due, or a fact is unknown.</summary>
    None,

    /// <summary>
    /// The room differs from the plan and the role's template (Template: the furniture the role scores): the
    /// build side reconciles it (ReconcileRoom). The room's state is whatever the diff leaves; there is no
    /// shell or place step.
    /// </summary>
    [WireName("reconcile")]
    Reconcile,
}

/// <summary>One bounded step towards an owed child room.</summary>
public sealed record ChildRoomStep
{
    public ChildRoomStepKind Kind { get; init; }

    public ChildRoomNeed? Need { get; init; }

    public PlannedRoom? Room { get; init; }

    /// <summary>The wanted furniture: each role's standing pieces where they stand, the missing ones in free template slots.</summary>
    public EquatableArray<WantedPiece> Template { get; init; }

    /// <summary>Whether the planner can act on the step now.</summary>
    public bool Owed() => Kind == ChildRoomStepKind.Reconcile;
}

public static class ChildRoomsRules
{
    /// <summary>The baby bed count below which the game scores a room no Nursery at all.</summary>
    internal const int MinNurseryBeds = 2;

    /// <summary>Whether the catalog assigns the definition the role.</summary>
    public static bool HasRole(this FurnitureDefinition d, FurnitureRole role) => d.Roles.Contains(role.Value);

    /// <summary>
    /// The child rooms the pawns owe, in role order: a nursery (beds for every newborn and baby, at least the game's two, and a toy box
    /// for their play) while one lives, a classroom (a desk per child and
    /// one blackboard) while a child does, and a deathrest chamber while a deathrester does: a casket per deathrester, which
    /// does not count toward its deathrest capacity, and the buildings that raise its deathrest efficiency (a factor above 1,
    /// the accelerator) as many as it binds (<see cref="ForecastDeathrest.Binds"/>, the credit's rule: each def's stackLimit
    /// and the capacity, #3842). A pawn without Biotech owes nothing, and one whose developmental stage is unknown owes no child
    /// room. A pawn whose deathrest section is unread (a failed native read), or a deathrester whose capacity or whose
    /// <paramref name="deathrest"/> defs are unread, raises <see cref="RequiredFactException"/> (#3835).
    /// </summary>
    public static List<ChildRoomNeed> ChildRoomNeeds(IEnumerable<WorkPawn> pawns, DeathrestDefsRead? deathrest)
    {
        int babies = 0, children = 0, caskets = 0;
        var bound = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in pawns)
        {
            if (p.Biotech is not PawnBiotechStatus.Active { Facts: var bt })
            {
                continue;
            }

            if (bt.Deathrest is { } d)
            {
                caskets++;
                var defs = deathrest ?? throw new RequiredFactException("deathrest defs");
                foreach (var (bindable, count) in ForecastDeathrest.Binds(defs.Bindables.Where(b => b.Factor > 1).Select(b => (b, int.MaxValue)), d.Capacity))
                {
                    // A def neither stack-limited nor counting toward the capacity binds without bound; the game ships none.
                    bound[bindable.Def] = count == int.MaxValue
                        ? throw new InvalidOperationException("deathrest bindable " + bindable.Def + " has no stack limit and does not count toward capacity")
                        : bound.GetValueOrDefault(bindable.Def) + count;
                }
            }

            switch (bt.DevelopmentalStage)
            {
                case RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Newborn:
                    babies++;
                    break;
                case RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Baby:
                    babies++;
                    break;
                case RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Child:
                    children++;
                    break;
            }
        }

        var output = new List<ChildRoomNeed>();
        if (babies > 0)
        {
            output.Add(NurseryNeed(babies));
        }

        if (children > 0)
        {
            output.Add(ClassroomNeed(children));
        }

        if (caskets > 0)
        {
            output.Add(DeathrestNeed(caskets, bound));
        }

        return output;
    }

    private static ChildRoomNeed NurseryNeed(int babies) =>
        new()
        {
            Role = RoomRole.Nursery,
            Module = PlannedRole.Nursery,
            // The babies' play buildings stand where they sleep (#3837): a toy box is the building BabyPlayGiver_PlayToys plays
            // at, looked for in the baby's room first. The layout plans one into the nursery (LayoutCluster's Play); it is
            // placed once the catalog has one available, and the room is a nursery without it.
            Furniture = EquatableArray.From(
                [
                    new ChildFurniture { Role = FurnitureRole.BabyBed, Count = Math.Max(babies, MinNurseryBeds) },
                    new ChildFurniture { Role = FurnitureRole.Toy, Count = 1, Optional = true },
                ]
            ),
        };

    private static ChildRoomNeed ClassroomNeed(int children) =>
        new()
        {
            Role = RoomRole.Classroom,
            Module = PlannedRole.Classroom,
            Furniture = EquatableArray.From([new ChildFurniture { Role = FurnitureRole.Board, Count = 1 }, new ChildFurniture { Role = FurnitureRole.Desk, Count = children }]),
        };

    /// <summary>The chamber: <paramref name="caskets"/> caskets, and each bound def by name, placed once the catalog has it available.</summary>
    private static ChildRoomNeed DeathrestNeed(int caskets, IEnumerable<KeyValuePair<string, int>> bound) =>
        new()
        {
            Role = RoomRole.DeathrestChamber,
            Module = PlannedRole.DeathrestChamber,
            Furniture = EquatableArray.From(
                [
                    new ChildFurniture { Role = FurnitureRole.DeathrestCasket, Count = caskets },
                    .. bound.Select(b => new ChildFurniture { Defs = [b.Key], Count = b.Value, Optional = true }),
                ]
            ),
        };

    /// <summary>
    /// The deathrest chamber's furniture among <paramref name="needs"/> as the reconcile places it (#3810): each entry's resolved
    /// def and count. None while no chamber is owed or a required entry has no available def.
    /// </summary>
    public static EquatableArray<DeathrestPiece> DeathrestFurniture(IEnumerable<ChildRoomNeed> needs, IReadOnlyList<FurnitureDefinition> defs) =>
        needs.FirstOrDefault(n => n.Role == RoomRole.DeathrestChamber)?.Resolve(defs) is { } pieces
            ? EquatableArray.From(pieces.Select(p => new DeathrestPiece(p.Piece.Def, p.Count)))
            : [];

    /// <summary>The child rooms a goal can name (#3496); the worship room rides <see cref="WorshipRules.WorshipRoomNeed"/>, which the ideoligion's required buildings size.</summary>
    internal static readonly IReadOnlyList<RoomRole> GoalRoles = [RoomRole.Nursery, RoomRole.Classroom, RoomRole.DeathrestChamber];

    /// <summary>
    /// The rooms the goals name that no pawn owes a room for: the smallest room of each role (the game's two baby beds,
    /// a blackboard and one desk, one casket). A role in <paramref name="have"/> keeps the need
    /// the pawns size, which is never smaller.
    /// </summary>
    public static List<ChildRoomNeed> GoalRoomNeeds(IEnumerable<RoomRole> roles, IReadOnlyCollection<ChildRoomNeed> have)
    {
        var output = new List<ChildRoomNeed>();
        foreach (var role in roles.Distinct())
        {
            if (have.Any(n => n.Role == role))
            {
                continue;
            }

            switch (role)
            {
                case RoomRole.Nursery:
                    output.Add(NurseryNeed(MinNurseryBeds));
                    break;
                case RoomRole.Classroom:
                    output.Add(ClassroomNeed(1));
                    break;
                case RoomRole.DeathrestChamber:
                    output.Add(DeathrestNeed(1, []));
                    break;
            }
        }

        return output;
    }

    /// <summary>
    /// The shapes of the owed child rooms the plan holds no fitting room for; empty while every owed room is
    /// planned or its furniture is unknown.
    /// </summary>
    public static List<ChildRoomShape> ChildRoomsOwed(LayoutPlan plan, IEnumerable<ChildRoomNeed> needs, IReadOnlyList<FurnitureDefinition> defs)
    {
        var output = new List<ChildRoomShape>();
        foreach (var n in needs)
        {
            if (n.Shape(defs) is not { } s)
            {
                continue;
            }

            if (!plan.ChildRoomFor(s, out _))
            {
                output.Add(s);
            }
        }

        return output;
    }

    /// <summary>
    /// Picks the first owed child room whose ground differs from its plan and template. A role whose plan room
    /// is missing (the layout review owes it) or whose furniture is unresolved is passed over, as is a room
    /// whose ring, doors and furniture match (a piece with no free slot is not wanted).
    /// </summary>
    public static ChildRoomStep NextChildRoomStep(
        LayoutPlan plan,
        RoomObservation rooms,
        GroundCensus ground,
        IReadOnlyList<CurrentBuilding> built,
        IEnumerable<ChildRoomNeed> needs,
        IReadOnlyList<FurnitureDefinition> defs
    )
    {
        foreach (var n in needs)
        {
            if (n.Shape(defs) is not { } shape || !plan.ChildRoomFor(shape, out var room))
            {
                continue;
            }

            var pieces = n.Resolve(defs) ?? [];
            var (interior, interiorOk) = InteriorRules.InteriorRoomFromLayout(room, rooms.Shapes);
            if (!interiorOk || interior is null)
            {
                continue;
            }

            var furnishing = RoomFurnishingRules.RoomFurnishing(
                room,
                interior,
                [.. pieces.Select(p => new FurnishingWant { Things = p.Defs, Count = p.Count, Piece = p.Piece })],
                built
            );

            if (furnishing.Absent || !plan.GroundMatches(room, ground))
            {
                return new ChildRoomStep
                {
                    Kind = ChildRoomStepKind.Reconcile,
                    Need = n,
                    Room = room,
                    Template = EquatableArray.From(furnishing.Pieces),
                };
            }
        }

        return new ChildRoomStep();
    }

    /// <summary>The buildings of any of <paramref name="defs"/> inside the room's interior.</summary>
    internal static int StandingChildPieces(PlannedRoom r, IReadOnlyCollection<string> defs, IEnumerable<CurrentBuilding> built)
    {
        var n = 0;
        foreach (var b in built)
        {
            if (b.Cells.Count > 0 && FurnitureRoomsRules.RectInside(r.Region.Bounds(), PlacementSearchRules.CellsRectangle(b.Cells.Items)) && defs.Contains(b.Building!.Definition))
            {
                n++;
            }
        }

        return n;
    }
}
