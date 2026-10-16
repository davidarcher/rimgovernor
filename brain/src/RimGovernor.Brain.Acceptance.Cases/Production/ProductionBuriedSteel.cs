using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/buried-steel</c> and <c>production/buried-steel-stockpile</c> (Go <c>buried_steel.go</c>): mines compacted
/// steel under a thick mountain roof. test/buried_steel raises a granite block east of the lab colonists with one two-cell
/// steel deposit on the face (bordering open ground) and one buried five cells in, fogged. The steel runway target
/// (recurring spend over the horizon) is short from zero, both deposits together hold less than it, so the economy/resource Need must
/// mine the face deposit as supported_roof and tunnel to the buried one and mine it too. Why not a snapshot test: the
/// roof-support verdict is native ExcavationSafety.Check reading the true map through fog, and the roof holding is vanilla
/// physics; only a live game shows the roof stay up.
/// </summary>
internal static class ProductionBuriedSteel
{
    private const string Op = "test/buried_steel";

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        yield return new AcceptanceCase
        {
            Name = "production/buried-steel",
            Scope = "The steel floor mines a face deposit and tunnels to a buried one under a thick rock roof; the steel runway goes positive, both deposits are mined and no roof collapses (#1075). Native: roof support through fog and vanilla collapse physics.",
            Start = new FixtureStart { Op = Op, Args = new JsonObject { ["action"] = "prepare" }, On = new LabStart { Colonists = 6 } },
            RequiredOps = [Op],
            QuietWorld = true,
            Serve = new ServeSpec { Families = ["resource", "supply"], NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "buried-steel" },
            Budget = TimeSpan.FromMinutes(14),
            Crew = new Crew { Size = 3 },
            Run = (s, ct) => RunAsync(s, false, ct),
        };

        // The stockpile against the face deposit: ore is always mined, and each face cell beside colony space is followed by a wall.
        yield return new AcceptanceCase
        {
            Name = "production/buried-steel-stockpile",
            Scope = "production/buried-steel with the stockpile beside the face deposit: the face is still mined and replacement walls are queued on the mined face cells (#1133).",
            Start = new FixtureStart { Op = Op, Args = new JsonObject { ["action"] = "prepare", ["stockpile"] = "face" }, On = new LabStart { Colonists = 6 } },
            RequiredOps = [Op],
            QuietWorld = true,
            Serve = new ServeSpec { Families = ["resource", "supply"], NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "buried-steel-stockpile" },
            Budget = TimeSpan.FromMinutes(14),
            Crew = new Crew { Size = 3 },
            Run = (s, ct) => RunAsync(s, true, ct),
        };
    }

    private static async Task RunAsync(ISession s, bool besideFace, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        report["fixture"] = prepared;
        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"fixture: {prepared.ToJson()}");
        }

        if (prepared["buriedFogged"].AsBool() != true)
        {
            throw new AcceptanceException($"the buried deposit is not fogged: {prepared.ToJson()}");
        }

        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                // Three in-game days: a face deposit, a five-cell granite corridor and a second deposit for three lab colonists.
                Watch = new WatchConfig { Watch = TimeSpan.FromMinutes(11), Window = 180000, Concern = EconomyFamily.Resource.Value },
                Prepare = async (h, rep, ct) =>
                {
                    var before = await h.CallAsync("audit-before", Op, new JsonObject { ["action"] = "audit" }, ct).ConfigureAwait(false);
                    rep["before"] = before;
                    if (before["steelOnMap"].AsNumber() != 0 || before["openLeft"].AsNumber() != 2 || before["buriedLeft"].AsNumber() != 2)
                    {
                        throw new AcceptanceException($"fixture did not start from zero steel and two intact deposits: {before.ToJson()}");
                    }
                },
                Audit = async (h, rep, ct) =>
                {
                    var after = await h.CallAsync("audit-after", Op, new JsonObject { ["action"] = "audit" }, ct).ConfigureAwait(false);
                    rep["after"] = after;
                    Verdict(after, besideFace);
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The case's assertion over the final audit.</summary>
    private static void Verdict(JsonObject after, bool besideFace)
    {
        var failures = new List<string>();
        if (besideFace && after["replacementWalls"].AsNumber() != 2)
        {
            failures.Add($"face beside the stockpile not walled: {after["replacementWalls"].ToJson()} of 2 cells");
        }

        if (after["roofless"].AsNumber() + after["collapsedRocks"].AsNumber() + after["collapsing"].AsNumber() != 0)
        {
            failures.Add($"roof collapse: roofless={after["roofless"].ToJson()} collapsedRocks={after["collapsedRocks"].ToJson()} collapsing={after["collapsing"].ToJson()}");
        }

        if (after["openLeft"].AsNumber() != 0)
        {
            failures.Add($"face deposit not mined: {after["openLeft"].ToJson()} cells left");
        }

        if (after["buriedLeft"].AsNumber() != 0)
        {
            failures.Add($"buried deposit not mined: {after["buriedLeft"].ToJson()} cells left");
        }

        if (after["steelStored"].AsNumber() <= 0)
        {
            failures.Add($"steel runway did not go positive: stored={after["steelStored"].ToJson()} onMap={after["steelOnMap"].ToJson()}");
        }

        if (failures.Count > 0)
        {
            throw new AcceptanceException($"[{string.Join(", ", failures)}] (audit {after.ToJson()})");
        }
    }
}
