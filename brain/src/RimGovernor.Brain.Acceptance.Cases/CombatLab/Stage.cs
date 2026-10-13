using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// A fixture as test/lab_stage read it back: each pawn's load id in spec order and the name-free map digest
/// (Go <c>combatlab.Staged</c>).
/// </summary>
internal sealed class StagedFixture
{
    internal required LabFixture Fixture { get; init; }

    internal List<JsonObject> Pawns { get; } = [];

    /// <summary>The staged things' load ids, spec order.</summary>
    internal List<string> Things { get; } = [];

    internal string Digest { get; init; } = "";

    internal List<string> Rows { get; } = [];

    internal string Faction { get; init; } = "";

    /// <summary>The seed test/lab_stage echoed back.</summary>
    internal int Seed { get; init; }

    /// <summary>The lab wipe's centre the fixture was built around.</summary>
    internal LabCell Center { get; init; }

    /// <summary>The staged colonists' load ids, spec order.</summary>
    internal List<string> Colonists() => Ids(CombatFixtures.Colonist);

    /// <summary>The staged hostiles' load ids, spec order.</summary>
    internal List<string> Hostiles() => Ids(CombatFixtures.Hostile);

    /// <summary>The staged player animals' load ids, spec order.</summary>
    internal List<string> Animals() => Ids(CombatFixtures.Animal);

    /// <summary>The staged manhunters' load ids, spec order.</summary>
    internal List<string> Manhunters() => Ids(CombatFixtures.Manhunter);

    /// <summary>The staged mechanoids' load ids, spec order.</summary>
    internal List<string> Mechs() => Ids(CombatFixtures.Mech);

    internal List<string> Ids(string side)
    {
        var output = new List<string>();
        for (var i = 0; i < Fixture.Pawns.Count; i++)
        {
            if (Fixture.Pawns[i].Side == side)
            {
                output.Add(Pawns[i]["id"].AsString());
            }
        }

        return output;
    }
}

/// <summary>One pawn as test/lab_stage action read reports it.</summary>
internal readonly record struct LabPosition(string Id, string Side, int X, int Z, bool Downed, bool Dead);

/// <summary>
/// Staging (Go <c>combatlab/stage.go</c>): wipes the lab to a fixture's colonists, builds it around the wipe's centre
/// and stages it, then reads the lab back.
/// </summary>
internal static class CombatStage
{
    /// <summary>An edit that stages the fixture under <paramref name="seed"/> (see <see cref="LabFixture.Seed"/>).</summary>
    internal static Action<LabFixture, int, int> WithSeed(int seed) => (f, _, _) => f.Seed = seed;

