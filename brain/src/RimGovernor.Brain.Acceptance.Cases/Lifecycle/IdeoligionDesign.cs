using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/ideoligion-design</c>: production colony creation applies the governor's ideoligion design and the live
/// colony reports it.
/// <para>
/// The brain resolves the design in-process from the game's Defs (the spec's <c>governorIdeoligion</c> flag) and the
/// acceptance process has no Def catalog to repeat that choice, so the case starts the colony with
/// <c>governorIdeoligion</c> and asserts the design the live colony holds is a well-formed one (1..5 memes, a decided
/// fluid flag, a duplicate-free selection) with no reform count or points spent.
/// </para>
/// </summary>
internal static class LifecycleIdeoligionDesign
{
    private const string InspectTool = "test/ideoligion_design_inspect";

    private static readonly FixtureOp<bool, Fx.IdeoligionDesignRead> Inspect = new(
        InspectTool,
        _ => new JsonObject(),
        (label, reply) => reply is not null
            ? Fx.FixtureJson.Deserialize<Fx.IdeoligionDesignRead>(reply.ToJsonString())
            : throw new AcceptanceException($"{label}: {InspectTool} replied nothing"));

    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/ideoligion-design",
        Scope = "#1662/#1663: production colony creation from a fresh main menu with governorIdeoligion applies a well-formed design (1..5 memes, a decided fluid flag, "
            + "duplicate-free memes and precepts) and the live colony's primary ideoligion holds it with no reform count or points spent. A snapshot cannot prove "
            + "pre-map catalog availability or vanilla creation initialization. Spends no ticks.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new OwnedStart(),
        ClaimsStart = new StartClaim { Launches = 1 },
        Expansions = ["ludeon.rimworld.ideology"],
        Ops = [InspectTool],
        Run = RunAsync,
    };

    private static Task RunAsync(ITypedSession s, CancellationToken cancellationToken) =>
        NewColonyStart.MenuAsync(
            s,
            "ideoligion",
            async () =>
            {
                var spec = NewColonyStart.Spec() with { Storyteller = "Phoebe", Difficulty = "Peaceful", SaveName = "ideoligion-design-accept", GovernorIdeoligion = true };
                _ = await NewColonyStart.CompleteAsync(s, "ideoligion", NewColonyStart.Request("ideoligion-create", spec), cancellationToken).ConfigureAwait(false);
                var read = await s.FixtureAsync("created-design", Inspect, true, cancellationToken).ConfigureAwait(false);
                var design = WireJson.Deserialize<Common.IdeoligionDesign>(read.Design);
                s.RecordWire("design", design);
                var memes = design.Memes.ToList();
                var precepts = design.Precepts.ToList();
                if (memes.Count is < 1 or > 5 || design.Fluid is null || memes.Distinct(StringComparer.Ordinal).Count() != memes.Count
                    || precepts.Distinct(StringComparer.Ordinal).Count() != precepts.Count || read.Count != 0 || read.Points != 0)
                {
                    throw new AcceptanceException($"created design is not a fresh well-formed one: {design}, reforms {read.Count}, points {read.Points}");
                }
            },
            cancellationToken);
}
