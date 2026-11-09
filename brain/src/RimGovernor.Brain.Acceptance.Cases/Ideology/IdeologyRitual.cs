using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>ideology/first-ritual</c> (#1665, rewritten in #4059): a staged due free-start ritual of the colony ideoligion with its
/// finished required building is priced by the served normal review as a coupled ritual root, admitted as the
/// <c>ideology/rituals</c> Need's Method and begun through the Ritual begin order; the game's ritual lord runs it to its end:
/// the precept's last finished tick advances past the staged baseline. No explicit intent is applied.
/// </summary>
internal static class IdeologyRitual
{
    private const string PrepareTool = "test/first_ritual_prepare";
    private const string InspectTool = "test/first_ritual_inspect";

    private static readonly FixtureOp<NoArgs, Fx.FirstRitualPrepared> Prepare = new(
        PrepareTool,
        _ => [],
        ServedOps.Succeeded<Fx.FirstRitualPrepared>(PrepareTool, r => r.Success && r.RitualId.Length > 0 && r.BuildingId.Length > 0 && r.PawnIds.Count >= 2));

    private static readonly FixtureOp<string, Fx.FirstRitualRead> Inspect = new(
        InspectTool,
        ritual => ServedOps.Args(("ritualId", ritual)),
        ServedOps.Succeeded<Fx.FirstRitualRead>(InspectTool, r => r.Success));

    internal static TypedCase Case() => new()
    {
        Name = "ideology/first-ritual",
        Scope = "A staged due free-start ritual of the colony ideoligion with its finished required building: the served normal review "
            + "prices it as a coupled ritual root, admits the Method on ideology/rituals and issues the Ritual begin, and the native "
            + "ritual lord completes it: the precept's last finished tick advances. A unit test cannot prove the native begin or the lord.",
        Ticks = 30_000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart(),
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        QuietWorld = true,
        Ops = [PrepareTool, InspectTool],
        Serve = new ServeSpec { Prefix = "first-ritual" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("prepare-ritual", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("fixture", ServedOps.Json(prepared));

        // The served review admits the begin and the lord walks the attendees to the building and runs the ritual to its end.
        var begun = false;
        _ = await s.ServeUntilAsync("ritual-finished", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("ritual-inspect", Inspect, prepared.RitualId, ct).ConfigureAwait(false);
            begun |= read.Running;
            return (string.Create(CultureInfo.InvariantCulture, $"running={read.Running} participants={read.Participants} finished={read.LastFinishedTick}"),
                read.LastFinishedTick > prepared.LastFinishedTick);
        }, cancellationToken).ConfigureAwait(false);
        var final = await s.FixtureAsync("ritual-final", Inspect, prepared.RitualId, cancellationToken).ConfigureAwait(false);
        s.Record("ritual", ServedOps.Json(final));
        if (!begun && final.LastFinishedTick <= prepared.LastFinishedTick)
        {
            throw new AcceptanceException($"the served review never began ritual {prepared.RitualId}: {ServedOps.Json(final)}");
        }
    }
}
