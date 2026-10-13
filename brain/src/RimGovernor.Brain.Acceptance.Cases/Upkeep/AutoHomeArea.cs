using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>upkeep/auto-home-area</c> (Go <c>auto_home_area.go</c>).</summary>
internal static class UpkeepAutoHomeArea
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "upkeep/auto-home-area",
        Scope = "AutoHomeAreaIntent on Actions/Apply (#1322): flipping Find.PlaySettings.autoHomeArea applies, the same value "
            + "again is UNCHANGED, and UpkeepFacts.auto_home_area reads each value back.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(2),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        async Task<bool> ReadAsync(string label)
        {
            var observed = await UpkeepSupport.ReadColonyFactsAsync(h, identity, label, cancellationToken).ConfigureAwait(false);
            var upkeep = UpkeepSupport.UpkeepSection(observed, label);
            // A set optional is emitted even when false.
            return upkeep["autoHomeArea"].AsBool()
                ?? throw new AcceptanceException($"{label}: auto_home_area absent: {upkeep["issues"].ToJson()}");
        }

        async Task ApplyAsync(string key, bool enabled, string outcome)
        {
            var reply = await h.WireAsync(key, "operations_apply", new JsonObject
            {
                ["identity"] = identity.DeepClone(),
                ["actions"] = new JsonArray(new JsonObject
                {
                    ["key"] = key,
                    ["autoHomeArea"] = new JsonObject { ["enabled"] = enabled },
                }),
            }, cancellationToken).ConfigureAwait(false);
            var result = CaseSupport.OneResult(key, reply);
            var fields = CaseSupport.AppliedEffect(result, "settings")?["fields"].AsSlice() ?? [];
            if (fields.Count != 1)
            {
                throw new AcceptanceException($"{key}: expected one settings field, got {result.ToJson()}");
            }

            var field = fields[0].AsMap();
            if (field?["field"].AsString() != "SETTINGS_FIELD_AUTO_HOME_AREA" || field["outcome"].AsString() != outcome)
            {
                throw new AcceptanceException($"{key}: expected {outcome}, got {field.ToJson()}");
            }

            var got = await ReadAsync(key + "-readback").ConfigureAwait(false);
            if (got != enabled)
            {
                throw new AcceptanceException($"{key}: fact reads {got}, want {enabled}");
            }
        }

        var initial = await ReadAsync("initial").ConfigureAwait(false);
        s.Report["initial"] = initial;
        await ApplyAsync("auto-home-flip", !initial, "FIELD_OUTCOME_APPLIED").ConfigureAwait(false);
        await ApplyAsync("auto-home-again", !initial, "FIELD_OUTCOME_UNCHANGED").ConfigureAwait(false);
        await ApplyAsync("auto-home-restore", initial, "FIELD_OUTCOME_APPLIED").ConfigureAwait(false);
    }
}
