namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    internal static GenerationSnapshot BudgetWorld(GenerationSnapshot s) => s with { Plan = default, Revision = default };

    internal static IReadOnlyList<PlanId> StandardMethodPlans(IReadOnlyList<Method> history, ulong episode, string prefix) =>
        history.Where(m => m.Episode == episode && m.Method.Value.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m.Plan).ToList();

    internal static IReadOnlyList<PlanId> IncidentPlans(IReadOnlyList<IncidentMethod> methods, string prefix) =>
        methods.Where(m => m.Method.Value.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m.Plan).ToList();

    internal static RefusalBudget RefusalLedger(CancellationToken ct, StoreJournal journal, IReadOnlyList<PlanId> plans, Func<Progress, string> subjectOf)
    {
        var budget = default(RefusalBudget);
        foreach (var id in plans)
        {
            var plan = journal.LoadPlan(ct, id);
            foreach (var progress in plan.Progress)
            {
                var view = progress.View();
                if (!view.Receipt.TryGetValue(out var receipt) || receipt != ReceiptKind.Refused)
                {
                    continue;
                }

                var subject = subjectOf(progress);
                if (subject == "")
                {
                    continue;
                }

                if (!view.Refusal.TryGetValue(out var refusal))
                {
                    refusal = new Refusal { Reason = "refused", Class = RefusalClass.Unknown };
                }

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

        if (why.Code == BudgetCode.Waiting)
        {
            return (RetryBudgetWait(subject, ReasonToken(why.Reason)), false);
        }

        return (Refuse(Cause.RetriesSpent, subject, ReasonToken(why.Reason)), false);
    }

    internal static string ReasonToken(string reason) => string.Join('_', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static (Verdict Verdict, bool Admitted) AdmitSubject(CancellationToken ct, StoreJournal journal, string subject, IReadOnlyList<PlanId> plans, GenerationSnapshot world)
    {
        subject = subject.EndsWith('-') ? subject[..^1] : subject;
        var budget = RefusalLedger(ct, journal, plans, _ => subject);
        return BudgetVerdict(budget, subject, world);
    }

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

    internal static (MethodId Method, Verdict Verdict, bool Admitted) AdmitStandardMethod(CancellationToken ct, StoreJournal journal, StandardState goal, string prefix, GenerationSnapshot world)
    {
        var (verdict, ok) = AdmitStandardSubject(ct, journal, goal, prefix, world);
        if (!ok)
        {
            return (default, verdict, false);
        }

        return (NextMethodId(prefix, HistoryMethodIds(goal.History, goal.Standard.Episode)), default, true);
    }

    internal static (Verdict Verdict, bool Admitted) AdmitStandardSubject(CancellationToken ct, StoreJournal journal, StandardState goal, string prefix, GenerationSnapshot world) =>
        AdmitSubject(ct, journal, prefix, StandardMethodPlans(goal.History, goal.Standard.Episode, prefix), world);

    internal static IReadOnlyList<MethodId> HistoryMethodIds(IReadOnlyList<Method> history, ulong episode) =>
        history.Where(m => m.Episode == episode).Select(m => m.Method).ToList();

    internal static IReadOnlyList<MethodId> IncidentMethodIds(IReadOnlyList<IncidentMethod> methods) =>
        methods.Select(m => m.Method).ToList();
}
