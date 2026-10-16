using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>storage/occupancy_ring</c> (#3234): the native occupancy ring samples an owned stockpile's held stacks every
/// game hour and keeps them across a save and reload. A unit test cannot prove it: the samples come from the native
/// map component's hourly tick over the real haul-destination groups, and survival from the game's own save.
/// </summary>
internal static class StorageOccupancyRing
{
    private const string Save = "RimGovernor-occupancy-ring";

    internal static AcceptanceCase Case() => new()
    {
        Name = "storage/occupancy_ring",
        Scope = "Native occupancy ring on the lab: a one-cell stockpile holding a Steel stack is sampled at each hour's first tick "
            + "over 6000 synchronous ticks (at least two samples, the last one stack), and the same samples read back after a save and reload.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(5),
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
            "occupancy-zone",
            "operations_apply",
            new JsonObject
            {
                ["identity"] = identity.DeepClone(),
                ["actions"] = new JsonArray(new JsonObject
                {
                    ["key"] = "occupancy-stockpile",
                    ["zone"] = new JsonObject
                    {
                        ["label"] = "Occupancy store",
                        ["kind"] = "ZONE_TYPE_STOCKPILE",
                        ["addCells"] = new JsonObject
                        {
                            ["rectangle"] = new JsonObject { ["origin"] = new JsonObject { ["x"] = store.X, ["z"] = store.Z }, ["width"] = 1, ["height"] = 1 },
                        },
                        ["stockpile"] = new JsonObject
                        {
                            ["priority"] = "STORAGE_PRIORITY_NORMAL",
                            ["preset"] = "FILTER_PRESET_NOTHING",
                            ["filter"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["thingDef"] = "Steel" }) },
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
            throw new AcceptanceException($"occupancy-zone: expected one created stockpile: {reply.ToJson()}");
        }

        s.Report["zone"] = zone;
        _ = await DebugGame.LabSpawnAsync(h, new LabThing { Def = "Steel", X = store.X, Z = store.Z, Count = 50 }, cancellationToken).ConfigureAwait(false);

        // 6000 ticks cross at least two hour boundaries (2500 ticks each).
        for (var i = 0; i < 3; i++)
        {
            _ = await CombatStage.TickAsync(h, 2000, cancellationToken).ConfigureAwait(false);
        }

        var before = await SamplesAsync(h, zone, "occupancy-before", cancellationToken).ConfigureAwait(false);
        s.Report["samples_before"] = Render(before);
        if (before.Count < 2)
        {
            throw new AcceptanceException($"expected at least two hourly samples of {zone}, got [{Render(before)}]");
        }

        if (before[^1].Stacks != 1)
        {
            throw new AcceptanceException($"the last sample of {zone} holds {before[^1].Stacks} stacks, want the one Steel stack");
        }

        _ = await h.CallAsync("save-occupancy", "rimgovernor/save_game", new JsonObject { ["saveName"] = Save }, cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("reload-occupancy", "rimgovernor/load_game_ready", new JsonObject
        {
            ["saveName"] = Save,
            ["readiness"] = "visual",
            ["timeoutMs"] = 90000,
            ["ignoreModCompatibility"] = false,
        }, cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("pause-occupancy", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);

        var after = await SamplesAsync(h, zone, "occupancy-after", cancellationToken).ConfigureAwait(false);
        s.Report["samples_after"] = Render(after);
        if (after.Count < before.Count || !after.Take(before.Count).SequenceEqual(before))
        {
            throw new AcceptanceException($"the reload lost samples of {zone}: before [{Render(before)}], after [{Render(after)}]");
        }
    }

    private static async Task<List<(int Hour, int Stacks)>> SamplesAsync(Harness h, string zone, string label, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "observations_read_storage_occupancy",
            new JsonObject { ["groups"] = new JsonArray(new JsonObject { ["id"] = zone }) },
            cancellationToken).ConfigureAwait(false);
        var snapshot = Harness.Outcome(reply, "observed").Value;
        var samples = new List<(int Hour, int Stacks)>();
        foreach (var group in snapshot["groups"].AsSlice())
        {
            if (group.AsMap()?["group"].AsMap()?["id"].AsString() != zone)
            {
                continue;
            }

            foreach (var sample in group.AsMap()?["samples"].AsSlice() ?? [])
            {
                samples.Add(((int)sample.AsMap()!["hour"].AsNumber(), (int)sample.AsMap()!["stacks"].AsNumber()));
            }
        }

        return samples;
    }

    private static string Render(List<(int Hour, int Stacks)> samples) =>
        string.Join(", ", samples.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Hour}:{x.Stacks}")));
}
