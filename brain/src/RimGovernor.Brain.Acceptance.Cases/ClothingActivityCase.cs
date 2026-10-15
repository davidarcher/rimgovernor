using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class ClothingActivityCase
{
    internal static AcceptanceCase Case(bool interrupted = false) => new()
    {
        Name = "production/clothing-activity" + (interrupted ? "-unfinished" : ""),
        Scope = "Prepared clothing deficit enters normal RoundsGear, shared comparison, Method journal and Hands without fixture-selected bill or material. Observe selected-stuff garment creation and exact input conservation, participant release, and optionally resumption of observed unfinished work. The interrupted case supplies only one native-minimum garment quantity of one material; the normal case retains competing materials and split stacks. Unit tests cannot prove native recipe conversion or unfinished-item conservation.",
        Start = new FixtureStart { Op = "test/clothing_activity_prepare", On = new LabStart(), Args = new JsonObject { ["interrupt"] = interrupted } },
        RequiredOps = ["test/clothing_activity_prepare", "test/clothing_activity_read"], QuietWorld = true,
        Crew = new Crew { Size = 3 }, Service = true, NoCheckpoint = true, Budget = TimeSpan.FromMinutes(5), Stall = TimeSpan.FromSeconds(45),
        Run = async (s, ct) =>
        {
            var run = await BrainRun.StartAsync(s, null, "clothing", "clothing", ct).ConfigureAwait(false);
            try
            {
                var wait = new Wait { Stall = TimeSpan.FromSeconds(45), Terminal = run.Service.Exited };
                await wait.WaitProgressAsync(async token =>
                {
                    var read = await s.Harness.CallAsync("clothing-observe", "test/clothing_activity_read", [], token).ConfigureAwait(false);
                    s.Report["clothing"] = read;
                    if (read["produced"].Flag() && !read["conserved"].Flag()) throw new AcceptanceException("Recipe input/product conservation failed: " + read.ToJson());
                    var complete = read["produced"].Flag() && read["conserved"].Flag() && read["released"].AsNumber() > 0
                        && (!interrupted || read["interrupted"].Flag() && read["resumed"].AsNumber() > 0);
                    if (!complete && read["elapsed"].AsNumber() >= 12000) throw new AcceptanceException("Normal clothing planner did not complete within 12000 ticks: " + read.ToJson());
                    return (Wait.Signature(read["tick"].AsNumber()), complete);
                }, ct).ConfigureAwait(false);
            }
            finally { await run.StopAsync(ct).ConfigureAwait(false); }
        }
    };
}
