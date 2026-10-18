using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/theme-colony</c> (#3511): a colony the launcher starts from the naked-cannibals theme, through the player
/// API route the launcher posts, holds the theme's memes, precepts and venerated animal in its primary ideoligion. The
/// brain designs it in-process from the game's Defs; the case reads what the live colony holds.
/// </summary>
internal static class LifecycleThemeColony
{
    private const string InspectTool = "test/ideoligion_design_inspect";

    // The naked-cannibals preset's ideology members (ThemePresetCatalog).
    private static readonly string[] Memes = ["Cannibal", "Nudism"];
    private static readonly string[] Precepts = ["Cannibalism_Preferred", "InsectMeatEating_Loved", "Nudity_Female_Mandatory", "Nudity_Male_Mandatory"];
    private const string Animal = "Monkey";

    private static readonly FixtureOp<bool, Fx.IdeoligionDesignRead> Inspect = new(
        InspectTool,
        _ => new JsonObject(),
        (label, reply) => reply is not null
            ? Fx.FixtureJson.Deserialize<Fx.IdeoligionDesignRead>(reply.ToJsonString())
            : throw new AcceptanceException($"{label}: {InspectTool} replied nothing"));

    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/theme-colony",
        Scope = "#3511: a new colony posted to the player API's /api/lifecycle/new with theme naked-cannibals (the launcher's request) starts with a "
            + "primary ideoligion that holds the Nudism and Cannibal memes, the preset's nudity, cannibalism and insect-meat precepts and the venerated "
            + "monkey, and no non-player faction holds that ideoligion (#3591). A unit test cannot prove the design against the game's real meme and precept data or native creation. Spends no ticks.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new OwnedStart(),
        ClaimsStart = new StartClaim { Launches = 1 },
        Expansions = ["ludeon.rimworld.ideology"],
        Ops = [InspectTool],
        Run = RunAsync,
    };

    private static Task RunAsync(TypedSession s, CancellationToken cancellationToken) =>
        NewColonyStart.MenuAsync(
            s,
            "theme",
            async () =>
            {
                var spec = NewColonyStart.Spec() with
                {
                    Scenario = "LostTribe", Storyteller = "Phoebe", Difficulty = "Peaceful", Seed = "theme-colony-accept",
                    SaveName = "theme-colony-accept", Theme = "naked-cannibals",
                };
                _ = await NewColonyStart.CompleteAsync(s, "theme", NewColonyStart.Request("theme-create", spec), cancellationToken, throughBrain: true).ConfigureAwait(false);
                var read = await s.FixtureAsync("created-design", Inspect, true, cancellationToken).ConfigureAwait(false);
                var design = WireJson.Deserialize<Common.IdeoligionDesign>(read.Design);
                s.RecordWire("design", design);
                var missing = Memes.Except(design.Memes).Concat(Precepts.Except(design.Precepts)).Concat(new[] { Animal }.Except(design.VeneratedAnimals)).ToList();
                if (missing.Count > 0)
                {
                    throw new AcceptanceException($"the themed colony's ideoligion lacks [{string.Join(' ', missing)}]: {design}");
                }

                if (read.SharedWith.Length > 0)
                {
                    throw new AcceptanceException($"the themed ideoligion is shared with [{string.Join(' ', read.SharedWith)}]");
                }
            },
            cancellationToken);
}
