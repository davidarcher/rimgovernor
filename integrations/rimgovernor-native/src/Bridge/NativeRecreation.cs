using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

/// <summary>
/// The recreation buildings the game's own JoyGivers use (#3570): every JoyGiverDef whose worker is a
/// <see cref="JoyGiver_InteractBuilding"/> (horseshoes, hoopstone, chess, poker, Ur, billiards, televisions, telescope,
/// instruments and any modded giver of that family), read from its def, never a hard-coded building. One source for the
/// needs read, bind-time checks and the exact job, so an observed target is one Hands can start.
/// </summary>
internal static class NativeRecreation
{
    private static Dictionary<ThingDef, JoyGiverDef>? givers;

    /// <summary>The interact-building giver that uses this building, or null when the game has none for it.</summary>
    internal static JoyGiverDef? GiverFor(Thing thing)
    {
        givers ??= DefDatabase<JoyGiverDef>.AllDefsListForReading
            .Where(g => g.Worker is JoyGiver_InteractBuilding && g.jobDef != null && g.thingDefs != null)
            .SelectMany(g => g.thingDefs.Select(d => (d, g)))
            .GroupBy(p => p.d).ToDictionary(p => p.Key, p => p.First().g);
        return givers.TryGetValue(thing.def, out var giver) ? giver : null;
    }

    /// <summary>
    /// The giver's own legality for this pawn and building, as JoyGiver_InteractBuilding.FindBestGame tests a candidate:
    /// CanBeGivenTo (required capacities, joy kind, outdoors preference) then the worker's CanInteractWith (reservation at
    /// joyMaxParticipants, forbidden, fogged, social and political propriety, vacuum, power, unroofed-only).
    /// </summary>
    internal static bool Usable(Pawn pawn, Thing thing, JoyGiverDef giver) =>
        pawn.needs?.joy != null && giver.Worker.CanBeGivenTo(pawn) && Access.CanInteract(giver, pawn, thing);

    /// <summary>The worker's own play job on exactly this building (its seat, watch cell or interaction cell), or null; the caller checks <see cref="Usable"/> first.</summary>
    internal static Job? PlayJob(Pawn pawn, Thing thing, JoyGiverDef giver) => Access.PlayJob(giver, pawn, thing);

    /// <summary>
    /// JoyUtility.JoyTickCheckEnd's gain per tick as every vanilla interact-building driver calls it (joySource = the
    /// building, extra factor 1, decompiled 1.7): joyGainRate × 0.36 / 2500 × the building's JoyGainFactor × the pawn's
    /// tolerance factor for the job's joy kind, at the observed tolerance.
    /// </summary>
    internal static double GainPerTick(Pawn pawn, Thing thing, JobDef job) =>
        job.joyGainRate * 0.36 / RimGovernor.Brain.Domain.GameTime.TicksPerHour * thing.GetStatValue(StatDefOf.JoyGainFactor)
        * pawn.needs.joy.tolerances.JoyFactorFromTolerance(job.joyKind);

    /// <summary>
    /// The joy a pawn gains a tick with no recreation building (#3825): the best of the outdoor givers every colony has,
    /// JoyGiver_GoForWalk and JoyGiver_Skygaze, whose drivers call JoyTickCheckEnd with no building (decompiled 1.7):
    /// joyGainRate × 0.36 / 2500 × the pawn's tolerance factor for the job's joy kind (Need_Joy.GainJoy); 0 when the giver's own
    /// CanBeGivenTo (capacities, outdoors preference, joy kind) refuses both. Both givers and both drivers also require
    /// JoyUtility.EnjoyableOutsideNow; when it holds is <see cref="OutdoorShares"/> (#3825 D7), not this rate. Skygaze's
    /// game-condition gain factor (aurora, eclipse) is not applied. The tolerances are <paramref name="joy"/>'s: the pawn's own
    /// need, or a detached one of a pawn without it now (#3805).
    /// </summary>
    internal static double UnservedGainPerTick(Pawn pawn, Need_Joy joy)
    {
        outdoorGivers ??= DefDatabase<JoyGiverDef>.AllDefsListForReading
            .Where(g => g.jobDef != null && (g.giverClass == typeof(JoyGiver_GoForWalk) || g.giverClass == typeof(JoyGiver_Skygaze))).ToArray();
        return outdoorGivers.Where(g => g.Worker.CanBeGivenTo(pawn))
            .Select(g => g.jobDef.joyGainRate * 0.36 / RimGovernor.Brain.Domain.GameTime.TicksPerHour * joy.tolerances.JoyFactorFromTolerance(g.jobDef.joyKind))
            .DefaultIfEmpty(0).Max();
    }

    private static JoyGiverDef[]? outdoorGivers;

