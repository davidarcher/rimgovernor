using System.Text.Json;
using RimGovernor.Contracts.Wire;
using Fx = RimGovernor.Fixtures.Contracts;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>ideology/first-ritual</c> (#1665): an explicit Ritual intent carrying the native begin dialog's legal roles and
/// spectators is applied, and the game's ritual lord runs it to its end: the precept's last finished tick advances past the
/// staged baseline. This is mechanics coverage; automatic participant planning stays held unsupported by design.
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
        Scope = "An explicit Ritual intent with the native begin dialog's roles and spectators is applied to a staged free-start ritual "
            + "of the colony ideoligion, and the native ritual lord completes it: the precept's last finished tick advances.",
        Ticks = 30_000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart(),
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        QuietWorld = true,
        Ops = [PrepareTool, InspectTool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("prepare-ritual", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("fixture", ServedOps.Json(prepared));

        var dialog = await s.FixtureAsync("ritual-explicit-assignment", Inspect, prepared.RitualId, cancellationToken).ConfigureAwait(false);
        Operations.RitualIntent intent;
        try
        {
            intent = JsonSerializer.Deserialize(dialog.ExplicitIntent, WireJsonContext.Default.OperationsRitualIntent)
                ?? throw new AcceptanceException("ritual-explicit-assignment: the intent is empty");
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"ritual-explicit-assignment: the native begin dialog supplied no legal explicit assignment: {ServedOps.Json(dialog)}", e);
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var result = await TradeKit.ApplyAsync(s, "explicit-ritual", intent, cancellationToken).ConfigureAwait(false);
        if (TradeKit.Applied("explicit-ritual", result).Ritual?.Ritual != prepared.RitualId)
        {
            throw new AcceptanceException($"explicit-ritual: expected {prepared.RitualId} begun, got {result}");
        }

        // The ritual lord walks the attendees to the building and runs the ritual to its end.
        _ = await s.RunUntilAsync("ritual-finished", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("ritual-inspect", Inspect, prepared.RitualId, ct).ConfigureAwait(false);
            return ($"{read.Running} {read.Participants} {read.LastFinishedTick}", read.LastFinishedTick > prepared.LastFinishedTick);
        }, cancellationToken).ConfigureAwait(false);
    }
}
