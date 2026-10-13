using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>food/empty-channels</c> and the empty-channel fixture the other food cases start from (Go <c>fixture.go</c>).</summary>
internal static class FoodFixture
{
    private const string PrepareOp = "test/food_channels_prepare";
    private const string ObserveOp = "test/food_channels_observe";

    /// <summary>
    /// Starts on the Core tribal8 save with no food channels and exactly <paramref name="units"/> of the named stock. The
    /// runner quiets the storyteller and freezes needs. Channel cases add their source after this start and before
    /// starting a service; Keep controls which needs their simulation exercises.
    /// </summary>
    internal static Start EmptyChannels(string foodDef, int units) => new FixtureStart
    {
        On = new SaveStart { Name = Baselines.BaselineName },
        Op = PrepareOp,
        Args = new JsonObject { ["foodDef"] = foodDef, ["stockUnits"] = units },
    };

    internal static AcceptanceCase Case() => new()
    {
        Name = "food/empty-channels",
        Scope = "Empty-channel fixture has no competing sources; native foodNutrition equals only the declared stock, including after a zero-stock reset.",
        Start = EmptyChannels("MealSurvivalPack", 10),
        RequiredOps = [ObserveOp, PrepareOp],
        Budget = TimeSpan.FromMinutes(2),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        await CheckFixtureAsync(s, s.Prepared ?? [], "stocked", 10, cancellationToken).ConfigureAwait(false);
        var prepared = await s.Harness.CallAsync("reset", PrepareOp, new JsonObject { ["stockUnits"] = 0 }, cancellationToken).ConfigureAwait(false);
        await CheckFixtureAsync(s, prepared, "empty", 0, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CheckFixtureAsync(ISession s, JsonObject prepared, string label, int units, CancellationToken cancellationToken)
    {
        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"fixture refused: {prepared.ToJson()}");
        }

        var ms = prepared["preparationMs"].Strict();
        if (ms is not { } millis || millis < 0 || millis >= 60000)
        {
            throw new AcceptanceException($"fixture preparation must take under a minute: {prepared["preparationMs"].ToJson()}");
        }

        var audit = await s.Harness.CallAsync(label + "-channels", ObserveOp, null, cancellationToken).ConfigureAwait(false);
        var observed = await NativeReads.ColonyFactsAsync(s, label + "-facts", planning: false, cancellationToken).ConfigureAwait(false);
        s.Report[label] = new JsonObject
        {
            ["prepared"] = prepared.DeepClone(),
            ["channels"] = audit.DeepClone(),
            ["foodNutrition"] = observed["foodNutrition"].Copy(),
        };
        CheckEmpty(audit, observed, prepared, units);
    }

    private static void CheckEmpty(JsonObject audit, JsonObject observed, JsonObject prepared, int units)
    {
        foreach (var key in new[] { "fields", "plants", "animals", "corpses", "producers" })
        {
            if (audit[key].Strict() is not { } value || value != 0)
            {
                throw new AcceptanceException($"fixture has unknown or nonzero {key}: {audit[key].ToJson()}");
            }
        }

        if (audit["stock"] is not JsonArray stock)
        {
            throw new AcceptanceException("fixture stock unobserved");
        }

        var count = 0.0;
        foreach (var raw in stock)
        {
            var row = raw.AsMap();
            if (row?["units"].Strict() is not { } n || n <= 0 || double.IsNaN(n) || double.IsInfinity(n)
                || row["defName"].AsString() != prepared["foodDef"].AsString() || row["spawned"].AsBool() != true)
            {
                throw new AcceptanceException($"unexpected food stock: {raw.ToJson()}");
            }

            count += n;
        }

        if (count != units)
        {
            throw new AcceptanceException($"stock units {NativeReads.Fmt(count)}, want {units}");
        }

        var declaredValue = prepared["declaredNutrition"].Strict();
        var actualValue = observed["foodNutrition"].Strict();
        if (declaredValue is not { } declared || actualValue is not { } actual
            || double.IsNaN(declared) || double.IsNaN(actual) || double.IsInfinity(declared) || double.IsInfinity(actual)
            || declared < 0 || (units > 0 && declared <= 0) || (units == 0 && declared != 0) || Math.Abs(actual - declared) > 1e-5)
        {
            throw new AcceptanceException($"native foodNutrition {observed["foodNutrition"].ToJson()} does not equal declared stock {prepared["declaredNutrition"].ToJson()}");
        }
    }

    /// <summary>
    /// Pauses the game before a fixture channel probe. The probes that drain a channel refuse on an unpaused game
    /// ("Pause before drain"): a drain mutates the map, so it runs on a still game or it races the very simulation it is
    /// measuring. Stopping a service does not leave the game paused, so a case that reattaches and probes must pause first.
    /// </summary>
    internal static Task PauseForProbeAsync(Harness harness, string label, CancellationToken cancellationToken) =>
        NativeReads.PauseAsync(harness, label + "-pause", cancellationToken);
}