    /// <summary>
    /// Wipes the lab to the fixture's colonists, builds it around the wipe's centre and stages it, checking every pawn
    /// read back where the spec put it, armed as specified, hostile under an assault lord. An edit adapts the named
    /// fixture for one case before staging, around the lab centre; it may change Colonists.
    /// </summary>
    internal static async Task<StagedFixture> StageAsync(Harness h, string name, CancellationToken cancellationToken, params Action<LabFixture, int, int>[] edits)
    {
        var probe = CombatFixtures.Build(name, 0, 0);
        foreach (var edit in edits)
        {
            edit(probe, 0, 0);
        }

        var wipe = await h.CallAsync("combatlab-wipe-" + name, DebugGame.LabStartTool, new JsonObject { ["colonists"] = probe.Colonists }, cancellationToken).ConfigureAwait(false);
        if (wipe["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{DebugGame.LabStartTool} wipe for {name} refused: {wipe.ToJson()}");
        }

        var center = wipe["center"].AsMap() ?? [];
        var cx = (int)center["x"].AsNumber();
        var cz = (int)center["z"].AsNumber();
        var f = CombatFixtures.Build(name, cx, cz);
        foreach (var edit in edits)
        {
            edit(f, cx, cz);
        }

        var spec = f.ToJson().ToJsonString();
        var reply = await h.CallAsync("combatlab-stage-" + name, CombatFixtures.StageTool, new JsonObject { ["spec"] = spec }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{CombatFixtures.StageTool} {name} refused: {reply.ToJson()}");
        }

        var rows = reply["pawns"].AsSlice();
        if (rows.Count != f.Pawns.Count)
        {
            throw new AcceptanceException($"{CombatFixtures.StageTool} {name}: {rows.Count} pawns read back, want {f.Pawns.Count}");
        }

        var seed = (int)reply["seed"].AsNumber();
        var output = new StagedFixture
        {
            Fixture = f,
            Digest = reply["digest"].AsString(),
            Faction = reply["faction"].AsString(),
            Seed = seed,
            Center = new LabCell(cx, cz),
        };
        if (output.Seed != f.Seed)
        {
            throw new AcceptanceException($"{CombatFixtures.StageTool} {name}: staged under seed {output.Seed}, want {f.Seed}");
        }

        foreach (var r in reply["digestRows"].AsSlice())
        {
            output.Rows.Add(r.AsString());
        }

        for (var i = 0; i < f.Pawns.Count; i++)
        {
            var row = rows[i].AsMap() ?? [];
            var lord = f.Siege is not null ? "LordJob_Siege" : "LordJob_AssaultColony";
            try
            {
                CheckPawn(f.Pawns[i], row, lord);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{name} pawn {i}: {e.Message}", e);
            }

            output.Pawns.Add(row);
        }

        var things = reply["things"].AsSlice();
        if (things.Count != f.Things.Count)
        {
            throw new AcceptanceException($"{CombatFixtures.StageTool} {name}: {things.Count} things staged, want {f.Things.Count}");
        }

        foreach (var t in things)
        {
            output.Things.Add((t.AsMap() ?? [])["id"].AsString());
        }

        return output;
    }

    /// <summary>
    /// Sets the game's RNG from <paramref name="seed"/> through test/lab_stage action reseed (#2849) and returns the next
    /// draws the native read without consuming them.
    /// </summary>
    internal static async Task<List<int>> ReseedAsync(Harness h, int seed, CancellationToken cancellationToken)
    {
        if (seed == 0)
        {
            throw new AcceptanceException("reseed needs a non-zero seed");
        }

        var spec = new JsonObject { ["seed"] = seed }.ToJsonString();
        var reply = await h.CallAsync(
            "combatlab-reseed-" + seed.ToString(CultureInfo.InvariantCulture),
            CombatFixtures.StageTool,
            new JsonObject { ["action"] = "reseed", ["spec"] = spec },
            cancellationToken).ConfigureAwait(false);
        return ParseReseed(reply, seed);
    }

    /// <summary>Checks a reseed reply echoed the seed and carried draws.</summary>
    internal static List<int> ParseReseed(JsonObject reply, int seed)
    {
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{CombatFixtures.StageTool} reseed {seed} refused: {reply.ToJson()}");
        }

        var got = (int)reply["seed"].AsNumber();
        if (got != seed)
        {
            throw new AcceptanceException($"{CombatFixtures.StageTool} reseeded under {got}, want {seed}");
        }

        var draws = new List<int>();
        foreach (var d in reply["draws"].AsSlice())
        {
            draws.Add((int)d.AsNumber());
        }

