using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed class CombatRestorationPendingException(string message, Exception? inner = null) : Exception(message, inner);

public sealed partial class RoundsDefensePlanner
{
    /// <summary>
    /// Commits original settings before the first temporary mutation. Subsequent tactical changes never replace
    /// the original values. Returns the orders that may be sent: a door or animal order whose original setting
    /// cannot be read is dropped (the next stop proposes it again) rather than failing the batch, which also
    /// carries the drafts and attacks that answer the hostile.
    /// </summary>
    internal async Task<IReadOnlyList<CombatOrder>> CaptureCombatSettingsAsync(ControlState state, PlanId fight, IReadOnlyList<CombatOrder> orders, CancellationToken ct)
    {
        if (!orders.Any(o => o.Kind is OrderKind.Door or OrderKind.AnimalArea))
        {
            return orders;
        }
        var journal = _reviewer.Player.Journal;
        var (kept, exists) = await journal.LoadCombatRestorationAsync(ct).ConfigureAwait(false);
        var world = new World(state.Snapshot.Colony, state.Snapshot.Load, state.Snapshot.Map);
        if (exists && (kept.Owner != fight || kept.World != world))
        {
            throw new ControlException("prior combat restoration pending");
        }
        if (!exists)
        {
            kept = new CombatRestoration { World = world, Owner = fight };
        }
        var combat = await _native.ReadCombatAsync(Boundary.Identity(state.Snapshot), ct).ConfigureAwait(false);
        var known = combat.DoorStates.TryGetValue(out var doors);
        var sendable = new List<CombatOrder>(orders.Count);
        var keptDoors = kept.Doors.ToList();
        var keptAnimals = kept.Animals.ToList();
        foreach (var o in orders)
        {
            if (o.Kind == OrderKind.Door && !keptDoors.Any(d => d.Cell == o.Cell))
            {
                var i = known ? doors!.ToList().FindIndex(d => d.Cell == o.Cell) : -1;
                if (i < 0)
                {
                    continue;
                }
                if (!doors![i].HoldOpen.TryGetValue(out var hold) || !doors[i].Forbidden.TryGetValue(out var forbidden))
                {
                    continue;
                }
                keptDoors.Add(new CombatDoorRestoration(o.Cell, hold, forbidden));
            }
            else if (o.Kind == OrderKind.AnimalArea && !keptAnimals.Any(a => a.Pawn == o.Pawn))
            {
                if (!combat.Detail.TryGet(o.Pawn.Value, out var p) || p?.AnimalState is not { } animal || !animal.HasSupportsAllowedAreas
                    || animal.Issues.Any(issue => issue.Field == "allowed_area"))
                {
                    continue;
                }
                keptAnimals.Add(new CombatAnimalRestoration(o.Pawn, animal.AllowedAreaId));
            }
            sendable.Add(o);
        }
        if (keptDoors.Count + keptAnimals.Count == 0)
        {
            return sendable;
        }
        await journal.SaveCombatRestorationAsync(kept with { Doors = keptDoors, Animals = keptAnimals }, ct).ConfigureAwait(false);
        return sendable;
    }

    /// <summary>Issues only settings from save-owned restoration intent. A reload starts a fresh cleanup method; historical tactical batches stay dead.</summary>
    internal async Task RestoreCombatSettingsAsync(ControlState state, CombatRestoration kept, CancellationToken ct)
    {
        if (kept.World.Colony != state.Snapshot.Colony || kept.World.Map != state.Snapshot.Map)
        {
            return;
        }
        var journal = _reviewer.Player.Journal;
        var combat = await _native.ReadCombatAsync(Boundary.Identity(state.Snapshot), ct).ConfigureAwait(false);
        var tick = new Tick((long)combat.Context.Tick);
        var incident = await journal.OpenIncidentAsync(new IncidentAssessment { Kind = IncidentKind.ActiveCombat, Trigger = "restore_combat_settings", Snapshot = state.Snapshot, Tick = tick }, ct).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(kept);
        var prefix = Encoding.UTF8.GetBytes($"{kept.Owner.Value}/{state.Snapshot.Load.Value}/{tick.Value}/");
        var sum = SHA256.HashData([.. prefix, .. payload]);
        var id = new PlanId("restore-" + Convert.ToHexStringLower(sum));
        var actions = CombatRestorationActions.Of(kept, id);
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        await journal.CommitCombatRestorationAsync(incident.Incident.Id, plan, ct).ConfigureAwait(false);
        var ids = actions.Select(a => a.Id).ToList();
        IReadOnlyList<DispatchItem> items;
        try
        {
            items = await _hands.RunBatchAsync(id, ids, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The batch may have dispatched part of it; the items report what did.
            RecordCombatDispatch(e is IBatchFailure failure ? failure.Items : []);
            throw;
        }
        RecordCombatDispatch(items);
        if (items.Count != actions.Count)
        {
            throw new ControlException("combat restoration batch incomplete");
        }
        try
        {
            await journal.FinishCombatRestorationAsync(kept.Owner, id, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new CombatRestorationPendingException("combat restoration pending: " + e.Message, e);
        }
    }

    internal async Task RestoreReloadedCombatAsync(ControlState state, CancellationToken ct)
    {
        var journal = _reviewer.Player.Journal;
        var (kept, ok) = await journal.LoadCombatRestorationAsync(ct).ConfigureAwait(false);
        if (!ok)
        {
            return;
        }
        var (fight, found) = await journal.LoadCombatFightAsync(kept.Owner, ct).ConfigureAwait(false);
        if (kept.World.Load == state.Snapshot.Load && found && fight.Open)
        {
            return;
        }
        await RestoreCombatSettingsAsync(state, kept, ct).ConfigureAwait(false);
    }
}
