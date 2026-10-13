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
        if (!orders.Any(o => o.Kind is CombatOrderKind.Door or CombatOrderKind.AnimalArea))
        {
            return orders;
        }
        var journal = _reviewer.Player.Journal;
        var loaded = journal.LoadCombatRestoration(ct);
        var exists = loaded is not null;
        var world = new World(state.Snapshot.Colony, state.Snapshot.Load, state.Snapshot.Map);
        if (loaded is not null && (loaded.Owner != fight || loaded.World != world))
        {
            throw new ControlException("prior combat restoration pending");
        }
        var kept = loaded ?? new CombatRestoration(world, fight, [], []);
        var combat = await _native.ReadCombatAsync(Boundary.Identity(state.Snapshot), ct).ConfigureAwait(false);
        var known = combat.DoorStates.TryGetValue(out var doors);
        var sendable = new List<CombatOrder>(orders.Count);
        var keptDoors = kept.Doors.ToList();
        var keptAnimals = kept.Animals.ToList();
        foreach (var o in orders)
        {
            if (o.Kind == CombatOrderKind.Door && !keptDoors.Any(d => d.Cell == o.Cell))
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
            else if (o.Kind == CombatOrderKind.AnimalArea && !keptAnimals.Any(a => a.Pawn == (Domain.PawnId)o.Pawn))
            {
                if (!combat.Detail.TryGetValue(o.Pawn.Value, out var p) || p.AnimalState is not { } animal || animal.SupportsAllowedAreas is null
                    || animal.Issues.Any(issue => (issue.Field ?? "") == "allowed_area"))
                {
                    continue;
                }
                keptAnimals.Add(new CombatAnimalRestoration((Domain.PawnId)o.Pawn, (animal.AllowedAreaId ?? "")));
            }
            sendable.Add(o);
        }
        if (keptDoors.Count + keptAnimals.Count == 0)
        {
            return sendable;
        }
        journal.SaveCombatRestoration(kept with { Doors = [.. keptDoors], Animals = [.. keptAnimals] }, ct);
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
        var tick = new Tick((long)(combat.Context?.Tick ?? 0));
        var incident = await journal.OpenIncidentAsync(new IncidentAssessment(PolicyApi.ActiveCombat, default, "restore_combat_settings", 0, state.Snapshot, tick, []), ct).ConfigureAwait(false);
        var payload = Encoding.UTF8.GetBytes(global::RimGovernor.Brain.Store.Codec.StoreJson.Serialize(kept));
        var prefix = Encoding.UTF8.GetBytes($"{kept.Owner.Value}/{state.Snapshot.Load.Value}/{tick.Value}/");
        var sum = SHA256.HashData([.. prefix, .. payload]);
        var id = new PlanId("restore-" + Convert.ToHexStringLower(sum));
        var actions = JournalStore.CombatRestorationActions(kept, id);
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        journal.CommitCombatRestoration(incident.Incident.Id, plan, ct);
        var ids = actions.Select(a => a.Id).ToList();
        IReadOnlyList<BatchItem> items;
        try
        {
            items = _hands.RunBatch(id, ids, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The batch may have dispatched part of it; the items report what did.
            RecordCombatDispatch(e is BatchFailureException failure ? failure.Items : [], ct);
            throw;
        }
        RecordCombatDispatch(items, ct);
        if (items.Count != actions.Count)
        {
            throw new ControlException("combat restoration batch incomplete");
        }
        try
        {
            journal.FinishCombatRestoration(kept.Owner, id, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new CombatRestorationPendingException("combat restoration pending: " + e.Message, e);
        }
    }

    internal async Task RestoreReloadedCombatAsync(ControlState state, CancellationToken ct)
    {
        var journal = _reviewer.Player.Journal;
        var kept = journal.LoadCombatRestoration(ct);
        if (kept is null)
        {
            return;
        }
        var fight = journal.LoadCombatFight(kept.Owner, ct);
        if (kept.World.Load == state.Snapshot.Load && fight is { Open: true })
        {
            return;
        }
        await RestoreCombatSettingsAsync(state, kept, ct).ConfigureAwait(false);
    }
}
