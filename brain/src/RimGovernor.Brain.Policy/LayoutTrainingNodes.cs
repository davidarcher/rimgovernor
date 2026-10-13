namespace RimGovernor.Brain.Policy;

/// <summary>Ports <c>layout_training_nodes.go</c>: the training range and the sparring ring as station-less room nodes of the station graph.</summary>
public static partial class PolicyFunctions
{
    /// <summary>Why the graph holds a training room: the colony's capable adults need a place to train.</summary>
    public const string ReasonTrainingRoom = "training_room_demanded";

    /// <summary>
    /// The range and ring nodes for stands range stands and markers ring markers; a count of zero asks for no such room.
    /// A first range is asked for at least RangeMinStands and a ring for at least RingMinMarkers, as the siting does.
    /// </summary>
    public static List<RoomNode> TrainingRoomNodes(int stands, int markers)
    {
        var nodes = new List<RoomNode>();
        if (stands > 0)
        {
            var n = Math.Min(Math.Max(stands, RangeMinStands), RangeMaxStands);
            nodes.Add(new RoomNode { Id = Wire.Name(PlannedTrainingRange), Role = PlannedTrainingRange, Area = n * (RangeDistance + 1), Width = RangeDistance + 1, Reason = ReasonTrainingRoom });
        }
        if (markers > 0)
        {
            var m = Math.Min(Math.Max(markers, RingMinMarkers), RingMaxMarkers);
            var ring = new RoomNode { Id = Wire.Name(PlannedSparringRing), Role = PlannedSparringRing, Area = RingSide * RingSide, Width = RingSide, Markers = m, Reason = ReasonTrainingRoom };
            if (stands > 0)
            {
                ring = ring with { Beside = Wire.Name(PlannedTrainingRange) };
            }
            nodes.Add(ring);
        }
        return nodes;
    }

    /// <summary>A role whose floor is its exact shape (a range's rows, the ring's square): a required link adds no door threshold to it, for it has no wall to open a door in.</summary>
    internal static bool ExactFloor(PlannedRole role) => role == PlannedTrainingRange || role == PlannedSparringRing;

    /// <summary>The embedding's interiors indexed by group (the zero rectangle for a group left out).</summary>
    internal static List<Rectangle> PlacedBlocks(IReadOnlyList<RoomGroup> groups, IEnumerable<EmbeddedBlock> blocks)
    {
        var output = Enumerable.Repeat(new Rectangle(), groups.Count).ToList();
        foreach (var b in blocks)
        {
            if (b.Group >= 0 && b.Group < output.Count)
            {
                output[b.Group] = b.Interior;
            }
        }
        return output;
    }

    /// <summary>The roles of the rooms the graph sites by their own terms, not as part of the base: the outskirts rooms, the training rooms and the shelter.</summary>
    private static bool ApartFromBase(PlannedRole role) =>
        role == PlannedTomb || role == PlannedMorgue || role == PlannedGraveyard || role == PlannedWasteYard || role == PlannedRole.Incinerator
        || role == PlannedTrainingRange || role == PlannedSparringRing || role == PlannedShelter;

    /// <summary>The bounding box of the placed blocks of the groups outside the rooms that stand apart: the base a facing group points away from. Null while none is placed.</summary>
    internal static Rectangle? AwayReference(IReadOnlyList<RoomGroup> groups, IReadOnlyList<Rectangle> blocks)
    {
        var reference = new Rectangle();
        var found = false;
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Width == 0 || ApartFromBase(groups[i].Planned))
            {
                continue;
            }
            if (!found)
            {
                (reference, found) = (b, true);
                continue;
            }
            int x0 = Math.Min(reference.X, b.X), z0 = Math.Min(reference.Z, b.Z);
            int x1 = Math.Max(reference.X + reference.Width, b.X + b.Width), z1 = Math.Max(reference.Z + reference.Height, b.Z + b.Height);
            reference = new Rectangle { X = x0, Z = z0, Width = x1 - x0, Height = z1 - z0 };
        }
        return found ? reference : null;
    }

    /// <summary>
    /// The direction the dummies of a range at r point: along the axis the rows are not on, toward the end farther from
    /// the reference's centre (a tie points to the + end). A range's rows run along X when its interior is taller than
    /// wide (stands never reach RangeDistance+1).
    /// </summary>
    internal static Rotation DummyFacing(Rectangle r, Rectangle reference)
    {
        if (r.Height > r.Width)
        {
            return (2 * r.Z) + r.Height >= (2 * reference.Z) + reference.Height ? Rotation.North : Rotation.South;
        }
        return (2 * r.X) + r.Width >= (2 * reference.X) + reference.Width ? Rotation.East : Rotation.West;
    }
}
