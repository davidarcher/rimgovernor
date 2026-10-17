using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>supplies/reads</c> (Go <c>cases/supplies</c>): typed supply-stock reads are internally consistent, for both held
/// and spawned-only ownership modes, on the lab.
/// </summary>
internal static class SuppliesReads
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "supplies/reads",
        Scope = "Fresh typed supply census: quantities, instances and holders agree within each row for held and spawned-only ownership over spawned WoodLog and Steel; no gameplay orders.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var names = s.Names;

        // The lab is bare: lay the starting materials the census reads.
        var c = LabWorld.LabMapSize / 2;
        var spawned = new[] { "WoodLog", "Steel" };
        for (var i = 0; i < spawned.Length; i++)
        {
            _ = await DebugGame.LabSpawnAsync(h, new LabThing { Def = spawned[i], X = c + 3 + i, Z = c + 3, Count = 50 }, cancellationToken).ConfigureAwait(false);
        }

        if (!Dyn.Contains(names, "rimgovernor/observations_list_supplies"))
        {
            throw new AcceptanceException("missing rimgovernor/observations_list_supplies in discovery");
        }

        var identityBefore = await h.WireAsync("identity-before", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, before) = Harness.Outcome(identityBefore, "loaded");
        if (before["paused"].AsBool() != true)
        {
            throw new AcceptanceException("fresh debug game did not start paused");
        }

        var beforeContext = before["context"].AsMap();
        var identity = beforeContext?["identity"].AsMap() ?? [];

        foreach (var includeHeld in new[] { true, false })
        {
            var label = includeHeld ? "held" : "spawned";
            var selected = new[] { "WoodLog", "Steel" };
            var defNames = new JsonArray();
            foreach (var name in selected)
            {
                defNames.Add(name);
            }

            var request = new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["filter"] = new JsonObject { ["defNames"] = defNames, ["ownership"] = "STOCK_OWNERSHIP_ALL", ["includeHeld"] = includeHeld },
            };
            var reply = await h.WireAsync(label + "-typed-census", "observations_list_supplies", request, cancellationToken).ConfigureAwait(false);
            var (_, observed) = Harness.Outcome(reply, "observed");
            if (!Dyn.DeepEqual(observed["context"], beforeContext))
            {
                throw new AcceptanceException($"{label}: context drifted mid-run");
            }

            var rows = observed["stocks"].AsSlice();
            if (rows.Count != selected.Length)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: expected {selected.Length} stock rows, found {rows.Count}"));
            }

            foreach (var raw in rows)
            {
                try
                {
                    CheckStock(raw.AsMap() ?? [], identity, includeHeld);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"{label}: {e.Message}", e);
                }
            }

            var checkedNames = new JsonArray();
            foreach (var name in selected)
            {
                checkedNames.Add(name);
            }

            report[label + "_definitions_checked"] = checkedNames;
        }

        var defaultRequest = new JsonObject
        {
            ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
            ["filter"] = new JsonObject { ["defNames"] = new JsonArray("WoodLog", "Steel") },
        };
        var defaultReply = await h.WireAsync("default-ownership-held", "observations_list_supplies", defaultRequest, cancellationToken).ConfigureAwait(false);
        var (_, defaultObserved) = Harness.Outcome(defaultReply, "observed");
        foreach (var raw in defaultObserved["stocks"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["ours"].AsNumber() <= 0)
            {
                throw new AcceptanceException("default read: expected a positive ours quantity");
            }
        }

        await CheckWeaponClassesAsync(h, identity, report, cancellationToken).ConfigureAwait(false);

        var identityAfterReply = await h.WireAsync("identity-after", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, after) = Harness.Outcome(identityAfterReply, "loaded");
        if (after["paused"].AsBool() != true)
        {
            throw new AcceptanceException("game unexpectedly resumed during the read pass");
        }

        if (!Dyn.DeepEqual(after["context"], beforeContext))
        {
            throw new AcceptanceException("identity/tick changed during a read-only pass");
        }
    }

    /// <summary>
    /// Asserts one typed ResourceStock row is internally consistent: every quantity a canonical count, spawned plus
    /// held units equal to the total, one item per stack with its CAS snapshot, and holders summing to the held units.
    /// </summary>
    private static void CheckStock(JsonObject row, JsonObject identity, bool includeHeld)
    {
        var definition = row["definition"].AsMap();
        var definitionText = row["definition"].ToJson();
        if (definition?["defName"].AsString().Length is null or 0)
        {
            throw new AcceptanceException($"stock row without a definition: {row.ToJson()}");
        }

        var keys = new List<string> { "units", "stacks", "ours", "oursUnforbidden", "forbidden", "otherFaction", "fogged", "reserved", "inStockpile", "inHomeArea" };
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            values[key] = Count(row[key], $"native stock {definitionText}.{key}");
        }

        if (values["units"] <= 0)
        {
            throw new AcceptanceException($"native stock {definitionText} has no units");
        }

        var spawned = Count(row["spawned"], $"spawned for {definitionText}");
        var units = Count(row["units"], $"units for {definitionText}");

        var playerFaction = Count(row["playerFaction"], $"playerFaction for {definitionText}");
        if (playerFaction > units || values["ours"] > units)
        {
            throw new AcceptanceException($"playerFaction or ours exceeds units for {definitionText}");
        }

        var stacks = Count(row["stacks"], $"stacks for {definitionText}");
        var items = row["items"].AsSlice();
        if (items.Count != stacks)
        {
            throw new AcceptanceException($"item count does not match stacks for {definitionText}");
        }

        // Not every loose supply item supports a CAS snapshot: NativeSupplyAllow.Snapshot returns null for a spawned
        // item that fails Eligible (e.g. missing CompForbiddable, as techprints and some other special items do), and
        // ListSupplies records that with an "items.snapshot"/UNSUPPORTED row issue instead of failing the read. Held
        // items always get a stateless CAS token from HeldSnapshot regardless, so only a snapshot-less item backed by
        // that documented gap is acceptable.
        var rowAllowsSnapshotless = NativeAssert.RequireIssueReason(row["issues"].AsSlice(), "items.snapshot", "UNAVAILABLE_REASON_UNSUPPORTED");
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in items)
        {
            var item = raw.AsMap() ?? [];
            var reference = item["item"].AsMap() ?? [];
            var id = reference["id"].AsString();
            if (id.Length == 0)
            {
                throw new AcceptanceException($"stock item missing id for {definitionText}");
            }

            if (!seenIds.Add(id))
            {
                throw new AcceptanceException($"duplicate stock item id {id} for {definitionText}");
            }

            try
            {
                NativeAssert.RequireSnapshot(item["snapshot"]);
            }
            catch (AcceptanceException e)
            {
                if (rowAllowsSnapshotless && item["snapshot"] is null)
                {
                    continue; // documented Eligible()-gated gap
                }

                throw new AcceptanceException($"stock item {id} missing a populated CAS snapshot: {e.Message}", e);
            }
        }

        var holders = row["holders"].AsSlice();
        if (includeHeld)
        {
            long total = 0;
            foreach (var raw in holders)
            {
                var holder = raw.AsMap() ?? [];
                var holderUnits = Count(holder["units"], $"holder units for {definitionText}");
                if (holderUnits <= 0)
                {
                    throw new AcceptanceException($"holder with non-positive units for {definitionText}");
                }

                if (holder["holder"].AsMap()?["id"].AsString().Length is null or 0)
                {
                    throw new AcceptanceException($"holder missing id for {definitionText}");
                }

                total += holderUnits;
            }

            if (spawned + total != units)
            {
                throw new AcceptanceException($"spawned plus holder units {spawned}+{total} does not equal units {units} for {definitionText}");
            }
        }
        else
        {
            if (holders.Count != 0)
            {
                throw new AcceptanceException($"holders populated without includeHeld for {definitionText}");
            }

            if (spawned != units)
            {
                throw new AcceptanceException($"spawned {spawned} does not equal units {units} without includeHeld for {definitionText}");
            }
        }
    }

    /// <summary>
    /// Proves the "weapons" census filters on IsWeapon, so a wood log lists as a makeshift equippable; the weapon class
    /// itself is a def fact the catalog carries, not a census field.
    /// </summary>
    private static async Task CheckWeaponClassesAsync(Harness h, JsonObject identity, Report report, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
            ["filter"] = new JsonObject { ["category"] = "STOCK_CATEGORY_WEAPONS", ["ownership"] = "STOCK_OWNERSHIP_ALL", ["includeHeld"] = true },
        };
        var reply = await h.WireAsync("weapons-census", "observations_list_supplies", request, cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var names = new JsonArray();
        foreach (var raw in observed["stocks"].AsSlice())
        {
            names.Add(raw.AsMap()?["definition"].AsMap()?["defName"].AsString() ?? "");
        }

        report["weapon_census"] = names;
        if (!names.Any(n => n?.GetValue<string>() == "WoodLog"))
        {
            throw new AcceptanceException($"WoodLog missing from the weapons census as a makeshift equippable: {names.ToJson()}");
        }
    }

    /// <summary>
    /// Asserts <paramref name="value"/> is a JSON integer quantity: a non-negative whole number within int64 range. An
    /// absent quantity is zero, which the wire omits; a string or a fraction fails.
    /// </summary>
    private static long Count(JsonNode? value, string what)
    {
        if (value is null)
        {
            return 0;
        }

        if (value is not JsonValue json || !json.TryGetValue<long>(out var n))
        {
            throw new AcceptanceException($"{what}: missing/non-integer quantity: {value.ToJson()}");
        }

        return n < 0 ? throw new AcceptanceException($"{what}: negative quantity: {value.ToJson()}") : n;
    }
}
