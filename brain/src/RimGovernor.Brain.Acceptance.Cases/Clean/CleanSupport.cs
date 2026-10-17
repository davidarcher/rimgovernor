using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>clean/*</c>: support cleaning under the served colony. test/support_&lt;scenario&gt;_prepare stages a dirty room
/// beside a bill or a patient and installs no chosen work; test/support_read reads the physical outcome. Both stay
/// bespoke: the stage primitives cannot dirty a room, wound a patient or count participant occupancy.
/// </summary>
internal static class CleanSupport
{
    private const string ReadTool = "test/support_read";

    // The fixture's prepared service window: an outcome not reached in it fails.
    private const int Window = 12000;

    private static readonly FixtureOp<NoArgs, Fx.SupportRead> Read = ServedOps.Read<Fx.SupportRead>(ReadTool);

    /// <summary>One scenario: its case name, the fixture's scenario key, the claim and the physical outcome it needs.</summary>
    private sealed record Scenario(string Name, string Key, string Claim, Func<Fx.SupportRead, bool> Outcome);

    private static readonly Scenario[] Scenarios =
    [
        new(
            "clean/bench",
            "bench",
            "A dirty cooking station and a hungry consumer under the served colony: the room gets cleaner and the bill produces, by "
                + "self service or helper work, without a fixed cleaning quota.",
            read => read.Cleaned && read.Produced),
        new(
            "clean/care",
            "care",
            "A bleeding patient in a dirty care room under the served colony: the patient is tended within the prepared 2500-tick "
                + "service budget (not a prognosis) and the room gets cleaner, without cleaning first or a tend finalizer. Critical "
                + "care's ownership of the tend is MedicalFamilyTests'.",
            read => read.Tended && read.CareWithinServiceBudget && read.Cleaned),
        new(
            "clean/care-routine",
            "routine",
            "A nonbleeding tendable injury beside a needed food station under the served colony: the patient is tended within the "
                + "prepared service budget and the room gets cleaner, without an automatic finalizer.",
            read => read.Tended && read.CareWithinServiceBudget && read.Cleaned),
    ];

    internal static IEnumerable<TypedCase> Cases() => Scenarios.Select(Case);

    private static TypedCase Case(Scenario scenario)
    {
        var prepare = "test/support_" + scenario.Key + "_prepare";
        var prepared = ServedOps.Inject(prepare);
        return new()
        {
            Name = scenario.Name,
            Scope = scenario.Claim + " Every participant's occupancy is counted. Native physical completion and occupancy no "
                + "forecast test can establish.",
            Ticks = Window,
            Tier = CaseTier.OnDemand,
            Ops = [prepare, ReadTool],
            Keep = ["Food"],
            QuietWorld = true,
            Serve = new ServeSpec { Prefix = "support-" + scenario.Key },
            Run = (s, ct) => RunAsync(s, scenario, prepared, ct),
        };
    }

    private static async Task RunAsync(TypedSession s, Scenario scenario, FixtureOp<NoArgs, Fx.FixtureDone> prepare, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("support-prepare", prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        Fx.SupportRead last = new();
        _ = await s.ServeUntilAsync("support-outcome", s.TicksLeft, async ct =>
        {
            last = await s.FixtureAsync("support-read", Read, NoArgs.Value, ct).ConfigureAwait(false);
            if (!last.Success)
            {
                throw new AcceptanceException("support-read: the prepared support fixture is no longer active");
            }

            var done = scenario.Outcome(last) && last.ParticipantAccounting;
            return (Wait.Signature(string.Create(CultureInfo.InvariantCulture, $"cleaned={last.Cleaned} produced={last.Produced} tended={last.Tended} accounted={last.ParticipantAccounting}")), done);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("support", ServedOps.Json(last));
    }
}
