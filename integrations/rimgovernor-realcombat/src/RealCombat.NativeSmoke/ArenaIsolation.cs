using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using RimGovernor.RealCombat;
using Verse;

namespace RimGovernor.RealCombat.NativeSmoke;

/// <summary>Four independent native arenas, one committed hunting seed; no timing or statistical qualification.</summary>
internal static class ArenaIsolation
{
    private static bool _injectIce;

    public static void GeneratedMap(Map __result)
    {
        if (_injectIce)
            __result.terrainGrid.SetTempTerrain(new IntVec3(0, 0, 0), DefDatabase<TerrainDef>.GetNamed("ThinIce"));
    }

    public static int Run(string game, string fixtures, string output)
    {
        var request = RequestJson.Read(File.ReadAllText(Path.Combine(fixtures, "hunting-observed.request.json")));
        GameHost.Boot(game);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var rows = new List<object>();
        string? baseline = null;
        var settlements = Find.WorldObjects.AllWorldObjects.Count;
        for (var i = 0; i < 4; i++)
        {
            object? environment = null;
            using var arena = new Arena(request, map => environment = new
            {
                tile = map.Tile.ToString(), maps = Find.Maps.Count,
                tick = Find.TickManager.TicksGame, temperature = map.mapTemperature.OutdoorTemp,
                sky = map.skyManager.CurSkyGlow,
                snow = map.AllCells.Select(c => map.snowGrid.GetDepth(c)).ToArray(),
                terrain = map.AllCells.Select(c => map.terrainGrid.TerrainAt(c).defName).ToArray(),
            });
            Require(Find.Maps.Count == 1, "one live arena");
            try
            {
                using var overlapping = new Arena(request);
                throw new InvalidDataException("overlapping arena accepted");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("dispose the active arena", StringComparison.Ordinal)) { }
            var outcome = arena.Fight(request.Seeds.Single());
            Require(!outcome.Nondeterministic, "within-arena replay");
            var hunter = request.Pawns.First(p => p.Hunt.Length > 0);
            var target = outcome.Pawns.Single(p => p.Id == hunter.Hunt);
            Require((target.Dead || target.Downed || target.Withdrew) && outcome.Pawns.Single(p => p.Id == hunter.Id).Shots > 0,
                "native Hunt produced a physical encounter outcome");
            foreach (var departure in request.Departures.Where(d => d.Kind == "leave" && d.Tick < outcome.Ticks))
                Require(outcome.Pawns.Single(p => p.Id == departure.Id).WithdrawalTick == departure.Tick, "native timed departure");
            var row = new { environment, outcome, readback = arena.Readback };
            var wire = JsonSerializer.Serialize(row, options);
            baseline ??= wire;
            Require(wire == baseline, "environment, cell terrain, readback and physical outcomes across arenas");
            rows.Add(row);
            arena.Dispose();
            arena.Dispose();
            Require(Find.Maps.Count == 0 && Find.WorldObjects.AllWorldObjects.Count == settlements, "map and settlement released");
            try
            {
                arena.Fight(request.Seeds.Single());
                throw new InvalidDataException("disposed arena accepted a fight");
            }
            catch (ObjectDisposedException) { }
        }
        // Construction failures must release ownership too, without requiring an object returned to the caller.
        try
        {
            using var rejected = new Arena(request, _ => throw new InvalidDataException("staging-rejection"));
            throw new InvalidDataException("staging did not fail");
        }
        catch (InvalidDataException error) when (error.Message == "staging-rejection") { }
        Require(Find.Maps.Count == 0 && Find.WorldObjects.AllWorldObjects.Count == settlements, "failed construction released");

        // Native generated temporary terrain survives SetTerrain. Inject that precondition before reconstruction,
        // then require the ordinary readback gate to accept the requested Soil. No fight or new seed is needed.
        var harmony = new Harmony("rimgovernor.realcombat.arena-isolation");
        var buildMap = typeof(GameHost).GetMethod("BuildMap", BindingFlags.NonPublic | BindingFlags.Static)!;
        harmony.Patch(buildMap, postfix: new HarmonyMethod(typeof(ArenaIsolation).GetMethod(nameof(GeneratedMap))!));
        _injectIce = true;
        try { using var reconstructed = new Arena(request); }
        finally { _injectIce = false; harmony.Unpatch(buildMap, HarmonyPatchType.Postfix, harmony.Id); }
        Require(Find.Maps.Count == 0 && Find.WorldObjects.AllWorldObjects.Count == settlements, "reconstruction released");
        File.WriteAllText(output, JsonSerializer.Serialize(rows, options));
        Console.WriteLine("PASS arena isolation: four identical native outcomes, terrain, lifecycle and temporary-layer reconstruction");
        return 0;
    }

    private static void Require(bool condition, string claim)
    {
        if (!condition) throw new InvalidDataException("arena isolation: " + claim);
    }
}
