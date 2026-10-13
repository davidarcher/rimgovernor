using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>One cell a live plan's wall removal names and whether a colony Wall still stood on it in the construction census.</summary>
public readonly record struct WallCell(Cell Cell, bool Standing = false);

public sealed partial class Store
{
    /// <summary>Bounds <c>Rounds.WallCells</c>: one stone-shell bundle names at most four removal cells, and few bundles are ever live at once.</summary>
    internal const int MaxWallCells = 256;

    /// <summary>
    /// Every admission's prerequisite gate: <see cref="PlanSpec.CheckDependencies"/>, then the last rounds's census
    /// for each prerequisite whose receipt does not mean the work is done. An accepted building intent completes
    /// once its blueprint is placed, so a dependent (a bed on a floor) waits for the building to stand built
    /// (<c>Rounds.Built</c>). An accepted wall removal completes once its demolition is designated, so a dependent
    /// (the replacement wall on the same cell) waits for the census to show no wall left in that cell
    /// (<c>Rounds.WallCells</c>).
    /// </summary>
    internal static void CheckDependencies(StoreTransaction tx, PlanState state, ActionId action, GenerationSnapshot current, Tick tick)
    {
        state.Spec.CheckDependencies(action, state.Progress, current, tick);
        var actions = state.Spec.Actions.ToDictionary(a => a.Id);
        var built = new List<ActionId>();
        var cleared = new List<Cell>();
        foreach (var d in state.Spec.Dependencies)
        {
            if (d.Action != action)
            {
                continue;
            }
            var required = actions[d.Requires];
            if (required.Kind == ActionKind.Building)
            {
                built.Add(d.Requires);
            }
            if (required.WallRemoval is { } removal)
            {
                cleared.Add(removal.Cell);
            }
        }
        if (built.Count == 0 && cleared.Count == 0)
        {
            return;
        }
        var review = LoadRounds(tx);
        var s = review.Snapshot;
        if (s.Colony != current.Colony || s.Load != current.Load || s.Map != current.Map)
        {
            throw new DependencyException();
        }
        if (built.Any(r => !review.Built.Contains(r)))
        {
            throw new DependencyException();
        }
        // A cell the review never censused (the plan is newer than it) is not known clear.
        if (cleared.Any(c => !review.WallCells.Contains(new WallCell(c))))
        {
            throw new DependencyException();
        }
    }

    /// <summary>
    /// Answers, from a complete colony construction census, whether a colony Wall stands on each cell a wall
    /// removal of a live plan names.
    /// </summary>
    internal static List<WallCell> WallCellsIn(StoreTransaction tx, CurrentConstruction census)
    {
        var named = new HashSet<Cell>();
        foreach (var body in tx.Query("SELECT a.body FROM actions a JOIN plans p ON p.id=a.plan_id WHERE a.kind='wall_removal' AND p.retired=0", [], r => r.GetString(0)))
        {
            if (StoreJson.Deserialize<DomainAction>(body).WallRemoval is not { } removal)
            {
                throw new StoreCorruptException("invalid wall removal payload");
            }
            _ = named.Add(removal.Cell);
        }
        if (named.Count > MaxWallCells)
        {
            throw new CapacityException();
        }
        var standing = new HashSet<Cell>();
        foreach (var b in census.Buildings)
        {
            if (b.Building?.Definition != "Wall")
            {
                continue;
            }
            foreach (var c in b.Cells)
            {
                _ = standing.Add(c);
            }
        }
        return [.. named.OrderBy(c => c.X).ThenBy(c => c.Z).Select(c => new WallCell(c, standing.Contains(c)))];
    }

    /// <summary>
    /// The census-side give-up for a wall removal native never carries out: once a completed removal's wall
    /// still stands in the census more than a day after its dispatch, the removal and every action waiting on it
    /// are cancelled, so the plan settles and its goal re-plans instead of <see cref="CheckDependencies"/>
    /// holding the replacement forever.
    /// </summary>
    internal static void AbandonStuckWallRemovals(StoreTransaction tx, IReadOnlyList<WallCell> cells, Tick tick)
    {
        var standing = cells.Where(c => c.Standing).Select(c => c.Cell).ToHashSet();
        if (standing.Count == 0)
        {
            return;
        }
        var plans = tx.Query("SELECT DISTINCT a.plan_id FROM actions a JOIN plans p ON p.id=a.plan_id WHERE a.kind='wall_removal' AND p.retired=0", [], r => new PlanId(r.GetString(0)));
        foreach (var plan in plans)
        {
            var state = LoadIn(tx, plan);
            var withdraw = new HashSet<ActionId>();
            foreach (var p in state.Progress)
            {
                var v = p.View;
                if (p.Action.WallRemoval is { } removal && v.Stage == Stage.Completed && standing.Contains(removal.Cell) && tick.Value - v.Tick.Value > GameTime.TicksPerDay)
                {
                    _ = withdraw.Add(v.Action);
                }
            }
            if (withdraw.Count == 0)
            {
                continue;
            }
            foreach (var d in state.Spec.Dependencies)
            {
                if (withdraw.Contains(d.Requires))
                {
                    _ = withdraw.Add(d.Action);
                }
            }
            foreach (var p in state.Progress)
            {
                var v = p.View;
                if (!withdraw.Contains(v.Action) || v.Stage is Stage.Cancelled or Stage.Unsuccessful || (v.Stage == Stage.Completed && !p.Action.Kind.IntentMode()))
                {
                    continue;
                }
                _ = AdvanceIn(tx, plan, v.Action, new Transition(TransitionKind.Cancel));
            }
        }
    }

    /// <summary>
    /// Every live plan's building action whose building stands built in the census, matched by definition, stuff,
    /// anchor and rotation, sorted.
    /// </summary>
    internal static List<ActionId> BuiltActions(StoreTransaction tx, CurrentConstruction census)
    {
        var built = new List<ActionId>();
        foreach (var body in tx.Query("SELECT a.body FROM actions a JOIN plans p ON p.id=a.plan_id WHERE a.kind='building' AND p.retired=0", [], r => r.GetString(0)))
        {
            var action = StoreJson.Deserialize<DomainAction>(body);
            if (action.Building is { } b && census.Built(b) is not null)
            {
                built.Add(action.Id);
            }
        }
        built.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return built;
    }
}
