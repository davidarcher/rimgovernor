namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsRefusalBudget
{
    internal static GenerationSnapshot BudgetWorld(GenerationSnapshot s) => s with { Plan = default, Revision = default };

    internal static RefusalBudget RefusalLedger(StoreJournal journal, IReadOnlyList<PlanId> plans, Func<Progress, string> subjectOf, CancellationToken ct)
    {
        var budget = new RefusalBudget();
        foreach (var id in plans)
        {
            var plan = journal.LoadPlan(id, ct);
            foreach (var progress in plan.Progress)
            {
                var view = progress.View;
                if (!view.Received(Domain.Receipt.Refused))
                {
                    continue;
                }

                var subject = subjectOf(progress);
                if (subject == "")
                {
                    continue;
                }

                var refusal =
                    view.Refusal ?? new NativeRefusal("", "refused", RefusalClass.Unclassified);

                budget = budget.Record(subject, refusal.Reason, refusal.Class, BudgetWorld(view.Snapshot));
            }
        }

        return budget;
    }

    internal static (Verdict Verdict, bool Allowed) BudgetVerdict(RefusalBudget budget, string subject, GenerationSnapshot world)
    {
        var (ok, why) = budget.Allowed(subject, BudgetWorld(world));
        if (ok)
        {
            return (default, true);
        }

        if (why.Code == RefusalBudgetRules.BudgetWaiting)
        {
            return (RetryBudgetWait(subject, ReasonToken(why.Reason)), false);
        }

        return (Refuse(Cause.RetriesSpent, subject, ReasonToken(why.Reason)), false);
    }

    internal static string ReasonToken(string reason) => string.Join('_', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static (Verdict Verdict, bool Admitted) AdmitSubject(StoreJournal journal, string subject, IReadOnlyList<PlanId> plans, GenerationSnapshot world, CancellationToken ct)
    {
        subject = subject.EndsWith('-') ? subject[..^1] : subject;
        var budget = RefusalLedger(journal, plans, _ => subject, ct);
        return BudgetVerdict(budget, subject, world);
    }

    /// <summary>
    /// The refusal budget of <paramref name="method"/> over the plan of its latest attempt under <paramref name="owner"/>
    /// (#3310). An earlier attempt's permanent refusal would have kept the MethodId from being admitted again, and its
    /// transient refusal's world is past, so the latest attempt alone carries the budget at any history length.
    /// </summary>
    internal static (Verdict Verdict, bool Admitted) AdmitLatestAttempt(StoreJournal journal, NeedOwner owner, MethodId method, GenerationSnapshot world, CancellationToken ct) =>
        AdmitSubject(journal, method.Value, journal.OwnerMethodPlan(owner, method, ct) is { } latest ? [latest] : [], world, ct);

    internal static MethodId NextMethodId(string prefix, IReadOnlyList<MethodId> taken)
    {
        var used = taken.ToHashSet();
        for (var i = 0; ; i++)
        {
            var id = new MethodId($"{prefix}{i}");
            if (!used.Contains(id))
            {
                return id;
            }
        }
    }

    /// <summary>
    /// The refusal budget of the subject <paramref name="prefix"/> over the latest attempt of every Method of
    /// <paramref name="owner"/> whose MethodId starts with it (the layout/defense Need's tiers and cover clearing, #3314).
    /// </summary>
    internal static (Verdict Verdict, bool Admitted) AdmitOwnerSubject(StoreJournal journal, NeedOwner owner, string prefix, GenerationSnapshot world, CancellationToken ct) =>
        AdmitSubject(journal, prefix, [.. journal.OwnerMethodHistory(owner, ct).Where(m => m.Method.Value.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m.Plan)], world, ct);
}
