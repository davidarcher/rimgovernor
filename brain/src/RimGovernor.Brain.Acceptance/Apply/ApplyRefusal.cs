using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>apply/refusal</c> (Go <c>refusal.go</c>): executes one routine write per kind with a snapshot token that was
/// valid when the fixture read it, after the fixture has moved the world under that token, and asserts the refusal names
/// the fact that moved (the reason strings in docs/developers/contracts/action-contracts.md) rather than the token. The
/// token comparison is the closing rule of every kind; the zone-creation and excavation steps reach it by moving only
/// what the token hashes, so their documented closing reasons are asserted too. Every refusal is a terminal failure
/// reply, never a receipt.
/// </summary>
internal static class ApplyRefusal
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "apply/refusal",
        Scope = "Apply-time precondition refusals (#242, #252): for zone cell edit, stockpile patch, zone creation, "
            + "haul, work settings, bills, build, tame and grower crop, "
            + "a write whose token was valid when read is executed after the fixture moved the world and is refused "
            + "with the documented reason naming the moved fact.",

        // Clutter plants every bare cell around the colonist before the fixture stages its interior, so the initial map
        // cannot supply one untouched and the fixture's own clearing is exercised; the case asserts the planting happened.
        Start = new FixtureStart
        {
            Op = "test/apply_refusal_prepare",
            Args = new JsonObject { ["clutter"] = true },
            On = new LabStart(),
        },
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static JsonObject XZ(JsonNode? cell)
    {
        var map = cell.AsMap();
        return new JsonObject { ["x"] = map?["x"]?.DeepClone(), ["z"] = map?["z"]?.DeepClone() };
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        foreach (var required in new[] { "rimgovernor/operations_apply", "rimgovernor/placement_preview", "rimgovernor/observations_list_pawns", "test/apply_refusal_move" })
        {
            if (!Dyn.Contains(s.Names, required))
            {
                throw new AcceptanceException($"missing {required} in discovery");
            }
        }

        string Str(string key)
        {
            var value = prepared[key].AsString();
            return value.Length == 0 ? throw new AcceptanceException($"prepare: missing {key}: {prepared.ToJson()}") : value;
        }

        List<JsonObject> CellsOf(string key)
        {
            var rows = prepared[key].AsSlice();
            if (rows.Count == 0)
            {
                throw new AcceptanceException($"prepare: missing {key}: {prepared.ToJson()}");
            }

            return [.. rows.Select(XZ)];
        }

        if (prepared["clutterPlanted"].AsNumber() <= 0)
        {
            throw new AcceptanceException($"prepare: clutter planted nothing, the initial map supplied the interior untouched: {prepared["clutterPlanted"].ToJson()}");
        }

        report["clutter_planted"] = prepared["clutterPlanted"];
        var zoneCells = CellsOf("zoneCells");
        var freeCells = CellsOf("freeCells");
        var buildCell = XZ(prepared["buildCell"]);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "zoneId", "haulItemId", "pawnId", "tameId", "growerId", "growerCrop" })
        {
            tokens[key] = Str(key);
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        async Task Move(string label, JsonObject args)
        {
            var result = await h.CallAsync(label, "test/apply_refusal_move", args, cancellationToken).ConfigureAwait(false);
            if (result["success"].AsBool() != true)
            {
                throw new AcceptanceException($"{label}: fixture move refused: {result.ToJson()}");
            }
        }

        // IntentRefused applies one Actions/Apply intent and asserts its result is a refusal whose code and reason are
        // the documented ones.
        async Task IntentRefused(string label, JsonObject action, string code, string reason)
        {
            action["key"] = "refusal-" + label;
            var reply = await h.WireAsync(
                "apply-" + label,
                "operations_apply",
                new JsonObject { ["identity"] = identity.DeepClone(), ["actions"] = new JsonArray(action) },
                cancellationToken).ConfigureAwait(false);
            var result = ApplyCases.OneResult(label, reply);
            var refusal = result["refused"].AsMap();
            var got = refusal?["reason"].AsString() ?? "";
            if (refusal is null || refusal["code"].AsString() != code || !got.Contains(reason, StringComparison.Ordinal))
            {
                throw new AcceptanceException($"{label}: expected {code} \"{reason}\", got {result.ToJson()}");
            }

            report[label.Replace('-', '_')] = got;
        }

        static string At(JsonObject cell) => string.Create(CultureInfo.InvariantCulture, $"({cell["x"].ToJson()}, {cell["z"].ToJson()})");

        // Build: the open build cell is walled over after the placement preview accepted it; the Actions/Apply building
        // intent is refused with the re-planned placement's own reason. A Campfire, not a wall: the fixture's player wall
        // would otherwise be applied as it stands.
        JsonObject Placement() => new()
        {
            ["defName"] = "Campfire",
            ["x"] = buildCell["x"]?.DeepClone(),
            ["z"] = buildCell["z"]?.DeepClone(),
            ["rotation"] = "ROTATION_NORTH",
        };

        async Task<bool> CanPlace(string label)
        {
            var reply = await h.WireAsync(
                "placement-" + label,
                "placement_preview",
                new JsonObject { ["identity"] = identity.DeepClone(), ["placements"] = new JsonArray(Placement()) },
                cancellationToken).ConfigureAwait(false);
            var batch = reply["batch"].AsMap();
            var results = batch?["results"].AsSlice() ?? [];
            if (results.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected one placement result, got {reply.ToJson()}");
            }

            var result = results[0].AsMap();
            var evaluated = result?["evaluated"].AsMap()
                ?? throw new AcceptanceException($"{label}: expected an evaluated placement, got {result.ToJson()}");
            return evaluated["canPlace"].AsBool() == true;
        }

        if (!await CanPlace("build-open").ConfigureAwait(false))
        {
            throw new AcceptanceException("build-open: expected the open build cell to be placeable");
        }

        await Move("fill-build-cell", new JsonObject { ["action"] = "fill_cell", ["x"] = buildCell["x"]?.DeepClone(), ["z"] = buildCell["z"]?.DeepClone() }).ConfigureAwait(false);
        if (await CanPlace("build-blocked").ConfigureAwait(false))
        {
            throw new AcceptanceException("build-blocked: expected the walled build cell to be unplaceable");
        }

        var applied = await h.WireAsync(
            "apply-build",
            "operations_apply",
            new JsonObject
            {
                ["identity"] = identity.DeepClone(),
                ["actions"] = new JsonArray(new JsonObject
                {
                    ["key"] = "apply-refusal-build",
                    ["building"] = new JsonObject { ["placement"] = Placement() },
                }),
            },
            cancellationToken).ConfigureAwait(false);
        var appliedResults = applied["results"].AsSlice();
        JsonObject? buildRefusal = null;
        if (appliedResults.Count == 1)
        {
            buildRefusal = appliedResults[0].AsMap()?["refused"].AsMap();
        }

        if (buildRefusal is null || buildRefusal["code"].AsString() != "FAILURE_CODE_INVALID_REQUEST" || buildRefusal["reason"].AsString().Length == 0)
        {
            throw new AcceptanceException($"build: expected one INVALID_REQUEST refusal with a reason, got {applied.ToJson()}");
        }

        report["build"] = buildRefusal["reason"].AsString();

        // Zone intents are Actions/Apply intents. Zone cell edit and zone creation: the free roofed cell is walled over
        // after the read.
        await Move("fill-free-cell", new JsonObject { ["action"] = "fill_cell", ["x"] = freeCells[0]["x"]?.DeepClone(), ["z"] = freeCells[0]["z"]?.DeepClone() }).ConfigureAwait(false);
        await IntentRefused(
            "zone-edit-add",
            new JsonObject
            {
                ["zone"] = new JsonObject
                {
                    ["zone"] = new JsonObject { ["id"] = tokens["zoneId"] },
                    ["addCells"] = new JsonObject { ["explicitCells"] = new JsonObject { ["cells"] = new JsonArray(freeCells[0].DeepClone()) } },
                },
            },
            "FAILURE_CODE_INVALID_REQUEST",
            "Zone cell edit refused: cell " + At(freeCells[0]) + " is not free zoneable ground").ConfigureAwait(false);
        await IntentRefused(
            "zone-create",
            new JsonObject
            {
                ["zone"] = new JsonObject
                {
                    ["label"] = "RimGovernor apply refusal",
                    ["kind"] = "ZONE_TYPE_STOCKPILE",
                    ["addCells"] = new JsonObject
                    {
                        ["rectangle"] = new JsonObject { ["origin"] = freeCells[0].DeepClone(), ["width"] = 1, ["height"] = 1 },
                    },
                    ["stockpile"] = new JsonObject { ["priority"] = "STORAGE_PRIORITY_NORMAL", ["preset"] = "FILTER_PRESET_NOTHING" },
                },
            },
            "FAILURE_CODE_INVALID_REQUEST",
            "Rectangle has no free vanilla-zoneable stockpile cells").ConfigureAwait(false);

        // Stockpile patch and a cell removal: the zone is gone. (A deletion of the gone zone is its effect already
        // holding, so it applies.)
        await Move("delete-zone", new JsonObject { ["action"] = "delete_zone", ["id"] = tokens["zoneId"] }).ConfigureAwait(false);
        await IntentRefused(
            "stockpile-patch",
            new JsonObject
            {
                ["zone"] = new JsonObject
                {
                    ["zone"] = new JsonObject { ["id"] = tokens["zoneId"] },
                    ["stockpile"] = new JsonObject { ["priority"] = "STORAGE_PRIORITY_IMPORTANT" },
                },
            },
            "FAILURE_CODE_NOT_FOUND",
            "Stockpile patch refused: the exact stockpile zone or storage building no longer exists on this map").ConfigureAwait(false);
        await IntentRefused(
            "zone-edit-remove",
            new JsonObject
            {
                ["zone"] = new JsonObject
                {
                    ["zone"] = new JsonObject { ["id"] = tokens["zoneId"] },
                    ["removeCells"] = new JsonObject { ["explicitCells"] = new JsonObject { ["cells"] = new JsonArray(zoneCells[0].DeepClone()) } },
                },
            },
            "FAILURE_CODE_NOT_FOUND",
            "Zone cell edit refused: the exact zone no longer exists on this map").ConfigureAwait(false);

        // Haul and work settings: the colonist was drafted after the read.
        await Move("draft-pawn", new JsonObject { ["action"] = "draft_pawn", ["id"] = tokens["pawnId"] }).ConfigureAwait(false);

        // Haul and work settings are intents on Actions/Apply: the refusal is the action's result, not a failure reply.
        await IntentRefused(
            "haul",
            new JsonObject { ["haul"] = new JsonObject { ["pawnId"] = tokens["pawnId"], ["thingId"] = tokens["haulItemId"] } },
            "FAILURE_CODE_INVALID_REQUEST",
            "Haul refused: the pawn is drafted").ConfigureAwait(false);
        await IntentRefused(
            "work-settings",
            new JsonObject
            {
                ["workSettings"] = new JsonObject
                {
                    ["pawnId"] = tokens["pawnId"],
                    ["work"] = new JsonArray(new JsonObject { ["workTypeDef"] = "Hauling", ["priority"] = 1 }),
                },
            },
            "FAILURE_CODE_INVALID_REQUEST",
            "Work settings refused: the pawn is drafted").ConfigureAwait(false);
        await Move("undraft-pawn", new JsonObject { ["action"] = "undraft_pawn", ["id"] = tokens["pawnId"] }).ConfigureAwait(false);

        // Bills are intents too: the same bill on the fixture's bench would find its matching bill standing and apply
        // again, so the refusal names a bench that is not on the map.
        await IntentRefused(
            "bill",
            new JsonObject
            {
                ["productionBill"] = new JsonObject
                {
                    ["benchId"] = "Building_missing_bench",
                    ["recipeDef"] = "ButcherCorpseFlesh",
                    ["settings"] = new JsonObject
                    {
                        ["repeatMode"] = "REPEAT_MODE_FOREVER",
                        ["suspended"] = false,
                        ["ingredientSearchRadius"] = 40,
                        ["store"] = new JsonObject { ["mode"] = "STORE_MODE_DROP_ON_FLOOR" },
                        ["corpseClass"] = "CORPSE_CLASS_ANIMAL",
                    },
                },
            },
            "FAILURE_CODE_NOT_FOUND",
            "Production bill refused: bench Building_missing_bench is not a loaded bill giver").ConfigureAwait(false);

        // Tame: the hare was designated for hunting after the read; the Actions/Apply husbandry intent is refused by
        // native tame eligibility. (A tame designation would apply as it stands.)
        await Move("designate-tame-prey", new JsonObject { ["action"] = "designate_hunt", ["id"] = tokens["tameId"] }).ConfigureAwait(false);
        await IntentRefused(
            "tame",
            new JsonObject { ["husbandry"] = new JsonObject { ["animalId"] = tokens["tameId"], ["order"] = "HUSBANDRY_ORDER_TAME" } },
            "FAILURE_CODE_INVALID_REQUEST",
            "Husbandry refused: native tame eligibility refused the animal or it is designated for hunting").ConfigureAwait(false);

        // Grower crop: the plant pot is gone.
        await Move("destroy-grower", new JsonObject { ["action"] = "destroy_thing", ["id"] = tokens["growerId"] }).ConfigureAwait(false);
        await IntentRefused(
            "grower-crop",
            new JsonObject { ["buildingPatch"] = new JsonObject { ["thingId"] = tokens["growerId"], ["plantDef"] = tokens["growerCrop"] } },
            "FAILURE_CODE_NOT_FOUND",
            "Exact plant grower is unavailable.").ConfigureAwait(false);
    }
}
