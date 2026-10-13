using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>apply/area-intent</c> (Go <c>area_intent.go</c>): AreaIntent on Actions/Apply (#1321). A bot area is created with
/// a cell, gains and loses cells, a resent create applies again on the same area, it is deleted (twice), and the home
/// area gains and loses one cell; every receipt carries the area load id and cell count after the edit.
/// </summary>
internal static class ApplyAreaIntent
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "apply/area-intent",
        Scope = "AreaIntent on Actions/Apply (#1321): a bot area is created with a cell, gains and loses cells, a resent "
            + "create applies again on the same area, it is deleted (twice), and the home area gains and loses one cell; every "
            + "receipt carries the area load id and cell count after the edit.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(2),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var report = s.Report;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> Apply(string key, JsonObject area)
        {
            var reply = await h.WireAsync(
                key,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = key, ["area"] = area }),
                },
                cancellationToken).ConfigureAwait(false);
            var result = ApplyCases.OneResult(key, reply);
            return ApplyCases.AppliedEffect(result, "areaEdit")
                ?? throw new AcceptanceException($"{key}: expected an applied area edit, got {result.ToJson()}");
        }

        static int Count(JsonObject effect) => (int)effect["cellCount"].AsNumber();

        async Task<JsonObject> Step(string key, string op, JsonArray? cells, int wantCount, bool wantPresent)
        {
            var intent = new JsonObject { ["operation"] = op, ["key"] = "accept" };
            if (cells is { Count: > 0 })
            {
                intent["cells"] = cells;
            }

            var effect = await Apply(key, intent).ConfigureAwait(false);
            var present = effect["present"].AsBool() == true;
            if (present != wantPresent || Count(effect) != wantCount || (effect["areaId"].AsString().Length != 0) != wantPresent)
            {
                throw new AcceptanceException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{key}: expected present={(wantPresent ? "true" : "false")} with {wantCount} cells, got {effect.ToJson()}"));
            }

            report[key] = effect;
            return effect;
        }

        var created = await Step("area-create", "AREA_OPERATION_CREATE", [ApplyCases.Cell(10, 10)], 1, true).ConfigureAwait(false);
        _ = await Step("area-set", "AREA_OPERATION_SET_CELLS", [ApplyCases.Cell(10, 11), ApplyCases.Cell(11, 10)], 3, true).ConfigureAwait(false);
        _ = await Step("area-clear", "AREA_OPERATION_CLEAR_CELLS", [ApplyCases.Cell(10, 10)], 2, true).ConfigureAwait(false);
        var again = await Step("area-create-again", "AREA_OPERATION_CREATE", null, 2, true).ConfigureAwait(false);
        if (again["areaId"].AsString() != created["areaId"].AsString())
        {
            throw new AcceptanceException($"re-create made a second area {again["areaId"].ToJson()} beside {created["areaId"].ToJson()}");
        }

        foreach (var key in new[] { "area-delete", "area-delete-again" })
        {
            _ = await Step(key, "AREA_OPERATION_DELETE", null, 0, false).ConfigureAwait(false);
        }

        async Task<int> Home(string key, string op)
        {
            var effect = await Apply(
                key,
                new JsonObject { ["operation"] = op, ["home"] = true, ["cells"] = new JsonArray(ApplyCases.Cell(12, 12)) }).ConfigureAwait(false);
            if (effect["home"].AsBool() != true || effect["areaId"].AsString().Length == 0)
            {
                throw new AcceptanceException($"{key}: expected the home area, got {effect.ToJson()}");
            }

            report[key] = effect;
            return Count(effect);
        }

        var set = await Home("home-set", "AREA_OPERATION_SET_CELLS").ConfigureAwait(false);
        var cleared = await Home("home-clear", "AREA_OPERATION_CLEAR_CELLS").ConfigureAwait(false);
        if (cleared != set - 1)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"home clear left {cleared} cells after set reached {set}"));
        }
    }
}
