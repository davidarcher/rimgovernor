using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>temperature/building</c> (Go <c>temperature/building.go</c>): the SetBuildingTemperature vertical through
/// BuildingPatchIntent's target_temperature arm on Actions/Apply, against a private disposable fixture
/// (<c>test/building_temperature_prepare</c>) since a fresh baseline colony does not reliably start with a
/// temperature-controlled building.
/// </summary>
internal static class BuildingTemperature
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "temperature/building",
        Scope = "BuildingPatchIntent target temperature against an exact CompTempControl building: "
            + "out-of-range refusal, the real write read back, and key replay idempotency.",
        Start = new FixtureStart { Op = "test/building_temperature_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        if (!Dyn.Contains(s.Names, "rimgovernor/observations_list_buildings"))
        {
            throw new AcceptanceException("missing rimgovernor/observations_list_buildings in discovery");
        }

        var thingId = prepared["thingId"].AsString();
        if (thingId.Length == 0)
        {
            throw new AcceptanceException($"building_temperature_prepare: unexpected fixture identifier: {prepared.ToJson()}");
        }

        async Task<JsonObject> Target(string label)
        {
            var reply = await h.WireAsync(
                label,
                "observations_list_buildings",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["ids"] = new JsonArray(thingId),
                },
                cancellationToken).ConfigureAwait(false);
            var (_, observed) = Harness.Outcome(reply, "observed");
            var buildings = observed["buildings"].AsSlice();
            if (buildings.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected exactly one building, got {observed.ToJson()}");
            }

            var row = buildings[0].AsMap() ?? [];
            if (row["building"].AsMap()?["id"].AsString() != thingId)
            {
                throw new AcceptanceException($"{label}: fixture building identity mismatch: {row.ToJson()}");
            }

            return row;
        }

        (double Temperature, string Token) SettingsOf(JsonObject row)
        {
            var settings = row["settings"].AsMap() ?? throw new AcceptanceException($"missing settings: {row.ToJson()}");
            var snapshot = settings["snapshot"].AsMap();
            if (snapshot is null || snapshot["entityId"].AsString() != thingId)
            {
                throw new AcceptanceException($"missing or mismatched settings snapshot: {settings.ToJson()}");
            }

            if (!settings.ContainsKey("targetTemperatureC"))
            {
                throw new AcceptanceException($"missing targetTemperatureC: {settings.ToJson()}");
            }

            return (settings["targetTemperatureC"].AsNumber(), snapshot["token"].AsString());
        }

        async Task<JsonObject> Apply(string key, double temperature)
        {
            var reply = await h.WireAsync(
                key,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["key"] = key,
                        ["buildingPatch"] = new JsonObject { ["thingId"] = thingId, ["targetTemperature"] = temperature },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            return results.Count != 1
                ? throw new AcceptanceException($"{key}: expected one result: {reply.ToJson()}")
                : results[0].AsMap() ?? [];
        }

        var beforeRow = await Target("target-before").ConfigureAwait(false);
        var (beforeTemperature, beforeToken) = SettingsOf(beforeRow);
        if (beforeToken.Length == 0)
        {
            throw new AcceptanceException($"target-before: missing settings snapshot token: {beforeRow.ToJson()}");
        }

        var newTemperature = beforeTemperature + 5;
        if (newTemperature > 1000)
        {
            newTemperature = beforeTemperature - 5;
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        // Refusal: a target outside the game's interface range.
        var outOfRange = await Apply("temperature-out-of-range", 5000).ConfigureAwait(false);
        if (outOfRange["refused"] is null)
        {
            throw new AcceptanceException($"temperature-out-of-range: expected a refusal, got {outOfRange.ToJson()}");
        }

        var set = await Apply("temperature-set", newTemperature).ConfigureAwait(false);
        var applied = set["applied"].AsMap() ?? throw new AcceptanceException($"temperature-set: expected an applied result, got {set.ToJson()}");
        var appliedSettings = applied["observed"].AsMap()?["settings"].AsMap();
        var fields = appliedSettings?["fields"].AsSlice() ?? [];
        if (fields.Count != 1)
        {
            throw new AcceptanceException($"temperature-set: expected exactly one field result, got {appliedSettings.ToJson()}");
        }

        var field = fields[0].AsMap();
        if (field?["field"].AsString() != "SETTINGS_FIELD_TEMPERATURE" || field["outcome"].AsString() != "FIELD_OUTCOME_APPLIED")
        {
            throw new AcceptanceException($"temperature-set: unexpected field result: {field.ToJson()}");
        }

        var afterRow = await Target("target-after").ConfigureAwait(false);
        var (afterTemperature, afterToken) = SettingsOf(afterRow);
        if (afterToken == beforeToken)
        {
            throw new AcceptanceException("target-after: expected the settings token to change");
        }

        if (Math.Abs(afterTemperature - newTemperature) > 0.01)
        {
            throw new AcceptanceException($"target-after: expected the native target temperature to change to {newTemperature}, got {afterTemperature}");
        }

        var replay = await Apply("temperature-set", newTemperature).ConfigureAwait(false);
        if (!Dyn.DeepEqual(replay, set))
        {
            throw new AcceptanceException($"temperature-set: resent key changed its result: {set.ToJson()} then {replay.ToJson()}");
        }
    }
}
