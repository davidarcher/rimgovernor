using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/strip</c> (Go <c>strip.go</c>).</summary>
internal static class CombatLabStrip
{
    private const string Apparel = "Apparel_Duster";

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/strip",
        Scope = "The generic strip op (#1117), a native op contract no snapshot can prove: on lab-open cut to one raider staged downed in a duster, "
            + "one Actions/Apply designate STRIP on the raider applies with Strip designation evidence, a second one refuses as already designated, "
            + "and within 2400 ticks a colonist strips the duster off through ordinary Hauling work. Who to strip is #1079.",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool, "rimgovernor/operations_apply"],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // stripKit keeps one raider, downed and wearing a duster.
    private static void Kit(LabFixture f, int cx, int cz)
    {
        var pawns = new List<LabPawn>();
        var raiders = 0;
        foreach (var p in f.Pawns)
        {
            if (p.Side == CombatFixtures.Hostile)
            {
                if (++raiders > 1)
                {
                    continue;
                }

                p.Downed = true;
                p.Apparel = Apparel;
            }

            pawns.Add(p);
        }

        f.Pawns = pawns;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var staged = await CombatStage.StageAsync(h, "lab-open", cancellationToken, Kit).ConfigureAwait(false);
        var hostiles = staged.Hostiles();
        if (hostiles.Count != 1)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(hostiles.Count)} hostiles, want 1");
        }

        var raider = hostiles[0];
        report["raider"] = raider;
        var worn = await ReadWornAsync(h, raider, cancellationToken).ConfigureAwait(false);
        if (!worn.Contains(Apparel))
        {
            throw new AcceptanceException($"raider does not wear {Apparel} before strip (<nil>): [{string.Join(' ', worn.Order(StringComparer.Ordinal))}]");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "strip-acquire", s.Identity, cancellationToken).ConfigureAwait(false);

        Task<JsonObject> ApplyAsync(string key) => ApplyIntents.ApplyOneAsync(
            h,
            key,
            s.Identity,
            key,
            new JsonObject { ["designate"] = new JsonObject { ["thingId"] = raider, ["designation"] = "THING_DESIGNATION_STRIP" } },
            cancellationToken);

        var first = await ApplyAsync("strip-1").ConfigureAwait(false);
        report["result"] = first.DeepClone();
        var applied = first["applied"].AsMap() ?? throw new AcceptanceException($"strip not applied: {first.ToJson()}");
        var effect = applied["observed"].AsMap() ?? [];
        var designation = effect["designation"].AsMap() ?? [];
        if (designation["designationDef"].AsString() != "Strip" || designation["present"].AsBool() != true || designation["thingId"].AsString() != raider)
        {
            throw new AcceptanceException($"strip evidence: want a present Strip designation on {raider}, got {applied.ToJson()}");
        }

        var second = await ApplyAsync("strip-2").ConfigureAwait(false);
        report["repeat"] = second.DeepClone();
        if (second["applied"].AsMap() is not null)
        {
            throw new AcceptanceException($"a second strip on a designated raider applied: {second.ToJson()}");
        }

        for (var step = 0; step < 40; step++)
        {
            var (_, tick) = await CombatStage.TickAsync(h, 60, cancellationToken).ConfigureAwait(false);
            worn = await ReadWornAsync(h, raider, cancellationToken).ConfigureAwait(false);
            if (!worn.Contains(Apparel))
            {
                report["stripped_tick"] = tick;
                return;
            }
        }

        throw new AcceptanceException($"raider still wears {Apparel} 2400 ticks after the strip designation");
    }

    // readWorn is one pawn's worn apparel defs from the lab read.
    private static async Task<HashSet<string>> ReadWornAsync(Harness h, string id, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("strip-worn", CombatFixtures.LabTool, new JsonObject { ["action"] = "read" }, cancellationToken).ConfigureAwait(false);
        foreach (var r in reply["pawns"].AsSlice())
        {
            var row = r.AsMap() ?? [];
            if (row["id"].AsString() != id)
            {
                continue;
            }

            var output = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in row["worn"].AsSlice())
            {
                if (w.AsMap() is { } item)
                {
                    _ = output.Add(item["def"].AsString());
                }
            }

            return output;
        }

        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"pawn {id} missing from the lab read"));
    }
}
