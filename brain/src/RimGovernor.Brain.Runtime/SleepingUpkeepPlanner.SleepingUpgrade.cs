using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

partial class SleepingUpkeepPlanner
{
    /// <summary>The next room quality upgrade: one template piece for an owned bedroom below its target.</summary>
    internal static (Pol.RoomUpgrade Upgrade, bool Due) RoomUpgradeOf(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        SleepingPlanningRules.RoomUpgradeOf(facts.Facts, facts.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray(), facts.Window is { } window ? window.Value.Cells : null);

    /// <summary>The next bed replacement step, read from the same census as RoomUpgradeOf.</summary>
    internal static (Pol.BedReplacement Replacement, bool Due) BedReplacementOf(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        SleepingPlanningRules.BedReplacementOf(facts.Facts, facts.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray(), facts.Window is { } window ? window.Value.Cells : null);

    /// <summary>The next unmet royal bedroom thing.</summary>
    internal static (Pol.RoomUpgrade Upgrade, bool Due) TitleFurniture(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        SleepingPlanningRules.TitleFurniture(facts.Facts, facts.Window is { } window ? window.Value.Cells : null);

    /// <summary>The next animal sleeping spot for a master's solo bedroom: none while the definition is unavailable or unsized.</summary>
    internal static (Pol.RoomUpgrade Upgrade, bool Due) CompanionBed(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        SleepingPlanningRules.CompanionBed(facts.Facts, facts.Window is { } window ? window.Value.Cells : null);

    /// <summary>The next beauty lever: a plant pot or a prettier floor for a bedroom whose weakest stat is beauty.</summary>
    internal static (Pol.RoomUpgrade Upgrade, bool Due) BeautyUpgrade(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        SleepingPlanningRules.BeautyUpgrade(facts.Facts, facts.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray(), facts.Window is { } window ? window.Value.Cells : null);
}

public sealed partial class SleepingUpkeepPlanner
{
    /// <summary>Deconstructs a replaced bed, once per bed per Episode.</summary>
    internal async Task<BuildingResult> RemoveOldBedAsync(ControlState state, Sto.ReviewState review, Sto.NeedOwner goal, ReviewReading reading, Pol.BedReplacement rep, CancellationToken call, CancellationToken epoch)
    {
        if (reading.Reading.Facts.Preferences.Protects(rep.Bed))
        {
            return new BuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "protected_thing") };
        }
        var p = Reviewer.Player;
        var method = new MethodId("bedroom-replace-remove-" + BuildingPlanner.DigestHex(rep.Bed, 8));
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return new BuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "old_bed_removal") };
        }
        // A real bed is packed, not deconstructed: the stored bed furnishes the next bedroom or bed spot (ReinstallStoredBed).
        if (rep.Def == "Bed" || rep.Def == SleepingRules.SleepingCoupleBedDefinition)
        {
            var value = new MoveBuilding(rep.Bed, rep.Def, rep.Cell, Rotation.South);
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewUninstallBuildingAction(new ActionId($"{id}-0"), value);
            var (result, _) = CommitSleepingPlan(state, goal, method, id, [action], call, epoch);
            return result;
        }
        return await Building.RetireBuildingAsync(state, review, goal, reading.Reading, rep.Bed, rep.Def, rep.Cell, "bedroom-replace-remove", "old_bed_removal", call, epoch).ConfigureAwait(false);
    }

    /// <summary>The colony's standing buildings, as the room furnishing counts them; none while the census is unknown.</summary>
    internal static IReadOnlyList<Pol.CurrentBuilding> StandingBuildings(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        facts.Facts.CurrentConstruction.Buildings;

    /// <summary>
    /// Places one lever piece (title furniture, companion bed, room upgrade, plant pot) through the room's one
    /// reconcile: the room's furnishing lists every lever piece standing there and the asked one, and the shared
    /// build side installs or builds what is missing. A census room that stands in no planned room has nowhere to reconcile.
    /// </summary>
    internal async Task<BuildingResult> FurnishBedroomAsync(PackedStock stock, ControlState state, Sto.ReviewState review, Sto.NeedOwner goal, ReviewReading reading, Pol.RoomUpgrade u, CancellationToken call, CancellationToken epoch)
    {
        var facts = reading.Reading;
        var rooms = facts.Facts.Upkeep.Rooms;
        var census = facts.Facts.CurrentConstruction;
        if (census is null || facts.Facts.LayoutPlan is not { } plan)
        {
            return new BuildingResult { Verdict = FieldUnavailable("room_census") };
        }
        var furniture = FurnitureRoomsRules.FurnitureRooms(rooms!, census!, facts.PlanningWindow.Cells).FirstOrDefault(r => r.Id == u.Room);
        var planned = furniture is null ? null : plan.Rooms.FirstOrDefault(r => r.Region.Bounds() == furniture.Room.Interior);
        if (planned is null)
        {
            return new BuildingResult { Verdict = NoSpace("bedroom_upgrade_site") };
        }
        var furnishing = SleepingRules.BedroomFurnishing(planned, rooms!.Shapes, "", census.Buildings, u);
        if (!furnishing.Absent)
        {
            return new BuildingResult { Verdict = NoSpace("bedroom_upgrade_site") };
        }
        var name = $"bedroom-upgrade-{u.Slot}-{BuildingPlanner.DigestHex(u.Room, 8)}";
        return await Building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile { Room = planned, Template = furnishing.Pieces, Name = name, Reason = "room upgrade " + u.Slot }, call, epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// Lays the beauty floor over a bedroom's cells, once per room per Episode. A floor is cells, not a listed
    /// piece, and it is laid over the floor already there rather than reconciled (a reconcile tears the old
    /// floor out first and keeps any floor that meets the room's tier), so it stays a special-case op.
    /// </summary>
    internal async Task<BuildingResult> LayBeautyFloorAsync(ControlState state, Sto.ReviewState review, Sto.NeedOwner goal, ReviewReading reading, Pol.RoomUpgrade u, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var facts = reading.Reading;
        var method = new MethodId($"bedroom-upgrade-{u.Slot}-{BuildingPlanner.DigestHex(u.Room, 8)}");
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return new BuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "bedroom_upgrade") };
        }
        var stuff = ColonyPlanningRules.BuildStuff(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), u.Def);
        var snapshot = state.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("layBeautyFloor: p.session.State() != state");
            }
        }
        Check();
        var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var selected = new List<Pol.Preview>();
        for (var i = 0; i < u.Cells.Count; i++)
        {
            var building = new Domain.Building(u.Def, u.Cells[i], u.Rot, stuff);
            var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-{i}")), building, ConstructionTier.Sustain);
            var preview = await Native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
            var v = preview.Preview;
            if (!v.CanPlace || !v.SafeToPlace)
            {
                continue;
            }
            stock = BuildingPlanner.MergeReviewStock(stock, preview.Stock);
            selected.Add(v);
        }
        if (selected.Count == 0)
        {
            return new BuildingResult { Verdict = NoSpace("bedroom_upgrade_site") };
        }
        return await Building.AdmitPreviewsAsync(new ReviewAdmission { State = state, Review = review, Owner = goal, Facts = facts, Method = method, Snapshot = snapshot, Selected = selected, Stock = stock, Purpose = Pol.Purpose.Shelter }, call, epoch).ConfigureAwait(false);
    }
}
