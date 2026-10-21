using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/ideoligion-shared</c> (#3595): colony creation's ideoligion design, applied while a world faction holds
/// the player's primary ideoligion (vanilla's 20% reuse branch, which the seeded creation cases reach only by chance),
/// gives the player an ideoligion of its own and leaves the sharing faction's precepts as they were.
/// </summary>
internal static class LifecycleIdeoligionShared
{
    private const string DesignTool = "test/ideoligion_shared_design";
    private const string InspectTool = "test/ideoligion_design_inspect";

    private static readonly FixtureOp<NoArgs, Fx.IdeoligionSharedDesign> Design = new(
        DesignTool,
        _ => [],
        ServedOps.Succeeded<Fx.IdeoligionSharedDesign>(DesignTool, r => r.Success && r.Before.Count > 0));

    private static readonly FixtureOp<NoArgs, Fx.IdeoligionDesignRead> Inspect = ServedOps.Read<Fx.IdeoligionDesignRead>(InspectTool);

    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/ideoligion-shared",
        Scope = "#3595: with a world faction staged to hold the player's primary ideoligion, colony creation's design step leaves the "
            + "player's designed ideoligion unshared, holding the design's memes and precepts, and the sharing faction's precepts "
            + "(def and load id) unchanged. The lab stands in for the pre-map creation moment. Spends no ticks.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart(),
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        QuietWorld = true,
        Ops = [DesignTool, InspectTool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var applied = await s.FixtureAsync("shared-design", Design, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("shared-design", ServedOps.Json(applied));
        if (!applied.Before.SequenceEqual(applied.After, StringComparer.Ordinal))
        {
            throw new AcceptanceException($"the design rewrote {applied.Faction}'s precepts: [{string.Join(' ', applied.Before)}] -> [{string.Join(' ', applied.After)}]");
        }

        var read = await s.FixtureAsync("designed", Inspect, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("designed", ServedOps.Json(read));
        if (read.SharedWith.Length > 0)
        {
            throw new AcceptanceException($"the designed ideoligion is shared with [{string.Join(' ', read.SharedWith)}]");
        }

        var want = IdeologyReform.Decode("shared-design", applied.Design);
        var got = IdeologyReform.Decode("designed", read.Design);
        if (got.Fluid != want.Fluid || !got.Memes.Order(StringComparer.Ordinal).SequenceEqual(want.Memes.Order(StringComparer.Ordinal))
            || !got.Precepts.Order(StringComparer.Ordinal).SequenceEqual(want.Precepts.Order(StringComparer.Ordinal)))
        {
            throw new AcceptanceException($"the player's ideoligion does not hold the design: want {want}, got {got}");
        }
    }
}
