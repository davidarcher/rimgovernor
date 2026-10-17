using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/maintenance-range</c> and <c>-sparring</c>: an assigned Ideology combat role below its native skill
/// floor, practiced under the served brain. The holder's skill recovers through native practice; a draft during the
/// gear swap restores the holder's own weapon; and once the role is removed the practice is released.
/// test/maintenance_prepare prepares the role, its deficit and the facilities (no activity is selected), and
/// test/maintenance_disturb rearms the deficit with a draft or removes the role, which the primitives cannot express.
/// </summary>
internal static class ProductionMaintenancePractice
{
    private const string Prepare = "test/maintenance_prepare";
    private const string ReadTool = "test/maintenance_read";
    private const string DisturbTool = "test/maintenance_disturb";

    // How long the released holder must stay off the practice job.
    private const int ReleasedTicks = 500;

    private static readonly FixtureOp<bool, Fx.FixtureDone> Prepared = new(Prepare, melee => ServedOps.Args(("melee", melee)), ServedOps.Done(Prepare));
    private static readonly FixtureOp<string, Fx.FixtureDone> Disturb = new(DisturbTool, action => ServedOps.Args(("action", action)), ServedOps.Done(DisturbTool));
    private static readonly FixtureOp<NoArgs, Fx.MaintenanceRead> Read = ServedOps.Read<Fx.MaintenanceRead>(ReadTool);

    internal static TypedCase Case(bool melee) => new()
    {
        Name = "production/maintenance-" + (melee ? "sparring" : "range"),
        Scope = "An assigned Ideology combat role below its native floor is practiced under the served brain: the holder's "
            + "skill recovers through native practice, a draft during the gear swap restores its original weapon, and removing "
            + "the role releases the practice. Superpawns would lift the holder above the floor, so the crew is ordinary. A unit "
            + "test cannot prove native practice, gear restoration or the competing-writer cutover.",
        Ticks = 18000,
        Tier = CaseTier.OnDemand,
        Ops = [Prepare, ReadTool, DisturbTool],
        Crew = new Crew { Size = 3, Ordinary = true },
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "maintenance" },
        Run = (s, ct) => RunAsync(s, melee, ct),
    };

    private static async Task RunAsync(TypedSession s, bool melee, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("maintenance-prepare", Prepared, melee, cancellationToken).ConfigureAwait(false);
        var stage = 0;
        var releasedAt = 0;
        _ = await s.ServeUntilAsync("maintenance", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("maintenance-observe", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("maintenance", ServedOps.Json(read));
            if (stage == 0 && read.Methods.Count > 0 && read.Earned && read.Activity != "running")
            {
                _ = await s.FixtureAsync("maintenance-interrupt", Disturb, "interrupt", ct).ConfigureAwait(false);
                stage = 1;
            }
            else if (stage == 1 && read.Drafted && read.Restored)
            {
                _ = await s.FixtureAsync("maintenance-release", Disturb, "release", ct).ConfigureAwait(false);
                stage = 2;
                releasedAt = read.Tick;
            }

            var done = stage == 2 && read.Released && read.Restored && read.Tick >= releasedAt + ReleasedTicks
                && read.Job is not ("RimGovernor_Spar" or "RimGovernor_TrainShooting");
            return (string.Create(CultureInfo.InvariantCulture, $"{read.Tick} stage={stage}"), done);
        }, cancellationToken).ConfigureAwait(false);
    }
}
