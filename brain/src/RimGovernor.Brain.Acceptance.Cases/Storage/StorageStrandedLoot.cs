using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>storage/stranded_loot</c> (#3235): a loose stack no storage has room for is reported with the storage group
/// whose filter accepts it. A unit test cannot prove it: the accepting ids come from native storage settings
/// (<c>ISlotGroupParent.Accepts</c>) and the headroom from the native zone grid.
/// </summary>
internal static class StorageStrandedLoot
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "storage/stranded_loot",
        Scope = "Native loot census on the lab: a one-cell stockpile accepting Steel and WoodLog holds a Steel stack, so a loose "
            + "unforbidden WoodLog stack reads zero storage headroom and names that zone in acceptedBy; read-only, the game stays paused.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var c = LabWorld.LabMapSize / 2;
        var store = (X: c + 8, Z: c + 3);

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);
        var reply = await h.WireAsync(
            "stranded-zone",
            "operations_apply",
            new JsonObject
            {
                ["identity"] = identity.DeepClone(),
                ["actions"] = new JsonArray(new JsonObject
                {
                    ["key"] = "stranded-stockpile",
                    ["zone"] = new JsonObject
                    {
                        ["label"] = "Stranded store",
                        ["kind"] = "ZONE_TYPE_STOCKPILE",
                        ["addCells"] = new JsonObject
                        {
                            ["rectangle"] = new JsonObject { ["origin"] = new JsonObject { ["x"] = store.X, ["z"] = store.Z }, ["width"] = 1, ["height"] = 1 },
                        },
                        ["stockpile"] = new JsonObject
                        {
                            ["priority"] = "STORAGE_PRIORITY_NORMAL",
                            ["preset"] = "FILTER_PRESET_NOTHING",
                            ["filter"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["thingDef"] = "Steel" }, new JsonObject { ["thingDef"] = "WoodLog" }) },
                        },
                    },
                }),
            },
            cancellationToken).ConfigureAwait(false);
        var results = reply["results"].AsSlice();
        var effect = results.Count == 1 ? CaseSupport.AppliedEffect(results[0].AsMap() ?? [], "zone") : null;
        var created = effect?["created"].AsSlice() ?? [];
        var zone = created.Count == 1 ? created[0].AsMap()?["zoneId"].AsString() ?? "" : "";
        if (zone.Length == 0)
        {
            throw new AcceptanceException($"stranded-zone: expected one created stockpile: {reply.ToJson()}");
        }

        // Steel fills the only cell; WoodLog cannot stack on it, so the zone accepts WoodLog with no room for it.
        _ = await DebugGame.LabSpawnAsync(h, new LabThing { Def = "Steel", X = store.X, Z = store.Z, Count = 50 }, cancellationToken).ConfigureAwait(false);
        var (wood, _) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = "WoodLog", X = c + 8, Z = c + 6, Count = 20 }, cancellationToken).ConfigureAwait(false);

        var observed = await NativeReads.ColonyFactsAsync(s, "stranded-facts", false, cancellationToken).ConfigureAwait(false);
        var section = observed["eventLoot"].AsMap() ?? throw new AcceptanceException("colony facts carry no eventLoot section");
        var census = Harness.Outcome(section, "observed").Value;
        JsonObject? row = null;
        foreach (var raw in census["items"].AsSlice())
        {
            if (raw.AsMap()?["item"].AsMap()?["id"].AsString() == wood)
            {
                row = raw.AsMap();
            }
        }

        if (row is null)
        {
            throw new AcceptanceException($"the loot census lists no row for the WoodLog stack {wood}: {census.ToJson()}");
        }

        s.Report["wood_row"] = row.DeepClone();
        if (row["forbidden"].AsBool() != false || row["safeToHaul"].AsBool() != true)
        {
            throw new AcceptanceException($"the WoodLog stack is not an unforbidden safe stack: {row.ToJson()}");
        }

        if (row["storageHeadroom"].AsNumber() != 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the WoodLog stack reads storage headroom {row["storageHeadroom"].ToJson()}, want 0"));
        }

        var accepted = row["acceptedBy"].AsSlice().Select(r => r.AsMap()?["id"].AsString() ?? "").ToList();
        if (!accepted.Contains(zone))
        {
            throw new AcceptanceException($"the stranded WoodLog stack names [{string.Join(", ", accepted)}], not the accepting zone {zone}");
        }

        s.Report["accepting_zone"] = zone;
    }
}
