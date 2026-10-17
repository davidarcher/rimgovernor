using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>coordination/*</c>: a specialist and helpers the served brain coordinates through Hands. The native game harvests
/// food to the selected storage, or supplies material and finishes a building, with tasks spread over more than one
/// pawn, then releases every participant; the carried-recovery case also ends one carrying delivery's job and needs a
/// later revision to carry it again. test/coordinated_work_prepare prepares the facts (no plan, order or task) and
/// records what the native activities did, which the primitives cannot express.
/// </summary>
internal static class CoordinatedWork
{
    private const string Prepare = "test/coordinated_work_prepare";
    private const string ReadTool = "test/coordinated_work_read";

    private static readonly FixtureOp<(string Mode, bool Interrupt), Fx.FixtureDone> Prepared = new(
        Prepare,
        r => ServedOps.Args(("mode", r.Mode), ("interrupt", r.Interrupt)),
        ServedOps.Done(Prepare));

    private static readonly FixtureOp<NoArgs, Fx.CoordinationRead> Read = ServedOps.Read<Fx.CoordinationRead>(ReadTool);

    internal static TypedCase Case(string mode, bool interrupted = false) => new()
    {
        Name = "coordination/" + mode + (interrupted ? "-carried-recovery" : "-delegated"),
        Scope = "Prepared specialist and helper facts are coordinated by the served brain through Hands: the native game "
            + (mode == "food" ? "harvests food to the selected storage" : "supplies material and finishes a building")
            + " with tasks on more than one pawn, then releases every participant"
            + (interrupted ? "; one carried input's job is ended and a later revision carries it again" : "")
            + ". A unit test cannot prove native carry conservation, blueprint and frame rebinding or actual completion.",
        Ticks = 6000,
        Tier = CaseTier.Nightly,
        Ops = [Prepare, ReadTool],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "coordination" },
        Run = (s, ct) => RunAsync(s, mode, interrupted, ct),
    };

    private static async Task RunAsync(ITypedSession s, string mode, bool interrupted, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("coordination-prepare", Prepared, (mode, interrupted), cancellationToken).ConfigureAwait(false);
        _ = await s.ServeUntilAsync("coordination", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("coordination-observe", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("coordination", ServedOps.Json(read));
            if (read.Forced)
            {
                throw new AcceptanceException($"planner work became player-forced: {ServedOps.Json(read)}");
            }

            var chain = mode == "food"
                ? read.FinishedKinds.Contains("Work:Harvest") && read.FinishedKinds.Contains("Deliver:") && read.PhysicalFood > 0
                : read.FinishedKinds.Contains("Supply:") && read.FinishedKinds.Contains("Work:FinishFrame") && read.PhysicalBuildings > 0;
            var done = read.Joint && chain && read.Delivered > 0 && read.Active == 0 && read.Participants.Count > 0
                && read.Released.Count == read.Participants.Count && (!interrupted || read.Interrupted && read.Recovered);
            return (string.Create(CultureInfo.InvariantCulture, $"{read.Tick}"), done);
        }, cancellationToken).ConfigureAwait(false);
    }
}
