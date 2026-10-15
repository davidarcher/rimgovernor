using System.Diagnostics;
using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Host;
using RimGovernor.RealCombat;

namespace RimGovernor.RealCombat.NativeSmoke;

internal static class PredictorCheck
{
    public static async Task<int> RunAsync(string game, string worker, string fixtures, string output)
    {
        // Same committed seed and scene: direct engine baseline, cold worker, then warm worker. No sweep.
        var source = RequestJson.Read(File.ReadAllText(Path.Combine(fixtures, "hunting-observed.request.json")));
        GameHost.Boot(game);
        var mods = EngineIdentity.Mods();
        var build = EngineIdentity.GameBuild;
        using var arena = new Arena(source);
        var baseline = arena.Fight(source.Seeds.Single());
        var readback = arena.Readback;
        var request = new EncounterRequest
        {
            Identity = new("prepared-fixture", 0, build, mods, "native-hunt/timed-departure"),
            Width = source.Width, Height = source.Height, Terrain = source.Terrain, Weather = source.Weather,
            Ground = source.Ground.Select(g => new EncounterCellRegion(g.MinX, g.MinZ, g.MaxX, g.MaxZ, g.Terrain, g.Roof)).ToArray(),
            Obstacles = source.Things.Select(t => new EncounterObstacle(t.Def, t.Stuff, t.X, t.Z)).ToArray(),
            HorizonTicks = source.MaxTicks, Seeds = source.Seeds, SamplingReason = "paired fixture lifecycle smoke; not statistical qualification",
            Actors = source.Pawns.Select(p =>
            {
                var observed = readback.Pawns.Single(a => a.Id == p.Id);
                var departure = source.Departures.SingleOrDefault(d => d.Id == p.Id);
                return new EncounterActor
                {
                    Id = p.Id, Kind = p.Kind, Wild = p.Side == "wild", X = p.X, Z = p.Z, Age = p.Age, Xenotype = p.Xenotype,
                    Herd = p.Herd, HuntTarget = p.Hunt,
                    Weapon = p.Weapon.Length == 0 ? null : new(p.Weapon, p.WeaponStuff, observed.WeaponQuality, observed.WeaponHitPoints),
                    Apparel = observed.Worn.Select(w => new EncounterGear(w.Def, w.Stuff, w.Quality, w.HitPoints)).ToArray(),
                    Skills = p.Skills, Traits = p.Traits.Select(t => new EncounterTrait(t.Def, t.Degree)).ToArray(),
                    Health = p.Hediffs.Select(h => new EncounterHediff(h.Def, h.Part, h.Severity)).ToArray(), MissingParts = p.Missing,
                    Departure = departure is null ? null : new(departure.Tick, departure.Kind == "leave" ? EncounterDepartureKind.Leave : EncounterDepartureKind.Flee),
                };
            }).ToArray(),
        };
        var settings = new LocalEncounterOptions("dotnet", Path.GetFullPath(worker), Path.GetFullPath(game), Path.GetDirectoryName(Path.GetFullPath(output))!, build, mods,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), 1, source.MaxTicks, 60, 16, 1048576);
        await using var predictor = new LocalEncounterPredictor(settings);
        var consumer = new EncounterPredictionConsumer(predictor, TimeSpan.FromSeconds(95));
        var cold = await consumer.PredictAsync(request, () => request, CancellationToken.None);
        var warm = await consumer.PredictAsync(request, () => request, CancellationToken.None);
        var unsupported = await consumer.PredictAsync(request with { UnsupportedObservations = ["unmodeled_temperature"] }, () => request with { UnsupportedObservations = ["unmodeled_temperature"] }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var pending = predictor.PredictAsync(request, cancellation.Token);
        cancellation.Cancel();
        var cancelled = await pending;
        var cancellationMilliseconds = watch.Elapsed.TotalMilliseconds;
        var expected = new EncounterSample(baseline.Seed, baseline.Ticks, baseline.Ticks == source.MaxTicks, baseline.Pawns.Select(p =>
            new EncounterActorOutcome(p.Id, p.Downed, p.Dead, p.HealthPercent, p.BleedRate, p.NeedsTend,
                p.Injuries.Select(i => new EncounterInjury(i.Def, i.Part, i.Severity, i.BleedRate, i.TendableNow, i.Tended, i.Permanent)).ToArray(),
                p.Missing, p.Kills, p.Products is not { } products ? null : new(products.MeatDef, products.Meat, products.LeatherDef, products.Leather),
                Tick(p.FirstAttackTick), Tick(p.LastShotTick), Tick(p.DepartureTick), Tick(p.WithdrawalTick))).ToArray());
        var paired = cold.Status == EncounterStatus.Evidence && warm.Status == EncounterStatus.Evidence &&
            JsonSerializer.Serialize(cold.Samples.Single()) == JsonSerializer.Serialize(expected) &&
            JsonSerializer.Serialize(cold.Samples) == JsonSerializer.Serialize(warm.Samples);
        File.WriteAllText(output, JsonSerializer.Serialize(new { build, mods, baseline, cold, warm, unsupported, cancelled, cancellationMilliseconds, paired }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"predictor paired={paired}, cold={cold.Status}/{cold.ColdStartMilliseconds}/{cold.RequestMilliseconds}ms, warm={warm.Status}/{warm.RequestMilliseconds}ms, cancel={cancelled.Status}/{cancellationMilliseconds}ms; {output}");
        return paired && cancelled.Status == EncounterStatus.Cancelled && unsupported.Status == EncounterStatus.Unsupported ? 0 : 1;
    }

    private static int? Tick(int tick) => tick < 0 ? null : tick;
}
