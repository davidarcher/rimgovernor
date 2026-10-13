namespace RimGovernor.Brain.BuildingRuntime;

internal sealed class PlacementIndex
{
    public object Mu { get; } = new();

    public bool Seeded { get; set; }

    public Dictionary<string, PlacementEntry> Entries { get; set; } = [];
}

internal readonly record struct PlacementEntry(string Bench, OrderSpec Spec, PlacementAttempt Attempt);

public sealed partial class Rounder
{
    /// <summary>The declarer's short name: its type name without the Rounds prefix and Planner/Declarer suffix.</summary>
    internal static string DeclarerName(IOrderDeclarer d)
    {
        var name = d.GetType().Name;
        if (name.StartsWith("Rounds", StringComparison.Ordinal))
        {
            name = name["Rounds".Length..];
        }

        foreach (var suffix in new[] { "Planner", "Declarer" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
            }
        }

        return name;
    }

    public LedgerView WorkLedgerView()
    {
        LedgerView view;
        GenerationSnapshot world;
        lock (Ledger.Mu)
        {
            view = Ledger.View;
            world = Ledger.World;
        }

        if (view is null || view.Status == "")
        {
            view = LedgerView.New(LedgerViewStatus.None, 0);
        }

        view.Export = Exports.View(world);
        return view;
    }

    internal static (string Key, PlacementEntry Entry) PlacementEntryOf(string plan, ProductionBill bill, PlacementAttempt attempt)
    {
        var spec = new OrderSpec { Recipe = bill.Recipe, Ingredients = bill.Ingredients, Worker = bill.Worker, Mode = bill.Mode, Target = bill.Target };
        attempt = attempt with { Plan = plan };
        return (bill.Bench + "/" + spec.Key(), new PlacementEntry(bill.Bench, spec, attempt));
    }

    internal void NotePlacement(PlanId plan, DomainAction action, ProgressView v)
    {
        if (!action.TryProductionBill(out var bill))
        {
            return;
        }

        var (key, e) = PlacementEntryOf(plan.Value, bill, AttemptOf(v));
        var x = Ledger.Attempts;
        lock (x.Mu)
        {
            x.Entries[key] = e;
        }
    }

    public LedgerView WorkLedger(CancellationToken ct)
    {
        var view = WorkLedgerView();
        Dictionary<string, string>? kinds;
        lock (Ledger.Mu)
        {
            kinds = Ledger.BenchKinds;
        }

        if (view.Orders.Count == 0 || !SeedPlacements(ct))
        {
            return view;
        }

        var attempts = new PlacementAttempts();
        lock (Ledger.Attempts.Mu)
        {
            foreach (var e in Ledger.Attempts.Entries.Values)
            {
                if (kinds is not null && kinds.TryGetValue(e.Bench, out var kind) && kind != "")
                {
                    var spec = e.Spec with { BenchKind = kind };
                    attempts.Note(spec.Key(), e.Attempt);
                }
            }
        }

        return view.WithAttempts(attempts);
    }

    /// <summary>Seeds the placement index once from the owner's recorded ledger plans.</summary>
    internal bool SeedPlacements(CancellationToken ct)
    {
        var x = Ledger.Attempts;
        lock (x.Mu)
        {
            if (x.Seeded)
            {
                return true;
            }
        }

        var journal = Player.Journal;
        StandardState owner;
        try
        {
            var review = journal.LoadRounds(ct);
            (owner, _) = journal.Workable(ct, review, ConcernId.MaintainWorkLedger);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }

        if (owner.Standard.Id.Value == "")
        {
            return false;
        }

        var found = new Dictionary<string, PlacementEntry>();
        foreach (var method in owner.History)
        {
            if (!method.Method.Value.StartsWith(LedgerMethodPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            Plan plan;
            try
            {
                plan = journal.LoadPlan(ct, method.Plan);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return false;
            }

            foreach (var progress in plan.Progress)
            {
                if (progress.Action.TryProductionBill(out var bill))
                {
                    var (key, e) = PlacementEntryOf(method.Plan.Value, bill, AttemptOf(progress.View));
                    if (!found.TryGetValue(key, out var held) || e.Attempt.Tick >= held.Attempt.Tick)
                    {
                        found[key] = e;
                    }
                }
            }
        }

        lock (x.Mu)
        {
            foreach (var (key, e) in found)
            {
                x.Entries.TryAdd(key, e);
            }

            x.Seeded = true;
        }

        return true;
    }

    internal static PlacementAttempt AttemptOf(ProgressView v)
    {
        var a = new PlacementAttempt { Tick = v.Tick, Outcome = AttemptOutcome.Pending };
        if (!v.Receipt.TryGetValue(out var receipt))
        {
            return a;
        }

        switch (receipt)
        {
            case Receipt.Accepted:
                a = a with { Outcome = AttemptOutcome.Accepted };
                break;
            case Receipt.Refused:
                a = a with { Outcome = AttemptOutcome.Refused };
                if (v.Refusal.TryGetValue(out var refusal))
                {
                    a = a with { Code = refusal.Code, Reason = refusal.Reason };
                }

                break;
            default:
                a = a with { Outcome = AttemptOutcome.Unconfirmed };
                break;
        }

        return a;
    }
}