    /// <summary>
    /// The expected share of each twelfth of the coming year, indexed by twelfth (the current one from now to its end), in which
    /// JoyUtility.EnjoyableOutsideNow holds for the pawn (#3825 D7): the hours of the map's <see cref="OutdoorClimate"/> whose
    /// outdoor temperature lies in the pawn's ComfortableTemperatureRange (FloatRange.Includes, at its gear now), each counted at
    /// the chance that no rain of 0.25 or more and no game condition rules it out; 0 throughout on a vacuum biome for a pawn
    /// ConcernedByVacuum.
    /// </summary>
    internal static double[] OutdoorShares(Pawn pawn, Map map, OutdoorClimate climate)
    {
        var shares = new double[GenDate.TwelfthsPerYear];
        if (map.Biome.inVacuum && pawn.ConcernedByVacuum) return shares;
        var comfort = pawn.ComfortableTemperatureRange();
        for (var twelfth = 0; twelfth < shares.Length; twelfth++)
        {
            var hours = climate.Hours[twelfth];
            var open = 0.0;
            foreach (var (temperature, chance) in hours) if (comfort.Includes(temperature)) open += chance;
            shares[twelfth] = open / hours.Length;
        }
        return shares;
    }

    /// <summary>
    /// The map's side of JoyUtility.EnjoyableOutsideNow over the coming year (#3825 D7), one row an hour through each twelfth's
    /// window on the local calendar of <see cref="NativeGearFacts.TwelfthNow"/> (the current twelfth from now, each other from its
    /// start in the year ahead). Each row is the outdoor temperature MapTemperature.OutdoorTemp will read then (the biome's
    /// constant or the pocket map's temperature, else TileTemperaturesComp.OutdoorTemperatureAt: the season, the sun cycle and the
    /// daily random variation; plus the TemperatureOffset of each condition active now while it lasts) and the chance the map
    /// allows it: 0 while an active condition's AllowEnjoyableOutsideNow is false, else the chance WeatherManager.RainRate is
    /// under 0.25. That chance is the time share of the weathers WeatherDecider picks at that hour's temperature
    /// (CurrentWeatherCommonality: the biome's baseWeatherCommonalities, the rainfall factor, the active conditions' factors and
    /// preventRain, the tile mutators, the weather's temperature range, the first eight days' favorability rule and the Anomaly
    /// monolith gate), each weather weighted by its mean durationRange, or a forcing condition's weather while it lasts.
    /// Left out: the 4000-tick weather transition, the no-repeat rule, the fire and out-of-range shortening and DisableRainFor.
    /// </summary>
    internal sealed class OutdoorClimate
    {
        /// <summary>JoyUtility.EnjoyableOutsideNow's rain limit, a literal in the game's code that no constant states.</summary>
        private const float RainLimit = 0.25f;

        /// <summary>The rain rate above which WeatherDecider treats a weather as rain (preventRain), a literal in the game's code.</summary>
        private const float RainyWeather = 0.1f;

        internal readonly (float Temperature, double Open)[][] Hours = new (float, double)[GenDate.TwelfthsPerYear][];

        internal OutdoorClimate(Map map)
        {
            var (current, toNext, _) = NativeGearFacts.TwelfthNow(map);
            var now = GenTicks.TicksAbs;
            var affecting = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, affecting);
            var own = map.gameConditionManager.ActiveConditions;
            for (var ahead = 0; ahead < GenDate.TwelfthsPerYear; ahead++)
            {
                var start = ahead == 0 ? 0 : toNext + (ahead - 1) * GenDate.TicksPerTwelfth;
                var length = ahead == 0 ? toNext : GenDate.TicksPerTwelfth;
                var hours = new (float, double)[Math.Max(1, (length + GenDate.TicksPerHour - 1) / GenDate.TicksPerHour)];
                for (var hour = 0; hour < hours.Length; hour++)
                {
                    var ticks = start + Math.Min(length - 1, hour * GenDate.TicksPerHour + GenDate.TicksPerHour / 2);
                    var temperature = Outdoor(map, now + ticks) + affecting.Where(c => Active(c, ticks)).Sum(c => c.TemperatureOffset());
                    var blocked = affecting.Any(c => Active(c, ticks) && !c.AllowEnjoyableOutsideNow(map));
                    hours[hour] = (temperature, blocked ? 0 : 1 - Rainy(map, temperature, (int)(GenDate.DaysPassedSinceSettleFloat + ticks / (float)GenDate.TicksPerDay), ticks, affecting, own));
                }
                Hours[(current + ahead) % GenDate.TwelfthsPerYear] = hours;
            }
        }

        private static bool Active(GameCondition condition, int ticks) => condition.Permanent || condition.TicksLeft > ticks;

