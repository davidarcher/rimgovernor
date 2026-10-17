using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class DevelopmentLifecycleCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "production/development-lifecycle",
        Scope = "Prepared useful cooking development (no player goal, no selected worker) is chosen by the normal planner and journaled. Once it has produced work, every colonist is made critically tired: the urgent rest obligation must postpone further development and the pawns must sleep. Recovery is then injected in place of hours of natural rest, development must resume, and the Method must retire once the demand is withdrawn. Unit tests prove the ordering; this case proves the postpone, resume and retire sequence through native jobs.",
        Start = new FixtureStart { Op = "test/productive_cooking_prepare", On = new LabStart() },
        RequiredOps = ["test/productive_cooking_prepare", "test/productive_cooking_read", "test/productive_cooking_urge", "test/productive_cooking_recover", "test/productive_cooking_satisfy"],
        Keep = ["Food", "Rest"], QuietWorld = true, Crew = new Crew { Size = 3, Ordinary = true }, Service = true,
        Reason = "Superpawns at skill 20 would make the prepared level-5 cooking skill and its learning unobservable.",
        NoCheckpoint = true, Budget = TimeSpan.FromMinutes(3), Stall = TimeSpan.FromSeconds(45),
        Run = async (s, ct) =>
        {
            var run = await BrainRun.StartAsync(s, null, "development-lifecycle", "development-lifecycle", ct).ConfigureAwait(false);
            try
            {
                using var store = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), ct).ConfigureAwait(false);
                var journaled = new HashSet<string>(StringComparer.Ordinal);
                var urged = false; var recovered = false; var satisfied = false; var woke = false; var resumed = false;
                await new Wait { Stall = TimeSpan.FromSeconds(45), Terminal = run.Service.Exited }.WaitProgressAsync(async token =>
                {
                    var read = await s.Harness.CallAsync("lifecycle-observe", "test/productive_cooking_read", [], token).ConfigureAwait(false);
                    foreach (var method in await ServiceJournal.NeedMethodsAsync(store, Policy.AdminFamily.WorkAssignments, token).ConfigureAwait(false))
                        journaled.Add(method);
                    var methods = read["methods"]!.AsArray().Select(n => n.AsString()).ToArray();
                    var completions = read["completions"].AsNumber();
                    s.Report["observed"] = read;
                    if (read["learningFailures"]!.AsArray().Count != 0) throw new AcceptanceException("Native learning differential failed: " + read.ToJson());
                    var chosen = methods.Length > 0 && methods.All(journaled.Contains);
                    if (!urged && chosen && completions >= 1)
                    {
                        s.Report["urge"] = await s.Harness.CallAsync("lifecycle-urge", "test/productive_cooking_urge", [], token).ConfigureAwait(false);
                        urged = true;
                        return (Wait.Signature(read["tick"].AsNumber()), false);
                    }
                    // A task already handed out when the obligation arrived may finish; no further cooking job may begin while the pawn is critically tired.
                    if (urged && read["tiredCooks"].AsNumber() > read["committedAtUrge"].AsNumber())
                        throw new AcceptanceException($"Development was not postponed under the urgent rest obligation: {read["tiredCooks"].AsNumber()} cooking jobs began while critically tired, {read["committedAtUrge"].AsNumber()} tasks were already handed out: " + read.ToJson());
                    woke = urged && read["slept"].Flag();
                    // Natural recovery from critical fatigue is hours of game time; sleep has been observed, so stand in for it.
                    if (woke && !recovered) { s.Report["recover"] = await s.Harness.CallAsync("lifecycle-recover", "test/productive_cooking_recover", [], token).ConfigureAwait(false); recovered = true; }
                    resumed = woke && read["restedCooksAfterUrge"].AsNumber() > 0;
                    // Resumed development is observed; the colony then no longer wants meals, and the Method must retire with no pawn left owned.
                    if (resumed && !satisfied) { s.Report["satisfy"] = await s.Harness.CallAsync("lifecycle-satisfy", "test/productive_cooking_satisfy", [], token).ConfigureAwait(false); satisfied = true; }
                    var retired = satisfied && !read["billActive"].Flag() && read["cooking"].AsNumber() == 0 && read["owned"].AsNumber() == 0;
                    s.Report["postponed"] = woke; s.Report["resumed"] = resumed; s.Report["retired"] = retired;
                    if (!retired && urged && read["tick"].AsNumber() - read["urgeTick"].AsNumber() >= 15000)
                        throw new AcceptanceException("Development did not postpone, resume and retire within 15000 ticks of the urgent obligation: " + read.ToJson());
                    return (Wait.Signature(read["tick"].AsNumber()), retired);
                }, ct).ConfigureAwait(false);
            }
            finally { await run.StopAsync(ct).ConfigureAwait(false); }
        }
    };
}