        return draws.Count == 0
            ? throw new AcceptanceException($"{CombatFixtures.StageTool} reseed {seed} read back no draws: {reply.ToJson()}")
            : draws;
    }

    /// <summary>Returns every pawn on the lab with its cell, and the game tick.</summary>
    internal static Task<(Dictionary<string, LabPosition> Pawns, int Tick)> ReadAsync(Harness h, CancellationToken cancellationToken) =>
        ReadCoreAsync(h, new JsonObject { ["action"] = "read" }, cancellationToken);

    /// <summary>
    /// Runs <paramref name="n"/> synchronous game ticks on the paused lab (no clock, no safety stops: the fixture's
    /// hostiles are the point), then reads.
    /// </summary>
    internal static Task<(Dictionary<string, LabPosition> Pawns, int Tick)> TickAsync(Harness h, int n, CancellationToken cancellationToken) =>
        ReadCoreAsync(h, new JsonObject { ["action"] = "tick", ["ticks"] = n }, cancellationToken);

    private static async Task<(Dictionary<string, LabPosition> Pawns, int Tick)> ReadCoreAsync(Harness h, JsonObject args, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("combatlab-" + args["action"].AsString(), CombatFixtures.StageTool, args, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{CombatFixtures.StageTool} read refused: {reply.ToJson()}");
        }

        var output = new Dictionary<string, LabPosition>(StringComparer.Ordinal);
        foreach (var r in reply["pawns"].AsSlice())
        {
            var row = r.AsMap() ?? [];
            var p = new LabPosition(
                row["id"].AsString(),
                row["side"].AsString(),
                (int)row["x"].AsNumber(),
                (int)row["z"].AsNumber(),
                row["downed"].AsBool() == true,
                row["dead"].AsBool() == true);
            output[p.Id] = p;
        }

        return (output, (int)reply["tick"].AsNumber());
    }

    /// <summary>The shortest squared distance between a live hostile and a live colonist in pawns; -1 when either side has none.</summary>
    internal static int Gap(Dictionary<string, LabPosition> pawns)
    {
        var best = -1;
        foreach (var h in pawns.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (h.Side != CombatFixtures.Hostile || h.Dead)
            {
                continue;
            }

            foreach (var c in pawns.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                if (c.Side != CombatFixtures.Colonist || c.Dead)
                {
                    continue;
                }

                var d = ((h.X - c.X) * (h.X - c.X)) + ((h.Z - c.Z) * (h.Z - c.Z));
                if (best < 0 || d < best)
                {
                    best = d;
                }
            }
        }

        return best;
    }

    /// <summary>The digest rows only one of two stagings has.</summary>
    internal static string RowDiff(IEnumerable<string> a, IEnumerable<string> b)
    {
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in a)
        {
            count[r] = count.GetValueOrDefault(r) + 1;
        }

        foreach (var r in b)
        {
            count[r] = count.GetValueOrDefault(r) - 1;
        }

        var only = new List<string>();
        foreach (var (r, n) in count)
        {
            if (n > 0)
            {
                only.Add("first only " + r);
            }
            else if (n < 0)
            {
                only.Add("second only " + r);
            }
        }

        only.Sort(StringComparer.Ordinal);
        return string.Join("; ", only);
    }

    private static void CheckPawn(LabPawn want, JsonObject got, string lord)
    {
        var wantX = want.X;
        var wantZ = want.Z;

        // A pod hostile lands where its arrival mode drops the pod.
        if (got["inPod"].AsBool() == true)
        {
            wantX = (int)got["x"].AsNumber();
            wantZ = (int)got["z"].AsNumber();
        }

        var j = want.Jitter;
        var x = (int)got["x"].AsNumber();
        var z = (int)got["z"].AsNumber();
        if (x < wantX - j || x > wantX + j || z < wantZ - j || z > wantZ + j)
        {
            throw new AcceptanceException($"at {x},{z}, want {wantX},{wantZ}: {got.ToJson()}");
        }

        if (got["side"].AsString() != want.Side)
        {
            throw new AcceptanceException($"side {got["side"].AsString()}, want {want.Side}");
        }

        if (got["weapon"].AsString() != want.Weapon)
        {
            throw new AcceptanceException($"weapon {got["weapon"].AsString()}, want {want.Weapon}");
        }

        var hostile = got["hostile"].AsBool() == true;
        var wantHostile = want.Side is CombatFixtures.Hostile or CombatFixtures.Manhunter or CombatFixtures.Mech;
        if (hostile != wantHostile)
        {
            throw new AcceptanceException($"hostile {hostile} for side {want.Side}");
        }

        if (want.Side == CombatFixtures.Hostile)
        {
            if (got["lordJob"].AsString() != lord)
            {
                throw new AcceptanceException($"lord job {got["lordJob"].AsString()}, want {lord}");
            }

            var n = (int)got["apparel"].AsNumber();
            if (n != 0)
            {
                throw new AcceptanceException($"hostile wears {n} apparel, want none");
            }
        }

        var health = got["health"].AsNumber();
        if (health < 0.999)
        {
            throw new AcceptanceException($"health {health.ToString("0.000", CultureInfo.InvariantCulture)}, want full");
        }
    }
}
