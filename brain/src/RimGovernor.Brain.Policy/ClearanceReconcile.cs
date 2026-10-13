namespace RimGovernor.Brain.Policy;

// Go: clearance_reconcile.go
// The clear side of the reconciler: every planned room whose ground does not match the plan, standing or not,
// is reconciled, and the operations that take things down are batched by kind across rooms. The build side
// (wall, door, floor in, install, build) belongs to the owning concerns.

internal sealed record ClearedRoom(PlannedRoom Room, Reconciliation Rec);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The removal kinds in the order a pass takes them: other buildings first (one at a time, so removals
    /// cannot jointly invalidate the observed roof support), then packing, the door swaps, the roof and the
    /// walls, the floors last.
    /// </summary>
    private static readonly OpKind[] _clearKinds =
    [
        OpKind.FurnitureOut,
        OpKind.Pack,
        OpKind.PackInUse,
        OpKind.DoorOut,
        OpKind.RoofOff,
        OpKind.WallOut,
        OpKind.FloorOut,
    ];

    /// <summary>The plan's open-ground rooms, barns and vet rooms included, whose ring or doors differ from the plan.</summary>
    internal static List<PlannedRoom> GroundRooms(this LayoutPlan p, GroundCensus g) =>
        [.. p.RoomsWithHerd().Where(r => !r.Dug && !p.GroundMatches(r, g))];

    /// <summary>
    /// Reconciles every ground room against the flooring review's wanted floors: a floor is owed removal only
    /// where it is not the wanted one nor one that stands in for it. Without a decision the floors are left.
    /// </summary>
    private static List<ClearedRoom> ReconcileGround(
        LayoutPlan plan,
        GroundCensus g,
        IReadOnlyList<ClearanceTarget> rows,
        IReadOnlyList<ClearanceFloor> floors,
        RoomObservation rooms,
        RoomFloors? wants
    )
    {
        var output = new List<ClearedRoom>();
        foreach (var r in plan.GroundRooms(g))
        {
            var input = new ReconcileInput
            {
                Plan = plan,
                Room = r,
                Ground = g,
                Rows = EquatableArray.From(HerdOwnFurnitureLeft(r, rows, rooms.Shapes.Furniture)),
                Floors = EquatableArray.From(floors),
                Rooms = rooms,
            };
            if (wants is not null)
            {
                var (wanted, kept) = wants(r);
                input = input with { WantedFloor = wanted, FloorKept = kept };
            }

            output.Add(new ClearedRoom(r, Reconcile(input)));
        }

        return output;
    }

    /// <summary>
    /// The rows without the animal beds and the heater standing in a barn or hospital interior. The herd owner
    /// builds those beside the ring (its template is not the clear side's), so while the ring differs from the
    /// plan the clear side would deconstruct each one as it is finished and the owner would place it again.
    /// </summary>
    private static IReadOnlyList<ClearanceTarget> HerdOwnFurnitureLeft(PlannedRoom room, IReadOnlyList<ClearanceTarget> rows, RoomFurniture f)
    {
        if (room.Role != PlannedRole.Barn && room.Role != PlannedRole.Hospital)
        {
            return rows;
        }

        var output = new List<ClearanceTarget>();
        foreach (var row in rows)
        {
            var own = row.DefName.Length != 0 && (row.DefName == f.AnimalSpot || row.DefName == f.AnimalBed || row.DefName == f.Heater);
            if (
                own
                && RectInside(
                    room.Region.Bounds(),
                    new Rectangle
                    {
                        X = row.Minimum.X,
                        Z = row.Minimum.Z,
                        Width = row.Maximum.X - row.Minimum.X + 1,
                        Height = row.Maximum.Z - row.Minimum.Z + 1,
                    }
                )
            )
            {
                continue;
            }

            output.Add(row);
        }

        return output;
    }

    /// <summary>
    /// The next clearance method: the first removal kind with an operation ready in any planned room, all of
    /// its ready cells of every room in one batch (a single other building or door swap at a time), else the
    /// first retired ground with work. Rows are the census's player rows (SplitGroundRows). Null when the
    /// ground is clear.
    /// </summary>
    public static GroundStep? PlannedGroundStep(
        LayoutPlan plan,
        GroundCensus g,
        IReadOnlyList<ClearanceTarget> rows,
        IReadOnlyList<ClearanceFloor> floors,
        RoomObservation rooms,
        RetiredGround rg,
        RoomFloors? wants
    )
    {
        var crs = ReconcileGround(plan, g, rows, floors, rooms, wants);
        foreach (var kind in _clearKinds)
        {
            var targets = new List<ClearanceTarget>();
            var groundFloors = new List<ClearanceFloor>();
            var roof = new List<Cell>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seenRoof = new HashSet<Cell>();
            foreach (var cr in crs)
            {
                foreach (var op in cr.Rec.Ready.Items)
                {
                    if (op.Kind != kind)
                    {
                        continue;
                    }

                    foreach (var t in op.Targets.Items)
                    {
                        var id = TargetId(t);
                        if (id.Length != 0 && seen.Add(id))
                        {
                            targets.Add(t);
                        }
                    }

                    foreach (var f in op.Floors.Items)
                    {
                        if (seen.Add(GroundFloorID(f.Cell)))
                        {
                            groundFloors.Add(f);
                        }
                    }

                    if (kind == OpKind.RoofOff)
                    {
                        foreach (var c in op.Cells.Items)
                        {
                            if (seenRoof.Add(c))
                            {
                                roof.Add(c);
                            }
                        }
                    }
                }
            }

            if (targets.Count + groundFloors.Count + roof.Count == 0)
            {
                continue;
            }

            if (kind == OpKind.FurnitureOut || kind == OpKind.DoorOut)
            {
                targets = targets.GetRange(0, 1);
            }

            return new GroundStep
            {
                Phase = KindLabel(kind),
                Targets = EquatableArray.From(targets),
                Floors = EquatableArray.From(groundFloors),
                Roof = EquatableArray.From(roof),
            };
        }

        return RetiredGroundStep(rows, floors, rg.Ground, rooms, rg);
    }

    /// <summary>
    /// The clearance deficit planned ground owes: every removal the planned rooms owe (Reconcile) and every
    /// target building and floor retired ground holds, stable.
    /// </summary>
    public static List<string> PlannedGroundWork(
        LayoutPlan plan,
        GroundCensus g,
        IReadOnlyList<ClearanceTarget> rows,
        IReadOnlyList<ClearanceFloor> floors,
        RoomObservation rooms,
        RetiredGround rg,
        RoomFloors? wants
    )
    {
        var output = RetiredGroundWork(rows, floors, rg.Ground, rg);
        foreach (var cr in ReconcileGround(plan, g, rows, floors, rooms, wants))
        {
            foreach (var op in cr.Rec.Owed.Items)
            {
                if (!_clearKinds.Contains(op.Kind) || op.Kind == OpKind.RoofOff)
                {
                    continue;
                }

                foreach (var t in op.Targets.Items)
                {
                    var id = TargetId(t);
                    if (id.Length != 0)
                    {
                        output.Add(id);
                    }
                }

                foreach (var f in op.Floors.Items)
                {
                    output.Add(GroundFloorID(f.Cell));
                }
            }
        }

        return [.. output.Distinct().Order(StringComparer.Ordinal)];
    }

    /// <summary>Names a building target; empty for a ring wall the census does not list, which no action can address.</summary>
    private static string TargetId(ClearanceTarget t) => t.EntityID;
}
