using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>caravan/departure</c> (Go <c>departure.go</c>): the FormCaravanIntent arm of Actions/Apply end to end against a
/// live game: a fixed pack (WoodLog trade cargo, the forbidden pemmican reserve and survival meals, simple meals left
/// home) applied through rimgovernor/operations_apply, and native's own caravan inventory census, which must carry
/// exactly that pack. Uses a private disposable fixture (test/caravan_departure_prepare) to guarantee a colony shaped
/// for it (leave at least one home colonist) with a forbidden pemmican reserve, survival meals, simple meals and WoodLog
/// cargo, and a real reachable destination.
/// </summary>
internal static class CaravanDeparture
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "caravan/departure",
        Scope = "Native FormCaravanIntent: a fixed pack (WoodLog, the forbidden pemmican reserve, survival "
            + "meals, simple meals left home) formed through Actions/Apply, native's caravan inventory "
            + "carrying exactly that pack, native's home-staffing refusal, replay and a departed crew applied again.",
        Start = new FixtureStart
        {
            On = new LabStart(),
            Op = "test/caravan_departure_prepare",
            Args = new JsonObject { ["crewCount"] = 1 },
        },
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var crewPawnIds = prepared["crewPawnIds"].AsSlice().Select(raw => raw.AsString()).ToList();
        var remainingPawnIds = prepared["remainingPawnIds"].AsSlice().Select(raw => raw.AsString()).ToList();
        var destinationTile = prepared["destinationTile"].AsNumber();
        if (crewPawnIds.Count != 1 || remainingPawnIds.Count < 2 || destinationTile <= 0)
        {
            throw new AcceptanceException($"caravan_departure_prepare: unexpected fixture identifiers: {prepared.ToJson()}");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        // The fixture's whole forbidden pemmican stack (20) plus survival meals for the trip; simple meals stay home.
        var packed = new Dictionary<string, long>(StringComparer.Ordinal) { ["WoodLog"] = 10, ["Pemmican"] = 20, ["MealSurvivalPack"] = 10 };
        var cargo = new JsonArray();
        foreach (var (def, count) in packed)
        {
            cargo.Add(new JsonObject { ["defName"] = def, ["count"] = count });
        }

        async Task<JsonObject> Apply(string label, string key, IEnumerable<string> pawnIds)
        {
            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["key"] = key,
                        ["formCaravan"] = new JsonObject
                        {
                            ["pawnIds"] = new JsonArray([.. pawnIds.Select(id => (JsonNode?)JsonValue.Create(id))]),
                            ["cargo"] = cargo.DeepClone(),
                            ["destinationTile"] = destinationTile,
                        },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            if (results.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected one result, got {reply.ToJson()}");
            }

            return results[0].AsMap() ?? [];
        }

        void Formed(string label, JsonObject result)
        {
            var effect = result["applied"].Member("applied").Member("observed").Member("caravan").AsMap();
            if (effect?["assemblyStarted"].AsBool() != true || effect["destinationTile"].AsNumber() != destinationTile)
            {
                throw new AcceptanceException($"{label}: expected an applied formation toward the destination, got {result.ToJson()}");
            }
        }

        // Refusal: native keeps at least one colonist home.
        var allPawnIds = crewPawnIds.Concat(remainingPawnIds).ToList();
        var result = await Apply("leave-no-one-home", "caravan-departure-everyone", allPawnIds).ConfigureAwait(false);
        if (result["refused"].AsMap() is not { } refusal || refusal["code"].AsString() != "FAILURE_CODE_INVALID_REQUEST")
        {
            throw new AcceptanceException($"leave-no-one-home: expected an invalid-request refusal, got {result.ToJson()}");
        }

        // Apply: the real native Dialog_FormCaravan mechanism (TryFormAndSendCaravan) with exactly the composed pack.
        result = await Apply("apply", "caravan-departure", crewPawnIds).ConfigureAwait(false);
        Formed("apply", result);

        // Replay: the same key returns the identical result.
        var replay = await Apply("replay", "caravan-departure", crewPawnIds).ConfigureAwait(false);
        if (!Dyn.DeepEqual(replay, result))
        {
            throw new AcceptanceException($"replay: the same key returned a different result: {replay.ToJson()}");
        }

        // The crew gathers the pack and walks to the exit in game time, so the wait is a tick budget (RunUntil runs and
        // re-pauses the clock).
        JsonObject? caravan = null;
        try
        {
            _ = await RunAt.RunUntilAsync(
                h,
                "observe-formation",
                Wait.TicksPerDay,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var read = await h.WireAsync(
                        "observe-formation-world",
                        "observations_read_world_progression",
                        new JsonObject
                        {
                            ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                        },
                        ct).ConfigureAwait(false);
                    var (_, world) = Harness.Outcome(read, "observed");
                    var rows = world["caravans"].AsSlice();
                    foreach (var raw in rows)
                    {
                        var row = raw.AsMap();
                        var pawns = row?["pawns"].AsSlice() ?? [];
                        if (row is not null && pawns.Count == 1 && pawns[0].Member("pawn").RefId() == crewPawnIds[0])
                        {
                            caravan = row;
                            return (row["caravan"].RefId(), true);
                        }
                    }

                    return (string.Create(CultureInfo.InvariantCulture, $"{rows.Count} caravans"), false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"observe: caravan formation did not complete: {e.Message}", e);
        }

        // Native's own caravan census carries exactly the composed pack: the reserve and survival meals aboard, every
        // simple meal left home.
        var inventory = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in caravan?["inventory"].AsSlice() ?? [])
        {
            inventory[item.Member("defName").AsString()] = (long)item.Member("units").AsNumber();
        }

        foreach (var def in new[] { "WoodLog", "Pemmican", "MealSurvivalPack", "MealSimple" })
        {
            var carried = inventory.GetValueOrDefault(def);
            var wanted = packed.GetValueOrDefault(def);
            if (carried != wanted)
            {
                throw new AcceptanceException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"world-progression: caravan carries {def} x{carried}, the pack had x{wanted} (inventory {caravan?["inventory"].ToJson()})"));
            }
        }

        // A fresh key after the crew departed is applied again: the intent already holds, so a resend after a lost
        // reply cannot form twice.
        var again = await Apply("apply-again", "caravan-departure-again", crewPawnIds).ConfigureAwait(false);
        Formed("apply-again", again);
    }
}
