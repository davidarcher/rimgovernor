using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>ideology/convert-colonist</c> (#3501): a colony whose moral guide holds the Convert ability orders it on a colonist of
/// another ideoligion through Actions/Apply; the game's cast job runs and the target's certainty drops or its ideoligion flips.
/// </summary>
internal static class IdeologyConvert
{
    private const string PrepareTool = "test/convert_colonist_prepare";
    private const string InspectTool = "test/convert_colonist_inspect";

    private static readonly FixtureOp<NoArgs, Fx.ConvertColonistPrepared> Prepare = new(
        PrepareTool,
        _ => [],
        ServedOps.Succeeded<Fx.ConvertColonistPrepared>(PrepareTool, r => r.Success && r.GuideId.Length > 0 && r.TargetId.Length > 0));

    private static readonly FixtureOp<string, Fx.ConvertColonistRead> Inspect = new(
        InspectTool,
        target => ServedOps.Args(("targetId", target)),
        ServedOps.Succeeded<Fx.ConvertColonistRead>(InspectTool, r => r.Success));

    internal static TypedCase Case() => new()
    {
        Name = "ideology/convert-colonist",
        Scope = "#3501: the moral guide's Convert ability is ordered on a colonist of another ideoligion through Actions/Apply, and the "
            + "game's cast job lowers the target's certainty or flips the target to the guide's ideoligion.",
        Ticks = 6_000,
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
        var prepared = await s.FixtureAsync("prepare-conversion", Prepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("fixture", ServedOps.Json(prepared));
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        var result = await TradeKit.ApplyAsync(s, "convert-colonist", new RimGovernor.Brain.GameOrders.Operations.ConversionIntent
        {
            GuideId = prepared.GuideId,
            TargetId = prepared.TargetId,
        }, cancellationToken).ConfigureAwait(false);
        _ = TradeKit.Applied("convert-colonist", result);

        // The guide walks to the target and casts; the conversion lands as a lower certainty or another ideoligion.
        _ = await s.RunUntilAsync("target-converted", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("convert-inspect", Inspect, prepared.TargetId, ct).ConfigureAwait(false);
            return ($"{read.TargetIdeoId} {read.Certainty}", read.TargetIdeoId != prepared.TargetIdeoId || read.Certainty < prepared.Certainty);
        }, cancellationToken).ConfigureAwait(false);
    }
}
