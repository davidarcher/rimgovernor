namespace RimGovernor.Brain.Policy;

// Staging the Biotech child rooms: the nursery, playroom and classroom. A room role is the game's own score
// over the room's contents (Rooms wiki, Roles): a Nursery needs at least two baby beds (a crib or a baby
// sleeping spot) and no other bed, a Playroom scores each toy box and baby decoration and a Classroom each
// blackboard and school desk, both only while the room holds no humanlike bed. The planner owes a role's room
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
    public static readonly FurnitureRole Decoration = new("Decoration");
    public static readonly FurnitureRole Board = new("Board");
    public static readonly FurnitureRole Desk = new("Desk");
    public static readonly FurnitureRole DeathrestCasket = new("DeathrestCasket");
    public static readonly FurnitureRole DeathrestAccelerator = new("DeathrestAccelerator");

    /// <summary>Every role a planner places furniture by.</summary>
    public static readonly IReadOnlyList<FurnitureRole> All = [BabyBed, Toy, Decoration, Board, Desk, DeathrestCasket, DeathrestAccelerator];

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
    public RoomRole Role { get; init; }

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
                var ak = d.Available.TryGetValue(out var available);
                var sk = d.Size.TryGetValue(out var size);
                if (ak && available && sk && size.Width > 0 && size.Height > 0 && (!found || string.CompareOrdinal(d.Name, piece.Piece.Def) < 0))
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
    /// The child rooms the pawns owe, in role order: a nursery (beds for every newborn and baby, at least the
    /// game's two) while one lives, a playroom while a baby or child does, a classroom (a desk per child and
    /// one blackboard) while a child does, and a deathrest chamber (a casket per deathrester, and the
    /// accelerators its deathrest capacity beyond the casket allows) while a deathrester does. Pawns whose
    /// developmental stage or deathrest is unknown owe nothing.
    /// </summary>
    public static List<ChildRoomNeed> ChildRoomNeeds(IEnumerable<WorkPawn> pawns)
    {
        int babies = 0, toddlers = 0, children = 0, caskets = 0, accelerators = 0;
        foreach (var p in pawns)
        {
            if (!p.Biotech.TryGetValue(out var bt))
            {
                continue;
            }

            if (bt.Deathrest.TryGetValue(out var d) && d is not null)
            {
                caskets++;
                if (d.Capacity.TryGetValue(out var capacity) && capacity > 1)
                {
                    accelerators += capacity - 1;
                }
            }

            if (!bt.DevelopmentalStage.TryGetValue(out var stage))
            {
                continue;
            }

            switch (stage)
            {
                case RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Newborn:
                    babies++;
                    break;
                case RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Baby:
                    babies++;
                    toddlers++;
                    break;
                case RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Child:
                    children++;
                    toddlers++;
                    break;
            }
        }

        var output = new List<ChildRoomNeed>();
        if (babies > 0)
        {
            output.Add(NurseryNeed(babies));
        }

        if (toddlers > 0)
        {
            output.Add(PlayroomNeed());
        }

        if (children > 0)
        {
            output.Add(ClassroomNeed(children));
        }

        if (caskets > 0)
        {
            output.Add(DeathrestNeed(caskets, accelerators));
        }

        return output;
    }

    private static ChildRoomNeed NurseryNeed(int babies) =>
        new()
        {
            Role = RoomRole.Nursery,
            Module = PlannedRole.Nursery,
            Furniture = EquatableArray.From([new ChildFurniture { Role = FurnitureRole.BabyBed, Count = Math.Max(babies, MinNurseryBeds) }]),
        };

    private static ChildRoomNeed PlayroomNeed() =>
        new()
        {
            Role = RoomRole.Playroom,
            Module = PlannedRole.Playroom,
            Furniture = EquatableArray.From([new ChildFurniture { Role = FurnitureRole.Toy, Count = 1 }, new ChildFurniture { Role = FurnitureRole.Decoration, Count = 1 }]),
        };

    private static ChildRoomNeed ClassroomNeed(int children) =>
        new()
        {
            Role = RoomRole.Classroom,
            Module = PlannedRole.Classroom,
            Furniture = EquatableArray.From([new ChildFurniture { Role = FurnitureRole.Board, Count = 1 }, new ChildFurniture { Role = FurnitureRole.Desk, Count = children }]),
        };

    private static ChildRoomNeed DeathrestNeed(int caskets, int accelerators) =>
        new()
        {
            Role = RoomRole.DeathrestChamber,
            Module = PlannedRole.DeathrestChamber,
            Furniture = EquatableArray.From(
                [
                    new ChildFurniture { Role = FurnitureRole.DeathrestCasket, Count = caskets },
                    new ChildFurniture { Role = FurnitureRole.DeathrestAccelerator, Count = accelerators, Optional = true },
                ]
            ),
        };

    /// <summary>The child rooms a goal can name (#3496); the worship room rides <see cref="WorshipRules.WorshipRoomNeed"/>, which the ideoligion's required buildings size.</summary>
    internal static readonly IReadOnlyList<RoomRole> GoalRoles = [RoomRole.Nursery, RoomRole.Playroom, RoomRole.Classroom, RoomRole.DeathrestChamber];

    /// <summary>
    /// The rooms the goals name that no pawn owes a room for: the smallest room of each role (the game's two baby beds,
    /// a toy and a decoration, a blackboard and one desk, one casket). A role in <paramref name="have"/> keeps the need
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
                case RoomRole.Playroom:
                    output.Add(PlayroomNeed());
                    break;
                case RoomRole.Classroom:
                    output.Add(ClassroomNeed(1));
                    break;
                case RoomRole.DeathrestChamber:
                    output.Add(DeathrestNeed(1, 0));
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
