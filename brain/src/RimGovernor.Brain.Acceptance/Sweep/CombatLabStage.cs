using System.Globalization;
using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// A fixture as test/stage read it back: each pawn in spec order and the name-free map digest.
/// </summary>
public sealed class StagedFixture
{
    public required LabFixture Fixture { get; init; }

    public List<Fx.StagedPawn> Pawns { get; } = [];

    /// <summary>The staged things' load ids, spec order.</summary>
    public List<string> Things { get; } = [];

    /// <summary>The player's turret guns the fixture's things staged, with their damage per second and power.</summary>
    public List<Fx.StagedGun> Guns { get; } = [];

    public string Digest { get; init; } = "";

    /// <summary>The staged colonists' load ids, spec order.</summary>
    public List<string> Colonists() => Ids(CombatFixtures.Colonist);

    /// <summary>The staged hostiles' load ids, spec order.</summary>
    public List<string> Hostiles() => Ids(CombatFixtures.Hostile);

    /// <summary>The staged manhunters' load ids, spec order.</summary>
    public List<string> Manhunters() => Ids(CombatFixtures.Manhunter);

    /// <summary>The staged mechanoids' load ids, spec order.</summary>
    public List<string> Mechs() => Ids(CombatFixtures.Mech);

    public List<string> Ids(string side)
    {
        var output = new List<string>();
        for (var i = 0; i < Fixture.Pawns.Count; i++)
        {
            if (Fixture.Pawns[i].Side == side)
            {
                output.Add(Pawns[i].Id);
            }
        }

        return output;
    }
}

