using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The waste/burnable-filters case verifies the RimGovernorBurnable and RimGovernorNotBurnable special filters
/// against real stockpile semantics and RimWorld's own hauling (Go <c>burnable.go</c>).
/// </summary>
internal static class WasteBurnable
{
    private const string StageTool = "test/burnable_stage";
    private const string ReadTool = "test/burnable_read";

    /// <summary>Names the zone each staged item with a fixed verdict must end in; gear probes are judged by MarketValue instead.</summary>
    private static readonly Dictionary<string, string> Expect = new(StringComparer.Ordinal)
    {
        ["potatoes_rotted"] = "dump",
        ["potatoes_fresh"] = "store",
        ["corpse_animal_rotted"] = "dump",
        ["corpse_animal_fresh"] = "store",
        ["corpse_stranger_rotted"] = "dump",
        ["corpse_colonist_rotted"] = "store",
        ["revolver_normal"] = "store",
        ["steel_stack"] = "store",
    };

    internal static AcceptanceCase Case() => new()
    {
        Name = "waste/burnable-filters",
        Scope = "Special thing filters RimGovernorBurnable and RimGovernorNotBurnable on real stockpile zones: a Low zone " +
            "disallowing not-burnable beside a Normal zone disallowing burnable, with one colonist hauling, so " +
            "RimWorld's own hauling puts rotted rottables and worn-out gear only in the Low zone and everything else " +
            "(fresh rottables, a rotted colonist corpse, valuable gear, a resource stack) in the Normal one. A Go " +
            "snapshot test cannot cover it: the verdict is vanilla ThingFilter and hauling semantics over live Things, " +
            "and whether MarketValue folds in hit points, quality and tainted is a read of the game's own stat, " +
            "recorded in the report.",
        Start = new FixtureStart { Op = StageTool, On = new LabStart() },
        RequiredOps = [ReadTool],
        QuietWorld = true,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static string ZoneOf(JsonObject row) => row["priority"].AsString() switch
    {
        "Low" => "dump",
        "Normal" => "store",
        _ => "",
    };

    private static string Bool(bool value) => value ? "true" : "false";

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var prepared = s.Prepared ?? [];
        var items = new List<(string Label, string Id)>();
        foreach (var raw in prepared["items"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var item = (Label: row["label"].AsString(), Id: row["id"].AsString());
            if (item.Label.Length == 0 || item.Id.Length == 0)
            {
                throw new AcceptanceException($"stage: bad item row {row.ToJson()}");
            }

            items.Add(item);
        }

        if (items.Count == 0)
        {
            throw new AcceptanceException($"stage: no items: {prepared.ToJson()}");
        }

        var ids = string.Join(',', items.Select(i => i.Id));
        var cutoff = prepared["cutoff"].AsNumber();
        var h = s.Harness;

        async Task<SortedDictionary<string, JsonObject>> Read(string label)
        {
            var reply = await h.CallAsync(label, ReadTool, new JsonObject { ["ids"] = ids }, cancellationToken).ConfigureAwait(false);
            var byLabel = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
            var rows = reply["rows"].AsSlice();
            if (rows.Count != items.Count)
            {
                throw new AcceptanceException($"{label}: want {items.Count} rows, got {reply.ToJson()}");
            }

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i].AsMap() ?? [];
                if (row["present"].AsBool() != true)
                {
                    throw new AcceptanceException($"{label}: item {items[i].Label} ({items[i].Id}) is gone");
                }

                byLabel[items[i].Label] = row;
            }

            return byLabel;
        }

        // Run game time until every item stands in a zone, or give up after less than one day (a fresh corpse starts
        // rotting after a few).
        SortedDictionary<string, JsonObject> rows;
        for (var advanced = 0; ; advanced += 600)
        {
            rows = await Read(string.Create(CultureInfo.InvariantCulture, $"burnable-read-{advanced}")).ConfigureAwait(false);
            var stored = rows.Values.Count(row => ZoneOf(row).Length != 0);
            if (stored == rows.Count)
            {
                break;
            }

            if (advanced >= 40000)
            {
                throw new AcceptanceException($"after {advanced} ticks only {stored} of {rows.Count} items are stored: {ZoneRows(rows).ToJson()}");
            }

            _ = await s.AdvanceAsync(600, null, cancellationToken).ConfigureAwait(false);
        }