        private static float Outdoor(Map map, int absTick) => map.Biome.constantOutdoorTemperature
            ?? (map.IsPocketMap ? map.generatorDef.pocketMapProperties.temperature : Find.World.tileTemperatures.OutdoorTemperatureAt(map.Tile, absTick));

        // The time share of rain at or above the limit, as WeatherDecider.ChooseNextWeather draws the weather and durationRange holds it.
        private static double Rainy(Map map, float temperature, int days, int ticks, List<GameCondition> affecting, List<GameCondition> own)
        {
            WeatherDef? forced = null;
            foreach (var condition in affecting) if (Active(condition, ticks) && condition.ForcedWeather() is { } weather) forced = weather;
            if (forced != null) return forced.rainRate >= RainLimit ? 1 : 0;
            double all = 0, rainy = 0;
            foreach (var weather in DefDatabase<WeatherDef>.AllDefsListForReading)
            {
                var weight = Commonality(map, weather, temperature, days, ticks, own) * weather.durationRange.Average;
                all += weight;
                if (weather.rainRate >= RainLimit) rainy += weight;
            }
            if (all > 0) return rainy / all;
            // All commonalities zero: the decider takes one of the biome's weathers at random, or Clear.
            var records = map.Biome.baseWeatherCommonalities;
            if (records.Count == 0) return WeatherDefOf.Clear.rainRate >= RainLimit ? 1 : 0;
            return records.Sum(r => r.weather.rainRate >= RainLimit ? r.weather.durationRange.Average : 0) / records.Sum(r => r.weather.durationRange.Average);
        }

        // WeatherDecider.CurrentWeatherCommonality at this hour's temperature, days since settling and active conditions.
        private static float Commonality(Map map, WeatherDef weather, float temperature, int days, int ticks, List<GameCondition> own)
        {
            if (!weather.temperatureRange.Includes(temperature) || weather.favorability < Favorability.Neutral && days < 8) return 0;
            if (weather.rainRate > RainyWeather && own.Any(c => Active(c, ticks) && c.def.preventRain)) return 0;
            if (ModsConfig.AnomalyActive && weather.minMonolithLevel > Find.Anomaly.HighestLevelReached
                && (!weather.canOccurInAmbientHorror || !Find.Anomaly.AmbientHorrorMode)) return 0;
            var commonality = 0f;
            foreach (var record in map.Biome.baseWeatherCommonalities)
            {
                if (record.weather != weather) continue;
                commonality = record.commonality;
                if (weather.commonalityRainfallFactor != null) commonality *= weather.commonalityRainfallFactor.Evaluate(map.TileInfo.rainfall);
                foreach (var condition in own) if (Active(condition, ticks)) commonality *= condition.WeatherCommonalityFactor(weather, map);
                break;
            }
            foreach (var mutator in map.TileInfo.Mutators) mutator.Worker?.MutateWeatherCommonalityFor(weather, map.Tile, ref commonality);
            return commonality;
        }
    }

    /// <summary>
    /// Vanilla drivers whose play is one stationary final Delay toil of joyDuration (the watch and sit-facing families),
    /// so ticksLeftThisToil is the session's remainder. Billiards re-walks between shots and is not one.
    /// </summary>
    internal static bool StationaryDriver(JobDriver? driver) => driver?.GetType() is { } type
        && (type == typeof(JobDriver_WatchBuilding) || type == typeof(JobDriver_PlayHorseshoes) || type == typeof(JobDriver_PlayHoopstone)
            || type == typeof(JobDriver_WatchTelevision) || type == typeof(JobDriver_SitFacingBuilding) || type == typeof(JobDriver_PlayPoker)
            || type == typeof(JobDriver_PlayMusicalInstrument));

    // The worker's protected members, invoked on the def's own worker so each giver's override runs (reflection invokes virtually).
    private abstract class Access : JoyGiver_InteractBuilding
    {
        private static readonly MethodInfo CanInteractWithMethod =
            (typeof(JoyGiver_InteractBuilding).GetMethod(nameof(CanInteractWith), BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."));
        private static readonly MethodInfo TryGivePlayJobMethod =
            (typeof(JoyGiver_InteractBuilding).GetMethod(nameof(TryGivePlayJob), BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."));
        internal static bool CanInteract(JoyGiverDef giver, Pawn pawn, Thing thing) =>
            (bool)(CanInteractWithMethod.Invoke(giver.Worker, [pawn, thing, false]) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."));
        internal static Job? PlayJob(JoyGiverDef giver, Pawn pawn, Thing thing) =>
            (Job?)TryGivePlayJobMethod.Invoke(giver.Worker, [pawn, thing]);
    }
}
