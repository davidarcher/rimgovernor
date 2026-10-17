using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class DevelopmentReassignmentCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "production/development-reassignment",
        Scope = "Prepared useful cooking development is chosen by the normal planner. Once it has produced work, the Cooking assignment is withdrawn from the colonist it owns, as a player changing work priorities would. The Method must not keep that colonist owned while it cannot do the work (it is released, or the Governor restored the assignment), and must still retire once the demand is withdrawn with nobody left owned. Unit tests prove lost-participant handling; this case proves it through native jobs.",
        Start = new FixtureStart { Op = "test/productive_cooking_prepare", On = new LabStart() },
        RequiredOps = ["test/productive_cooking_prepare", "test/productive_cooking_read", "test/productive_cooking_reassign", "test/productive_cooking_satisfy"],
        Keep = ["Food", "Rest"], QuietWorld = true, Crew = new Crew { Size = 3, Ordinary = true }, Service = true,
        Reason = "Superpawns at skill 20 would make the prepared level-5 cooking skill and its learning unobservable.",
        NoCheckpoint = true, Budget = TimeSpan.FromMinutes(3), Stall = TimeSpan.FromSeconds(45),
        Run = async (s, ct) =>
        {
            var run = await BrainRun.StartAsync(s, null, "development-reassignment", "development-reassignment", ct).ConfigureAwait(false);
            try
            {
                using var store = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), ct).ConfigureAwait(false);
                var journaled = new HashSet<string>(StringComparer.Ordinal);
                var reassigned = false; var settled = false; var satisfied = false; double staleSince = -1;
                await new Wait { Stall = TimeSpan.FromSeconds(45), Terminal = run.Service.Exited }.WaitProgressAsync(async token =>
                {
                    var read = await s.Harness.CallAsync("reassignment-observe", "test/productive_cooking_read", [], token).ConfigureAwait(false);
                    foreach (var method in await ServiceJournal.NeedMethodsAsync(store, Policy.AdminFamily.WorkAssignments, token).ConfigureAwait(false))
                        journaled.Add(method);
                    var methods = read["methods"]!.AsArray().Select(n => n.AsString()).ToArray();
                    var tick = read["tick"].AsNumber();
                    s.Report["observed"] = read;
                    if (read["learningFailures"]!.AsArray().Count != 0) throw new AcceptanceException("Native learning differential failed: " + read.ToJson());
                    if (!reassigned && methods.Length > 0 && methods.All(journaled.Contains) && read["completions"].AsNumber() >= 1)
                    {
                        s.Report["reassign"] = await s.Harness.CallAsync("reassignment-change", "test/productive_cooking_reassign", [], token).ConfigureAwait(false);
                        reassigned = true;
                        return (Wait.Signature(tick), false);
                    }
                    if (reassigned && !settled)
                    {
                        var cannotCook = read["reassignedPriority"].AsNumber() == 0;
                        var owned = read["reassignedOwned"].Flag();
                        // A task already handed out may finish; ownership of a colonist who cannot do the work must not persist.
                        if (cannotCook && owned && !read["reassignedCooking"].Flag())
                        {
                            if (staleSince < 0) staleSince = tick;
                            if (tick - staleSince >= 1500) throw new AcceptanceException("The Method kept a colonist owned after the Cooking assignment was withdrawn: " + read.ToJson());
                        }
                        else staleSince = -1;
                        settled = !cannotCook || !owned;
                        s.Report["settled"] = settled;
                    }
                    if (settled && !satisfied) { s.Report["satisfy"] = await s.Harness.CallAsync("reassignment-satisfy", "test/productive_cooking_satisfy", [], token).ConfigureAwait(false); satisfied = true; }
                    var retired = satisfied && !read["billActive"].Flag() && read["cooking"].AsNumber() == 0 && read["owned"].AsNumber() == 0;
                    s.Report["retired"] = retired;
                    if (!retired && reassigned && tick - read["reassignTick"].AsNumber() >= 6000)
                        throw new AcceptanceException("Development did not settle and retire within 6000 ticks of the reassignment: " + read.ToJson());
                    return (Wait.Signature(tick), retired);
                }, ct).ConfigureAwait(false);
            }
            finally { await run.StopAsync(ct).ConfigureAwait(false); }
        }
    };
}
