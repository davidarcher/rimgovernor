using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/ideoligion-design</c> (Go <c>ideoligion.go</c>): production colony creation applies the governor's
/// ideoligion design and the live colony reports it.
/// <para>
/// Adaptation (#3090): the Go case decoded the creation catalog on the harness side, chose the design with
/// <c>StartingIdeoligion</c>, asserted the choice was deterministic and that the created colony reported exactly it.
/// The C# brain resolves the design in-process from the game's Defs (the spec's <c>governorIdeoligion</c> flag,
/// <c>BrainCompositionOptions.StartingIdeoligion</c>) and the acceptance process has no Def catalog to repeat that
/// choice, so the case starts the colony with <c>governorIdeoligion</c> and asserts the design the live colony reports
/// is a well-formed one (1..5 memes, a decided fluid flag, a duplicate-free selection) with no reform count or points
/// spent. The exact-equality and determinism assertions return with a harness-side creation-catalog decoder.
/// </para>
/// </summary>
internal static class LifecycleIdeoligionDesign
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/ideoligion-design",
        Scope = "#1662/#1663: a fresh main-menu catalog produces a deterministic fluid design; production colony creation applies the requested memes and plain precepts exactly and the live colony reports them. A snapshot cannot prove pre-map catalog availability or vanilla creation initialization.",
        Start = new OwnedStart(),
        Reason = "the production creation contract starts at a fresh main menu",
        Expansions = ["ludeon.rimworld.ideology"],
        NoKeep = true,
        Quiet = QuietMode.Required,
        RequiredOps = ["test/ideoligion_design_inspect"],
        Budget = TimeSpan.FromMinutes(10),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static Task RunAsync(ISession s, CancellationToken cancellationToken) =>
        NewColonyKit.MenuAsync(
            s,
            "ideoligion",
            NewColonyKit.MenuTools,
            async (h, row) =>
            {
                var spec = NewColonyKit.Spec();
                spec["storyteller"] = "Phoebe";
                spec["difficulty"] = "Peaceful";
                spec["saveName"] = "ideoligion-design-accept";
                spec["governorIdeoligion"] = true;
                _ = await NewColonyKit.RunAsync(h, "ideoligion-create", spec, "ideoligion", cancellationToken).ConfigureAwait(false);
                var got = await h.CallAsync("created-design", "test/ideoligion_design_inspect", new JsonObject(), cancellationToken).ConfigureAwait(false);
                var design = Dyn.ParseObject(got["design"].AsString());
                var memes = Names(design["memes"]);
                var precepts = Names(design["precepts"]);
                if (memes.Count is < 1 or > 5 || design["fluid"].AsBool() is null || !IsUnique(memes) || !IsUnique(precepts) || got["count"].AsNumber() != 0 || got["points"].AsNumber() != 0)
                {
                    throw new AcceptanceException($"created design differs: {got.ToJson()}");
                }

                row["design"] = got;
            },
            cancellationToken);

    private static List<string> Names(JsonNode? list) => [.. list.AsSlice().Select(n => n.AsString())];

    private static bool IsUnique(List<string> names) => names.Distinct(StringComparer.Ordinal).Count() == names.Count;
}
