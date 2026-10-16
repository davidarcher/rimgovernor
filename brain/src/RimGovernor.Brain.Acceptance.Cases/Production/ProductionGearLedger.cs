namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/gear-ledger</c> (Go <c>gear_ledger.go</c>): The military/equipment Need and the armory declare their gear batches to
/// the work ledger (#2597): the lab colony's missing apparel or weapons become a gear_batch bill the ledger planner places
/// under a ledger-* method, and the journal records it completed.
/// </summary>
internal static class ProductionGearLedger
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "production/gear-ledger",
        Scope = "The military/equipment Need and the armory declare their gear batches to the work ledger (#2597): the lab colony's "
            + "missing apparel or weapons become a gear_batch bill the ledger planner places under a ledger-* method, and the "
            + "journal records it completed. Nightly tier: the bill finishing and an undeclared bill being "
            + "removed after policy.OrphanGraceRounds are pinned by the buildingruntime ledger tests.",
        Start = new LabStart(),
        Serve = new ServeSpec { Families = ["gear", "armory", "bill"] },
        Budget = TimeSpan.FromMinutes(6),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var service = await ObservedSession.Of(s).ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            var journal = await service.StoreAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = new Wait { Ceiling = TimeSpan.FromMinutes(5), Stall = TimeSpan.FromMinutes(2), Interval = TimeSpan.FromSeconds(1), Terminal = service.Exited };
                await wait.WaitProgressAsync(
                    async ct =>
                    {
                        var plans = await journal.PlanHistoryAsync(256, "ledger-*", ct).ConfigureAwait(false);
                        foreach (var plan in plans)
                        {
                            foreach (var progress in plan["progress"].AsSlice())
                            {
                                var row = progress.AsMap() ?? [];
                                if (row["kind"].AsString() == "production_bill" && row["mode"].AsString() == "gear_batch" && row["stage"].AsString() == "completed")
                                {
                                    return ("gear batch placed by the ledger", true);
                                }
                            }
                        }

                        return ("waiting for the ledger to place a gear batch", false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                (journal as IDisposable)?.Dispose();
            }
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }
    }
}