/// <summary>
/// Staging: wipes the lab to a fixture's colonists, builds it around the wipe's centre
/// and stages it, then reads the lab back.
/// </summary>
public static class CombatStage
{
    /// <summary>
    /// Wipes the lab to the fixture's colonists, builds it around the wipe's centre and stages it, checking every pawn
    /// read back where the spec put it, armed as specified, hostile under an assault lord. An edit adapts the named
    /// fixture for one case before staging, around the lab centre; it may change Colonists.
    /// </summary>
    public static async Task<StagedFixture> StageAsync(Harness h, string name, CancellationToken cancellationToken, params Action<LabFixture, int, int>[] edits)
    {
        var probe = CombatFixtures.Build(name, 0, 0);
        foreach (var edit in edits)
        {
            edit(probe, 0, 0);
        }

        var wipe = await h.CallAsync("combatlab-wipe-" + name, DebugGame.LabStartTool, new JsonObject { { "colonists", probe.Colonists } }, cancellationToken).ConfigureAwait(false);
        var wiped = CombatServe.Typed<Fx.CombatLabWipe>(DebugGame.LabStartTool, wipe);
        if (!wiped.Success)
        {
            throw new AcceptanceException($"{DebugGame.LabStartTool} wipe for {name} refused: {wipe.ToJson()}");
        }

        var (cx, cz) = (wiped.Center.X, wiped.Center.Z);
        var f = CombatFixtures.Build(name, cx, cz);
        foreach (var edit in edits)
        {
            edit(f, cx, cz);
        }

        var reply = await FixtureStage.ApplyAsync(h, "combatlab-stage-" + name, f.ToSpec(), cancellationToken).ConfigureAwait(false);
        if (reply.Pawns.Count != f.Pawns.Count)
        {
            throw new AcceptanceException($"{FixtureStage.Tool} {name}: {reply.Pawns.Count} pawns read back, want {f.Pawns.Count}");
        }

        var output = new StagedFixture { Fixture = f, Digest = reply.Digest };
        var lord = f.Siege is not null ? "LordJob_Siege" : "LordJob_AssaultColony";
        for (var i = 0; i < f.Pawns.Count; i++)
        {
            var row = reply.Pawns.GetValueOrDefault(LabFixture.PawnName(i))
                ?? throw new AcceptanceException($"{FixtureStage.Tool} {name}: pawn {i} not read back");
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

        for (var i = 0; i < f.Things.Count; i++)
        {
            var key = LabFixture.ThingName(i);
            var made = reply.Buildings.GetValueOrDefault(key) ?? reply.Things.GetValueOrDefault(key);
            output.Things.Add(made is { Ids.Count: > 0 } ? made.Ids[0] : throw new AcceptanceException($"{FixtureStage.Tool} {name}: thing {i} not staged"));
            output.Guns.AddRange(made.Guns);
        }

        return output;
    }

    /// <summary>
    /// Sets the game's RNG from <paramref name="seed"/> through test/lab_stage action reseed (#2849) and returns the next
    /// draws the native read without consuming them.
    /// </summary>
    public static async Task<List<int>> ReseedAsync(Harness h, int seed, CancellationToken cancellationToken)
    {
        if (seed == 0)
        {
            throw new AcceptanceException("reseed needs a non-zero seed");
        }

        var spec = new JsonObject { { "seed", seed } }.ToJsonString();
        var reply = await h.CallAsync(
            "combatlab-reseed-" + seed.ToString(CultureInfo.InvariantCulture),
            CombatFixtures.LabTool,
            new JsonObject { { "action", "reseed" }, { "spec", spec } },
            cancellationToken).ConfigureAwait(false);
        var read = CombatServe.Typed<Fx.LabReseedRead>(CombatFixtures.LabTool, reply);
        if (!read.Success)
        {
            throw new AcceptanceException($"{CombatFixtures.LabTool} reseed {seed} refused: {reply.ToJson()}");
        }

        if (read.Seed != seed)
        {
            throw new AcceptanceException($"{CombatFixtures.LabTool} reseeded under {read.Seed}, want {seed}");
        }

        return read.Draws.Count == 0
            ? throw new AcceptanceException($"{CombatFixtures.LabTool} reseed {seed} read back no draws: {reply.ToJson()}")
            : [.. read.Draws];
    }

    /// <summary>
    /// Runs <paramref name="n"/> synchronous game ticks on the paused lab (no clock, no safety stops: the fixture's
    /// hostiles are the point), then reads.
    /// </summary>
    public static async Task<Fx.LabFightRead> TickAsync(Harness h, int n, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("combatlab-tick", CombatFixtures.LabTool, new JsonObject { { "action", "tick" }, { "ticks", n } }, cancellationToken).ConfigureAwait(false);
        var read = CombatServe.Typed<Fx.LabFightRead>(CombatFixtures.LabTool, reply);
        return read.Success ? read : throw new AcceptanceException($"{CombatFixtures.LabTool} read refused: {reply.ToJson()}");
    }

    private static void CheckPawn(LabPawn want, Fx.StagedPawn got, string lord)
    {
        // A pod hostile lands where its arrival mode drops the pod.
        var wantCell = got.Spawned ? new Fx.StageCell(want.X, want.Z) : got.Cell;
        if (got.Cell != wantCell)
        {
            throw new AcceptanceException($"at {got.Cell.X},{got.Cell.Z}, want {wantCell.X},{wantCell.Z}: {got}");
        }

        if ((got.Weapon ?? "") != want.Weapon)
        {
            throw new AcceptanceException($"weapon {got.Weapon}, want {want.Weapon}");
        }

        var wantHostile = want.Side is CombatFixtures.Hostile or CombatFixtures.Manhunter or CombatFixtures.Mech;
        if (got.Hostile != wantHostile)
        {
            throw new AcceptanceException($"hostile {got.Hostile} for side {want.Side}");
        }

        if (got.Prisoner != (want.Side == CombatFixtures.Prisoner))
        {
            throw new AcceptanceException($"prisoner {got.Prisoner} for side {want.Side}");
        }

        if (want.Side == CombatFixtures.Hostile)
        {
            if (got.LordJob != lord)
            {
                throw new AcceptanceException($"lord job {got.LordJob}, want {lord}");
            }

            var wantWorn = want.Apparel.Length > 0 ? 1 : 0;
            if (got.Worn != wantWorn)
            {
                throw new AcceptanceException($"hostile wears {got.Worn} apparel, want {wantWorn}");
            }
        }

        if (got.Health < 0.999)
        {
            throw new AcceptanceException($"health {got.Health.ToString("0.000", CultureInfo.InvariantCulture)}, want full");
        }
    }
}
