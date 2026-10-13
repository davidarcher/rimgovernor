using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The zone/delete case applies native create, settings, cell-edit and delete intents to a fixture stockpile. Native
/// ListZones reads verify filters, priority, quality and hit-point ranges; an unresolvable selector must be refused.
/// Removing and restoring one corner verifies zone cells and free ground before deletion. Each intent is revalidated at
/// apply, and placement retries reuse the immutable key to recover all original created IDs (Go <c>delete.go</c>).
/// </summary>
internal static class ZoneDelete
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "zone/delete",
        Scope = "Native zone intents on operations_apply: a real allow-list stockpile zone with hit-point/quality ranges " +
            "is created by a ZoneIntent create over a disposable fixture site and its filter read back through " +
            "rimgovernor/observations_list_zones, its priority and ranges are changed by a ZoneIntent settings patch (an " +
            "unresolvable selector refused), one corner cell is removed and then re-added by ZoneIntent cell edits, and the " +
            "exact zone is deleted by a ZoneIntent delete, with a stale census token and a missing zone refused, real " +
            "applied evidence and ListZones readbacks, and resends of effects that already hold applying again. The " +
            "re-add runs under a playing clock window and the journal's observation_invalidated names the zone id and " +
            "its cell rectangle (#359).",
        Start = new FixtureStart { On = new LabStart(), Op = "test/zone_delete_prepare" },
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        // Fixture: one small roofed, walled, empty, unzoned 2x2 interior. Run this BEFORE acquiring authority,
        // mirroring the bed-assign/recovery-area ordering (external activity revokes any held lease).
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        foreach (var required in new[] { "rimgovernor/operations_apply", "rimgovernor/observations_list_zones" })
        {
            if (!s.Names.Contains(required))
            {
                throw new AcceptanceException($"missing {required} in discovery");
            }
        }

        var cellRows = prepared["cells"].AsSlice();
        if (cellRows.Count == 0)
        {
            throw new AcceptanceException($"prepare: missing fixture cells: {prepared.ToJson()}");
        }

        var cells = new List<JsonObject>();
        foreach (var row in cellRows)
        {
            var cell = row.AsMap() ?? [];
            cells.Add(new JsonObject { ["x"] = cell["x"]?.DeepClone(), ["z"] = cell["z"]?.DeepClone() });
        }

        report["fixture_cell_count"] = cells.Count;

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        // zoneRow reads one zone's row (with its filter) through observations_list_zones. Null with no exception
        // means the zone genuinely is not listed.
        async Task<JsonObject?> ZoneRow(string label, string zoneId)
        {
            var reply = await h.WireAsync(
                label,
                "observations_list_zones",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["ids"] = new JsonArray(zoneId),
                    ["includeFilter"] = true,
                },
                cancellationToken).ConfigureAwait(false);
            var (_, observed) = Harness.Outcome(reply, "observed");
            var rows = observed["zones"].AsSlice();
            if (rows.Count == 0)
            {
                return null;
            }

            if (rows.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected at most one observed zone, got {observed.ToJson()}");
            }

            var row = rows[0].AsMap() ?? [];
            if (row["id"].AsString() != zoneId)
            {
                throw new AcceptanceException($"{label}: unexpected zone row: {row.ToJson()}");
            }

            return row;
        }

        // zoneState reads one zone's row and its grid cells as a state token (their JSON), used here only to detect
        // whether a refused or resent intent touched the zone. Present=false means the zone is not listed.
        async Task<(string Token, bool Present)> ZoneState(string label, string zoneId)
        {
            var row = await ZoneRow(label, zoneId).ConfigureAwait(false);
            if (row is null)
            {
                return ("", false);
            }

            var grid = await h.MapCellsAsync(label + "-cells", identity, cancellationToken).ConfigureAwait(false);
            var gridCells = new JsonArray();
            foreach (var c in grid.ZoneCells().GetValueOrDefault(zoneId) ?? [])
            {
                gridCells.Add(CaseSupport.Cell(c.X, c.Z));
            }

            return (new JsonObject { ["row"] = row.DeepClone(), ["cells"] = gridCells }.ToJsonString(), true);
        }

        // zoneFilter asserts the exact filter readback: the allowed def set, the hit-point fractions and the quality names.
        async Task ZoneFilter(string label, string zoneId, string priority, string[] allowed, double hpMin, double hpMax, string qualityMin, string qualityMax)
        {
            var row = await ZoneRow(label, zoneId).ConfigureAwait(false)
                ?? throw new AcceptanceException($"{label}: expected the zone to be listed");
            if (row["priority"].AsString() != priority)
            {
                throw new AcceptanceException($"{label}: expected priority {priority}, got {row["priority"].ToJson()}");
            }

            var filter = row["filter"].AsMap()
                ?? throw new AcceptanceException($"{label}: expected a filter readback, got {row.ToJson()}");
            var names = filter["allowedDefNames"].AsSlice().Select(n => n.AsString()).ToArray();
            if (!names.SequenceEqual(allowed))
            {
                throw new AcceptanceException($"{label}: expected allowed defs [{string.Join(' ', allowed)}], got [{string.Join(' ', names)}]");
            }

            if (filter["hitPointsMin"].AsNumber() != hpMin || filter["hitPointsMax"].AsNumber() != hpMax)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: expected hit-point range [{hpMin}, {hpMax}], got {filter.ToJson()}"));
            }

            if (filter["qualityMin"].AsString() != qualityMin || filter["qualityMax"].AsString() != qualityMax)
            {
                throw new AcceptanceException($"{label}: expected quality range [{qualityMin}, {qualityMax}], got {filter.ToJson()}");
            }
        }

        // apply sends one intent (arm is its Action oneof field) through operations_apply and returns its single result.
        async Task<JsonObject> Apply(string label, string key, string arm, JsonObject intent)
        {
            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = key, [arm] = intent }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            if (results.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected one result, got {reply.ToJson()}");
            }

            return results[0].AsMap() ?? [];
        }

        // applied returns the ZoneEffect of an applied result.
        async Task<JsonObject> Applied(string label, string key, string arm, JsonObject intent)
        {
            var result = await Apply(label, key, arm, intent).ConfigureAwait(false);
            return CaseSupport.AppliedEffect(result, "zone")
                ?? throw new AcceptanceException($"{label}: expected applied zone evidence, got {result.ToJson()}");
        }

        // refused asserts a refusal with one of codes whose reason names the rule that failed.
        async Task Refused(string label, string key, string arm, JsonObject intent, string reason, params string[] codes)
        {
            var result = await Apply(label, key, arm, intent).ConfigureAwait(false);
            var refusal = result["refused"].AsMap();
            var got = refusal?["reason"].AsString() ?? "";
            if (refusal is null || !codes.Contains(refusal["code"].AsString()) || !got.Contains(reason, StringComparison.Ordinal))
            {
                throw new AcceptanceException($"{label}: expected [{string.Join(' ', codes)}] \"{reason}\", got {result.ToJson()}");
            }

            report[label.Replace('-', '_')] = got;
        }

        static JsonObject Obj(string key, string value) => new() { [key] = value };

        // --- create ---

        // The allow-list body bridge.stockpileSettings sends for an AllowOnlyFilter stockpile, plus both filter ranges.
        JsonObject StockpileBody() => new()
        {
            ["priority"] = "STORAGE_PRIORITY_IMPORTANT",
            ["preset"] = "FILTER_PRESET_NOTHING",
            ["filter"] = new JsonObject
            {
                ["allow"] = new JsonArray(Obj("thingDef", "Steel"), Obj("thingDef", "WoodLog")),
                ["hitPointsMin"] = 0.5,
                ["hitPointsMax"] = 1,
                ["qualityMin"] = "Normal",
                ["qualityMax"] = "Legendary",
            },
        };
        JsonObject Create() => new()
        {
            ["label"] = "Steel, WoodLog",
            ["kind"] = "ZONE_TYPE_STOCKPILE",
            ["addCells"] = new JsonObject
            {
                ["rectangle"] = new JsonObject { ["origin"] = cells[0].DeepClone(), ["width"] = 2, ["height"] = 2 },
            },
            ["stockpile"] = StockpileBody(),
        };

        var createEffect = await Applied("apply-create", "zone-create", "zone", Create()).ConfigureAwait(false);
        var created = createEffect["created"].AsSlice();
        if (created.Count != 1)
        {
            throw new AcceptanceException($"expected one created component: {createEffect.ToJson()}");
        }

        var createdZone = created[0].AsMap() ?? [];
        var zoneId = createdZone["zoneId"].AsString();
        if (zoneId.Length == 0)
        {
            throw new AcceptanceException($"apply-create: missing zoneId in effect evidence: {createEffect.ToJson()}");
        }

        if (createdZone["cells"].AsSlice().Count != cells.Count)
        {
            throw new AcceptanceException($"apply-create: expected a present {cells.Count}-cell zone, got {createEffect.ToJson()}");
        }

        report["created_zone_id"] = zoneId;
        var (tokenAfterCreate, present) = await ZoneState("zone-after-create", zoneId).ConfigureAwait(false);
        if (!present)
        {
            throw new AcceptanceException("zone-after-create: expected the created zone to be listed");
        }

        await ZoneFilter("filter-after-create", zoneId, "Important", ["Steel", "WoodLog"], 0.5, 1, "Normal", "Legendary").ConfigureAwait(false);

        // A lost reply resends the immutable placement key and recovers the receipt.
        var resent = await Applied("resend-create", "zone-create", "zone", Create()).ConfigureAwait(false);
        if (!Dyn.DeepEqual(resent, createEffect))
        {
            throw new AcceptanceException($"resend-create: expected the standing zone {zoneId}, got {resent.ToJson()}");
        }

        var (token, _) = await ZoneState("zone-after-create-resend", zoneId).ConfigureAwait(false);
        if (token != tokenAfterCreate)
        {
            throw new AcceptanceException("resend-create: a standing create changed the zone");
        }

        // --- settings ---
        //
        // Priority and the hit-point range change; the absent quality range and selectors preserve the live filter. A
        // selector that does not resolve refuses the whole body before anything is applied.

        await Refused(
            "patch-unresolved-selector",
            "stockpile-unresolved",
            "zone",
            new JsonObject
            {
                ["zone"] = Obj("id", zoneId),
                ["stockpile"] = new JsonObject
                {
                    ["filter"] = new JsonObject { ["allow"] = new JsonArray(Obj("thingDef", "NoSuchThingDefForZoneAccept")) },
                },
            },
            "Stockpile patch refused",
            "FAILURE_CODE_INVALID_REQUEST").ConfigureAwait(false);
        (token, _) = await ZoneState("zone-after-patch-unresolved", zoneId).ConfigureAwait(false);
        if (token != tokenAfterCreate)
        {
            throw new AcceptanceException("patch-unresolved-selector: a refused body unexpectedly changed the zone");
        }

        JsonObject Patch() => new()
        {
            ["zone"] = Obj("id", zoneId),
            ["stockpile"] = new JsonObject
            {
                ["priority"] = "STORAGE_PRIORITY_NORMAL",
                ["filter"] = new JsonObject { ["hitPointsMin"] = 0.25, ["hitPointsMax"] = 0.75 },
            },
        };
        var patchEffect = await Applied("apply-patch", "stockpile-patch", "zone", Patch()).ConfigureAwait(false);
        if (patchEffect["zoneId"].AsString() != zoneId)
        {
            throw new AcceptanceException($"apply-patch: unexpected zone effect identity: {patchEffect.ToJson()}");
        }

        await ZoneFilter("filter-after-patch", zoneId, "Normal", ["Steel", "WoodLog"], 0.25, 0.75, "Normal", "Legendary").ConfigureAwait(false);
        var (tokenAfterPatch, _) = await ZoneState("zone-after-patch", zoneId).ConfigureAwait(false);
        if (tokenAfterPatch == tokenAfterCreate)
        {
            throw new AcceptanceException("apply-patch: the listed zone did not change");
        }

        _ = await Applied("resend-patch", "stockpile-patch-resend", "zone", Patch()).ConfigureAwait(false);
        (token, _) = await ZoneState("zone-after-patch-resend", zoneId).ConfigureAwait(false);
        if (token != tokenAfterPatch)
        {
            throw new AcceptanceException("resend-patch: settings that already hold were written again");
        }

        // --- presets ---
        //
        // indoor_only and outdoor_safe partition the nonperishables: each preset is patched on and its allowed defs
        // read back from the live game.
        async Task<HashSet<string>> PresetDefs(string preset)
        {
            var label = "preset-" + preset;
            _ = await Applied(
                label,
                label,
                "zone",
                new JsonObject
                {
                    ["zone"] = Obj("id", zoneId),
                    ["stockpile"] = Obj("preset", "FILTER_PRESET_" + preset.ToUpperInvariant()),
                }).ConfigureAwait(false);
            var row = await ZoneRow(label + "-readback", zoneId).ConfigureAwait(false) ?? [];
            var filter = row["filter"].AsMap();
            return new HashSet<string>(
                (filter?["allowedDefNames"]).AsSlice().Select(n => n.AsString()),
                StringComparer.Ordinal);
        }

        var nonperishable = await PresetDefs("nonperishables").ConfigureAwait(false);
        var outdoor = await PresetDefs("outdoor_safe").ConfigureAwait(false);
        var indoor = await PresetDefs("indoor_only").ConfigureAwait(false);
        if (outdoor.Count == 0 || indoor.Count == 0)
        {
            throw new AcceptanceException($"presets: outdoor_safe has {outdoor.Count} defs and indoor_only {indoor.Count}; both must be non-empty");
        }

        foreach (var name in indoor)
        {
            if (outdoor.Contains(name) || !nonperishable.Contains(name))
            {
                throw new AcceptanceException($"presets: {name} must be nonperishable and in only one of indoor_only and outdoor_safe");
            }
        }

        foreach (var name in outdoor)
        {
            if (!nonperishable.Contains(name))
            {
                throw new AcceptanceException($"presets: outdoor_safe def {name} is not nonperishable");
            }
        }

        if (indoor.Count + outdoor.Count != nonperishable.Count)
        {
            throw new AcceptanceException($"presets: indoor_only ({indoor.Count}) and outdoor_safe ({outdoor.Count}) do not partition nonperishables ({nonperishable.Count})");
        }

        report["preset_partition"] = string.Create(CultureInfo.InvariantCulture, $"indoor_only {indoor.Count} + outdoor_safe {outdoor.Count} = nonperishables {nonperishable.Count}");

        // --- cells ---
        //
        // cells[0] is one corner of the fixture's 2x2 interior. Removing it leaves a contiguous 3-cell L-shape and
        // returns that cell to genuinely free ground (an add never steals a cell from another zone, so re-adding the
        // very cell this harness just freed is the one add case the fixture can exercise without a second site).

        JsonObject Edit(string zone, string op) => new()
        {
            ["zone"] = Obj("id", zone),
            [op] = new JsonObject
            {
                ["explicitCells"] = new JsonObject { ["cells"] = new JsonArray(cells[0].DeepClone()) },
            },
        };

        await Refused(
            "edit-missing-zone",
            "zone-edit-missing",
            "zone",
            Edit("Zone_999999", "removeCells"),
            "Zone cell edit refused: the exact zone no longer exists on this map",
            "FAILURE_CODE_NOT_FOUND").ConfigureAwait(false);
        var removeEffect = await Applied("apply-edit-remove", "zone-edit-remove", "zone", Edit(zoneId, "removeCells")).ConfigureAwait(false);
        if (removeEffect["present"].AsBool() != true || (int)removeEffect["listedCellCount"].AsNumber() != cells.Count - 1)
        {
            throw new AcceptanceException($"apply-edit-remove: expected {cells.Count - 1} listed cells after removing one, got {removeEffect.ToJson()}");
        }

        string tokenAfterRemove;
        (tokenAfterRemove, present) = await ZoneState("zone-after-edit-remove", zoneId).ConfigureAwait(false);
        if (!present || tokenAfterRemove == tokenAfterPatch)
        {
            throw new AcceptanceException("zone-after-edit-remove: expected the zone listed and changed by a real cell removal");
        }

        _ = await Applied("resend-edit-remove", "zone-edit-remove-resend", "zone", Edit(zoneId, "removeCells")).ConfigureAwait(false);
        (token, _) = await ZoneState("zone-after-edit-remove-resend", zoneId).ConfigureAwait(false);
        if (token != tokenAfterRemove)
        {
            throw new AcceptanceException("resend-edit-remove: a cell already out of the zone was edited again");
        }

        report["zone_cells_after_edit_remove"] = cells.Count - 1;

        // The add runs under a playing clock window so the native probe sees the zone change and journals an
        // observation_invalidated narrowed to this zone and its rectangle; the window is paused again before the
        // readbacks below.
        JsonObject? addEffect = null;
        _ = await ZoneInvalidation.EditUnderEpochAsync(
            h,
            identity,
            report,
            zoneId,
            cells,
            async () => addEffect = await Applied("apply-edit-add", "zone-edit-add", "zone", Edit(zoneId, "addCells")).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if ((int)(addEffect?["listedCellCount"]).AsNumber() != cells.Count)
        {
            throw new AcceptanceException($"apply-edit-add: expected {cells.Count} listed cells after re-adding the corner, got {addEffect.ToJson()}");
        }

        (_, present) = await ZoneState("zone-after-edit-add", zoneId).ConfigureAwait(false);
        if (!present)
        {
            throw new AcceptanceException($"zone-after-edit-add: expected the zone to be listed with all {cells.Count} cells restored");
        }

        report["zone_cells_after_edit_add"] = cells.Count;

        // --- delete ---

        JsonObject Delete() => new() { ["zone"] = Obj("id", zoneId), ["delete"] = true };
        var deleteEffect = await Applied("apply-delete", "zone-delete", "zone", Delete()).ConfigureAwait(false);
        if (deleteEffect["zoneId"].AsString() != zoneId)
        {
            throw new AcceptanceException($"apply-delete: unexpected zone effect identity: {deleteEffect.ToJson()}");
        }

        if (deleteEffect["present"].AsBool() == true)
        {
            throw new AcceptanceException($"apply-delete: expected present=false in the effect evidence, got {deleteEffect.ToJson()}");
        }

        bool afterDeletePresent;
        (_, afterDeletePresent) = await ZoneState("zone-after-delete", zoneId).ConfigureAwait(false);
        if (afterDeletePresent)
        {
            throw new AcceptanceException("zone-after-delete: expected the deleted zone to no longer be listed");
        }

        report["zone_deleted"] = true;

        // A zone already gone is the deletion's effect: the resend applies.
        _ = await Applied("resend-delete", "zone-delete-resend", "zone", Delete()).ConfigureAwait(false);
    }
}
