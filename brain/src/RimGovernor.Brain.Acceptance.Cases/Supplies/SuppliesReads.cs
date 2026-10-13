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

            if (!row.ContainsKey("carried"))
            {
                throw new AcceptanceException("default read: carried should be populated (includeHeld defaults true)");
            }

            if (!row.ContainsKey("inContainer"))
            {
                throw new AcceptanceException("default read: inContainer should be populated (includeHeld defaults true)");
            }
        }

        await CheckWeaponClassesAsync(h, identity, report, cancellationToken).ConfigureAwait(false);

        var invalid = new JsonObject
        {
            ["filter"] = new JsonObject { ["category"] = "unknown" },
            ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
        };
        var invalidReply = await h.WireAsync("bad-category", "observations_list_supplies", invalid, cancellationToken).ConfigureAwait(false);
        var code = NativeAssert.FailureCode(invalidReply);
        if (code != "FAILURE_CODE_INVALID_REQUEST")
        {
            throw new AcceptanceException($"bad-category: expected FAILURE_CODE_INVALID_REQUEST, got \"{code}\"");
        }

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
        if (includeHeld)
        {
            keys.AddRange(["carried", "inContainer", "traderStock"]);
        }

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            values[key] = Count(row[key], $"native stock {definitionText}.{key}");
        }

        if (values["units"] <= 0)
        {
            throw new AcceptanceException($"native stock {definitionText} has no units");
        }

        var held = values.GetValueOrDefault("carried") + values.GetValueOrDefault("inContainer");
        var spawned = Count(row["spawned"], $"spawned for {definitionText}");
        var units = Count(row["units"], $"units for {definitionText}");
        if (spawned + held != units)
        {
            throw new AcceptanceException($"spawned+held does not equal units for {definitionText}");
        }

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
            var id = item["id"].AsString();
            if (id.Length == 0)
            {
                throw new AcceptanceException($"stock item missing id for {definitionText}");
            }

            if (!seenIds.Add(id))
            {
                throw new AcceptanceException($"duplicate stock item id {id} for {definitionText}");
            }

            if ((int)item["mapId"].AsNumber() != (int)identity["mapId"].AsNumber())
            {
                throw new AcceptanceException($"stock item {id} has the wrong mapId");
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

            if (total != held)
            {
                throw new AcceptanceException($"holder unit total {total} does not match held {held} for {definitionText}");
            }
        }
        else
        {
            if (holders.Count != 0)
            {
                throw new AcceptanceException($"holders populated without includeHeld for {definitionText}");
            }

            foreach (var field in new[] { "carried", "inContainer", "traderStock" })
            {
                if (row.ContainsKey(field))
                {
                    throw new AcceptanceException($"{field} unexpectedly present without includeHeld for {definitionText}");
                }
            }

            foreach (var field in new[] { "carried", "in_container", "trader_stock" })
            {
                if (!NativeAssert.RequireIssueReason(row["issues"].AsSlice(), field, "UNAVAILABLE_REASON_NOT_REQUESTED"))
                {
                    throw new AcceptanceException($"missing NOT_REQUESTED issue for {field} on {definitionText}");
                }
            }
        }
    }

    /// <summary>
    /// Proves the "weapons" census carries each definition's Weapons-category membership and IsRangedWeapon /
    /// IsMeleeWeapon: the category is IsWeapon, so a wood log lists as a melee equippable that is no weapon by trade,
    /// and the Core def naming (Bow_/Gun_/Pila ranged, MeleeWeapon_ melee) that the planner used to infer from is here
    /// only the oracle for what the flags must say.
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
        var classes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in observed["stocks"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var name = row["definition"].AsMap()?["defName"].AsString() ?? "";
            var byTrade = row["weaponByTrade"].AsBool();
            var ranged = row["ranged"].AsBool();
            var melee = row["melee"].AsBool();
            if (byTrade is null || ranged is null || melee is null || ranged == melee)
            {
                throw new AcceptanceException($"weapons census row {name} lacks a consistent weapon class: weaponByTrade={row["weaponByTrade"].ToJson()} ranged={row["ranged"].ToJson()} melee={row["melee"].ToJson()}");
            }

            var cls = "makeshift";
            if (ranged == true && byTrade == true)
            {
                cls = "ranged";
            }
            else if (melee == true && byTrade == true)
            {
                cls = "melee";
            }

            var want = "makeshift";
            if ((name.Length > 4 && (name.StartsWith("Bow_", StringComparison.Ordinal) || name.StartsWith("Gun_", StringComparison.Ordinal))) || name == "Pila")
            {
                want = "ranged";
            }
            else if (name.Length > 12 && name.StartsWith("MeleeWeapon_", StringComparison.Ordinal))
            {
                want = "melee";
            }

            if (cls != want)
            {
                throw new AcceptanceException($"weapons census row {name} reports class {cls}, Core naming says {want}");
            }

            classes[name] = cls;
        }

        if (classes.GetValueOrDefault("WoodLog") != "makeshift")
        {
            throw new AcceptanceException($"WoodLog missing from the weapons census as a makeshift equippable: {string.Join(", ", classes.Select(pair => pair.Key + "=" + pair.Value))}");
        }

        var recorded = new JsonObject();
        foreach (var (name, cls) in classes)
        {
            recorded[name] = cls;
        }

        report["weapon_classes"] = recorded;
    }

    /// <summary>
    /// Asserts <paramref name="value"/> is a canonical ProtoJSON int64 quantity string: ASCII decimal digits only, no
    /// leading zero (except the literal "0"), no sign, and within int64 range. A missing quantity (null, a bare number
    /// or a malformed string) is never read as zero.
    /// </summary>
    private static long Count(JsonNode? value, string what)
    {
        if (value is not JsonValue json || !json.TryGetValue<string>(out var text))
        {
            throw new AcceptanceException($"{what}: missing/noncanonical ProtoJSON quantity: {value.ToJson()}");
        }

        if (text.Length == 0 || text.Any(ch => ch is < '0' or > '9'))
        {
            throw new AcceptanceException($"{what}: missing/noncanonical ProtoJSON quantity: {value.ToJson()}");
        }

        if (text != "0" && text[0] == '0')
        {
            throw new AcceptanceException($"{what}: noncanonical leading zero: {value.ToJson()}");
        }

        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            throw new AcceptanceException($"{what}: quantity is not a valid uint64: {value.ToJson()}");
        }

        return n > long.MaxValue ? throw new AcceptanceException($"{what}: quantity exceeds int64 max: {value.ToJson()}") : (long)n;
    }
}
