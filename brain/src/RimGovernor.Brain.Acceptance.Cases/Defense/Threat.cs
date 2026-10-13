using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>defense/threat</c> (Go <c>threat.go</c>): holds the typed colony facts' threat section to the game's own figures: on
/// the paused baseline the projected raid points and wealth split equal what the defense fixture reads from WealthWatcher
/// and StorytellerUtility.DefaultThreatPointsNow at the same tick, and after the fixture stocks wood and food and
/// recounts, the section reports the larger wealth and again the native raid points. The quiet storyteller sets difficulty
/// threatScale to 0, so raid points sit on the game's 35-point floor throughout: the case proves the projection, not the
/// wealth curve. Read-only: no orders, no clock.
/// </summary>
internal static class DefenseThreat
{
    /// <summary>Pairs the fixture's camelCase keys with the section's.</summary>
    private static readonly string[] s_threatFields =
    [
        "raidPoints", "wealthItems", "wealthBuildings", "wealthPawns", "wealthTotal", "storytellerWealth", "adaptationFactor", "difficultyThreatScale",
    ];

    internal static AcceptanceCase Case() => new()
    {
        Name = "defense/threat",
        Scope = "The typed colony read's threat section (raid points, the wealth split and the storyteller wealth) equals the native "
            + "WealthWatcher and DefaultThreatPointsNow figures on the paused " + Baselines.BaselineName + " save, and again after the "
            + "fixture stocks the colony and recounts; read-only (#395).",
        Start = new LabStart(),
        RequiredOps = ["test/defense_setup"],
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        if (!Dyn.Contains(s.Names, "test/defense_setup"))
        {
            throw new AcceptanceException("missing test/defense_setup in discovery; rebuild the native mod with -Fixture DefenseFixture");
        }

        async Task<JsonObject> FixtureAsync(string label, string op)
        {
            var output = await h.CallAsync(label, "test/defense_setup", new JsonObject { ["op"] = op }, cancellationToken).ConfigureAwait(false);
            if (output["success"].AsBool() != true)
            {
                throw new AcceptanceException($"defense_setup {op} refused: {output.ToJson()}");
            }

            return output["threat"].AsMap() ?? throw new AcceptanceException($"defense_setup {op} carries no threat block: {output.ToJson()}");
        }

        async Task<JsonObject> ProjectedAsync(string label)
        {
            var reply = await h.WireAsync(label, "observations_read_colony_facts", new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
            }, cancellationToken).ConfigureAwait(false);
            var observed = Harness.Outcome(reply, "observed").Value;
            var section = observed["threat"].AsMap() ?? throw new AcceptanceException("colony facts carry no threat section");
            JsonObject facts;
            try
            {
                facts = Harness.Outcome(section, "observed").Value;
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"threat section: {e.Message}", e);
            }

            foreach (var field in s_threatFields)
            {
                if (!facts.ContainsKey(field))
                {
                    throw new AcceptanceException($"threat section lacks {field}: {facts.ToJson()}");
                }
            }

            return facts;
        }

        // Native floats reach the case through two different encoders (the fixture's JSON and ProtoJSON), so
        // equality is to float32 precision.
        static void Equal(string stage, JsonObject native, JsonObject facts)
        {
            foreach (var field in s_threatFields)
            {
                double want = native[field].AsNumber();
                double got = facts[field].AsNumber();
                if (Math.Abs(want - got) > 1e-3 * Math.Max(1, Math.Abs(want)))
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{stage}: {field} projected {got}, native {want}"));
                }
            }
        }

        var baselineNative = await FixtureAsync("inspect-baseline", "inspect").ConfigureAwait(false);
        var baseline = await ProjectedAsync("colony-facts-baseline").ConfigureAwait(false);
        report["baseline"] = new JsonObject { ["native"] = baselineNative.DeepClone(), ["projected"] = baseline.DeepClone() };
        Equal("baseline", baselineNative, baseline);
        if (baseline["raidPoints"].AsNumber() < 35)
        {
            throw new AcceptanceException($"baseline raid points {baseline["raidPoints"].ToJson()} below the game's 35-point floor");
        }

        _ = await h.CallAsync("stock", "test/defense_setup", new JsonObject { ["op"] = "stock" }, cancellationToken).ConfigureAwait(false);
        var stockedNative = await FixtureAsync("wealth-stocked", "wealth").ConfigureAwait(false);
        var stocked = await ProjectedAsync("colony-facts-stocked").ConfigureAwait(false);
        report["stocked"] = new JsonObject { ["native"] = stockedNative.DeepClone(), ["projected"] = stocked.DeepClone() };
        Equal("stocked", stockedNative, stocked);
        // 1200 wood and 300 pemmican are worth well over 1000 silver; the recount must show them under items and
        // total alike.
        foreach (var field in new[] { "wealthItems", "wealthTotal" })
        {
            if (stocked[field].AsNumber() < baseline[field].AsNumber() + 1000)
            {
                throw new AcceptanceException($"{field} did not grow with the stocked supplies: {baseline[field].ToJson()} before, {stocked[field].ToJson()} after");
            }
        }

        if (stocked["raidPoints"].AsNumber() < baseline["raidPoints"].AsNumber())
        {
            throw new AcceptanceException($"raid points fell as wealth grew: {baseline["raidPoints"].ToJson()} before, {stocked["raidPoints"].ToJson()} after");
        }
    }
}