        s.Report["placement"] = ZoneRows(rows);

        // The filter verdict and the zones' own admission agree with the placement: the dump (Low) admits only
        // burnable, the store (Normal) only not-burnable.
        foreach (var (label, row) in rows)
        {
            var zone = ZoneOf(row);
            var burnable = row["burnable"].AsBool() == true;
            var notBurnable = row["notBurnable"].AsBool() == true;
            if (burnable == notBurnable && !label.StartsWith("steel", StringComparison.Ordinal))
            {
                throw new AcceptanceException($"{label}: filters are not complements: burnable {Bool(burnable)}, notBurnable {Bool(notBurnable)}");
            }

            if ((zone == "dump" && !burnable) || (zone == "store" && burnable))
            {
                throw new AcceptanceException($"{label}: stored in the {zone} but burnable={Bool(burnable)}: {row.ToJson()}");
            }

            if (row["zoneAllows"].AsBool() != true)
            {
                throw new AcceptanceException($"{label}: its zone does not admit it: {row.ToJson()}");
            }

            if (Expect.TryGetValue(label, out var want) && zone != want)
            {
                throw new AcceptanceException($"{label}: want the {want}, got the \"{zone}\": {row.ToJson()}");
            }
        }

        // A resource stack is outside both filters (CanEverMatch false), so neither disallow rejects it.
        var steel = rows.GetValueOrDefault("steel_stack") ?? [];
        if (steel["burnable"].AsBool() == true)
        {
            throw new AcceptanceException($"steel_stack: burnable: {steel.ToJson()}");
        }

        if (steel["notBurnable"].AsBool() == true)
        {
            throw new AcceptanceException($"steel_stack: notBurnable matches a def it can never judge: {steel.ToJson()}");
        }

        // Gear is judged by MarketValue below the cutoff, whatever folds into it.
        var gear = new SortedDictionary<string, double>(StringComparer.Ordinal);
        int burnableGear = 0, keptGear = 0;
        foreach (var (label, row) in rows)
        {
            var def = row["def"].AsString();
            if (def is not ("Apparel_Pants" or "MeleeWeapon_Club" or "Gun_Revolver"))
            {
                continue;
            }

            var value = row["marketValue"].AsNumber();
            gear[label] = value;
            var burnable = row["burnable"].AsBool() == true;
            if (burnable != (value < cutoff))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: MarketValue {value:F2} against cutoff {cutoff:F2} but burnable={Bool(burnable)}"));
            }

            if (burnable)
            {
                burnableGear++;
            }
            else
            {
                keptGear++;
            }
        }

        if (burnableGear == 0 || keptGear == 0)
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"gear probes did not straddle the {cutoff:F2} cutoff ({burnableGear} burnable, {keptGear} kept): {string.Join(' ', gear.Select(kv => $"{kv.Key}:{kv.Value}"))}"));
        }

        // MarketValue folds in a factor when the one-factor probe is cheaper than the plain pants.
        var plain = gear.GetValueOrDefault("pants_normal");
        var gearJson = new JsonObject();
        foreach (var (label, value) in gear)
        {
            gearJson[label] = value;
        }

        s.Report["marketValue"] = new JsonObject
        {
            ["cutoff"] = cutoff,
            ["gear"] = gearJson,
            ["foldsHitPoints"] = gear.GetValueOrDefault("pants_low_hp") < plain,
            ["foldsQuality"] = gear.GetValueOrDefault("pants_awful") < plain,
            ["foldsTainted"] = gear.GetValueOrDefault("pants_tainted") < plain,
        };
    }

    /// <summary>Each item's label, zone and market value for the report.</summary>
    private static JsonObject ZoneRows(SortedDictionary<string, JsonObject> rows)
    {
        var output = new JsonObject();
        foreach (var (label, row) in rows)
        {
            output[label] = new JsonObject
            {
                ["zone"] = ZoneOf(row),
                ["marketValue"] = row["marketValue"]?.DeepClone(),
                ["rotStage"] = row["rotStage"]?.DeepClone(),
            };
        }

        return output;
    }
}
