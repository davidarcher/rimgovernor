namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>The fireproof stuff for the shell's wall and door, or the refusal that explains why none exists.</summary>
    internal static (string WallStuff, string DoorStuff, Verdict Refusal, bool Ok) FireproofShellStuff(PlanningDefinition wall, PlanningDefinition door)
    {
        string wallStuff;
        try
        {
            wallStuff = wall.FireproofStuff();
        }
        catch (DomainException e)
        {
            return ("", "", FireproofRefusal(e), false);
        }

        string doorStuff;
        try
        {
            doorStuff = door.FireproofStuff();
        }
        catch (DomainException e)
        {
            return ("", "", FireproofRefusal(e), false);
        }

        return (wallStuff, doorStuff, default, true);
    }

    internal static Verdict FireproofRefusal(Exception err) =>
        err is FlammableStuffException ? SiteBlocked("incinerator", "flammable_wall_or_door") : FieldUnavailable("incinerator_stuff");

    internal static (LayoutPlan Plan, IReadOnlyList<PlannedRoom> Rooms, bool Known) IncineratorRooms(ColonyProjection facts)
    {
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!pk || !ck || !census!.Colony)
        {
            return (plan!, [], false);
        }

        return (plan!, plan!.IncineratorRooms(), true);
    }

    /// <summary>The first incinerator room whose shell ring is still owed.</summary>
    internal static (PlannedRoom? Room, bool Owed) IncineratorStep(ColonyProjection facts)
    {
        var (plan, rooms, known) = IncineratorRooms(facts);
        if (!known)
        {
            return (null, false);
        }

        foreach (var room in rooms)
        {
            if (RoomRingOwed(facts, plan, room))
            {
                return (room, true);
            }
        }

        return (null, false);
    }

    /// <summary>The first incinerator room whose shell is complete, or null.</summary>
    internal static PlannedRoom? StandingIncinerator(ColonyProjection facts)
    {
        var (plan, rooms, known) = IncineratorRooms(facts);
        if (!known)
        {
            return null;
        }

        foreach (var room in rooms)
        {
            if (!RoomRingOwed(facts, plan, room))
            {
                return room;
            }
        }

        return null;
    }

    internal static Fact<bool> IncineratorOwed(ColonyProjection facts)
    {
        var (_, _, known) = IncineratorRooms(facts);
        if (!known)
        {
            return Unknown<bool>();
        }

        var (_, owed) = IncineratorStep(facts);
        return Known(owed);
    }

    internal static Fact<bool> IncinerationOwed(ColonyProjection facts)
    {
        if (!IncineratorOwed(facts).TryGetValue(out var shell))
        {
            return Unknown<bool>();
        }

        if (shell)
        {
            return Known(true);
        }

        var room = StandingIncinerator(facts);
        if (room is null || FireBurning(facts))
        {
            return Known(false);
        }

        facts.Facts.Upkeep.Filth.TryGetValue(out var filth);
        return Known(PolicyFunctions.IncineratorAsh(filth ?? [], room.Region.Bounds()).Count > 0 || IncineratorStored(facts, room.Region.Bounds()) >= PolicyFunctions.BurnStoredCells);
    }

    internal static MethodId IncineratorMethod(PlannedRoom room) =>
        new($"incinerator-shell-{room.Region.Bounds().X}-{room.Region.Bounds().Z}");
}

public sealed partial class RoundsIncinerationPlanner
{
    /// <summary>The burn first, then the shell ring reconcile; not handled when neither is due.</summary>
    internal (RoundsIncinerationResult Result, bool Handled) StageDisposal(CancellationToken call, CancellationToken epoch, ControlState state, Rounds review, StandardState goal, StepArbiter arbiter, RoundsReading reading)
    {
        var (burn, handledBurn) = StageBurn(call, epoch, state, review, goal, arbiter, reading);
        if (handledBurn)
        {
            return (burn, true);
        }

        if (Building is null)
        {
            return (default, false);
        }

        var (room, owed) = IncineratorStep(reading.Projection);
        if (!owed)
        {
            return (default, false);
        }

        var rc = new RoomReconcile { RingOnly = true, Room = room!, Name = IncineratorMethod(room!).Value, Reason = "burn waste", Stuff = FireproofShellStuff };
        var result = Building.ReconcileRoom(call, epoch, state, review, goal, new RoundsReading(reading.ColonyReading), null, rc);
        return (new RoundsIncinerationResult { Verdict = result.Verdict }, true);
    }
}
