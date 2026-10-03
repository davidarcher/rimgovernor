#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Odyssey facts (#1708): the static defs of the definition catalog
    // (biomes, tile mutators, hackables, portals, stockpile types), the
    // building row block (hack progress, portal state) and the colony map's
    // tile mutators. Everything is read from the game defs and objects, never
    // from name lists, and is absent without Odyssey.
    internal static class NativeOdysseyFacts
    {
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static string Label(Def def) => PlacementPreviewOperation.Diagnostic(def.label ?? "");
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static IEnumerable<T> Sorted<T>(IEnumerable<T> defs) where T : Def =>
            defs.Where(d => ProtoBoundary.IsIdentifier(d.defName)).OrderBy(d => d.defName, StringComparer.Ordinal);
        private static IEnumerable<string> Names(IEnumerable<Def>? defs) =>
            (defs ?? Enumerable.Empty<Def>()).Select(d => Id(d?.defName)).Where(n => n != null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal);

        internal static Obs.OdysseyCatalog? Catalog()
        {
            if (!ModsConfig.OdysseyActive) return null;
            var catalog = new Obs.OdysseyCatalog();
            var animals = Sorted(DefDatabase<PawnKindDef>.AllDefsListForReading.Where(k => k.RaceProps != null && k.RaceProps.Animal)).ToList();
            var diseases = Sorted(DefDatabase<IncidentDef>.AllDefsListForReading.Where(i => i.diseaseIncident != null)).ToList();
            foreach (var def in Sorted(DefDatabase<BiomeDef>.AllDefsListForReading)) catalog.Biomes.Add(Biome(def, animals, diseases));
            foreach (var def in Sorted(DefDatabase<TileMutatorDef>.AllDefsListForReading)) catalog.TileMutators.Add(Mutator(def));
            foreach (var def in Sorted(DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.GetCompProperties<CompProperties_Hackable>() != null)))
                catalog.Hackables.Add(Hackable(def));
            foreach (var def in Sorted(DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.portal != null))) catalog.Portals.Add(Portal(def));
            var generatable = TileMutatorWorker_Stockpile.GeneratableStockpileTypes ?? new List<TileMutatorWorker_Stockpile.StockpileType>();
            foreach (var name in Enum.GetNames(typeof(TileMutatorWorker_Stockpile.StockpileType)).OrderBy(n => n, StringComparer.Ordinal))
                if (Id(name) is string id)
                    catalog.StockpileTypes.Add(new Obs.StockpileTypeRow { Name = id, Generatable = generatable.Contains((TileMutatorWorker_Stockpile.StockpileType)Enum.Parse(typeof(TileMutatorWorker_Stockpile.StockpileType), name)) });
            return catalog;
        }

        private static void Animals(Google.Protobuf.Collections.RepeatedField<Obs.BiomeAnimal> into, List<PawnKindDef> kinds, Func<PawnKindDef, float> commonality)
        {
            foreach (var kind in kinds)
            {
                var c = commonality(kind);
                if (c > 0 && Finite(c)) into.Add(new Obs.BiomeAnimal { Kind = kind.defName, Commonality = c });
            }
        }

        private static Obs.BiomeRow Biome(BiomeDef def, List<PawnKindDef> animals, List<IncidentDef> diseases)
        {
            var row = new Obs.BiomeRow { DefName = def.defName, Label = Label(def), GeneratesNaturally = def.generatesNaturally, CanBuildBase = def.canBuildBase,
                Impassable = def.impassable, Extreme = def.isExtremeBiome, Water = def.isWaterBiome, InVacuum = def.inVacuum, HasBedrock = def.hasBedrock,
                AllowPollution = def.allowPollution, WildPlantsAreCavePlants = def.wildPlantsAreCavePlants };
            if (Finite(def.animalDensity)) row.AnimalDensity = def.animalDensity;
            if (Finite(def.plantDensity)) row.PlantDensity = def.plantDensity;
            if (Finite(def.diseaseMtbDays)) row.DiseaseMtbDays = def.diseaseMtbDays;
            if (Finite(def.forageability)) row.Forageability = def.forageability;
            if (Finite(def.movementDifficulty)) row.MovementDifficulty = def.movementDifficulty;
            if (Finite(def.geyserCountFactor)) row.GeyserCountFactor = def.geyserCountFactor;
            if (Finite(def.wildAnimalScariaChance)) row.WildAnimalScariaChance = def.wildAnimalScariaChance;
            if (Finite(def.pollutionOffset)) row.PollutionOffset = def.pollutionOffset;
            if (def.constantOutdoorTemperature is float constant && Finite(constant)) row.ConstantOutdoorTemperatureC = constant;
            row.MapConditions.Add(Names(def.biomeMapConditions));
            Animals(row.WildAnimals, animals, def.CommonalityOfAnimal);
            Animals(row.PollutionWildAnimals, animals, def.CommonalityOfPollutionAnimal);
            Animals(row.CoastalWildAnimals, animals, def.CommonalityOfCoastalAnimal);
            foreach (var incident in diseases)
            {
                var c = def.CommonalityOfDisease(incident);
                if (c > 0 && Finite(c)) row.Diseases.Add(new Obs.BiomeDisease { Incident = incident.defName, Commonality = c });
            }
            return row;
        }

        private static Obs.TileMutatorRow Mutator(TileMutatorDef def)
        {
            var row = new Obs.TileMutatorRow { DefName = def.defName, Label = Label(def), Cave = def.IsCave, PreventsLandmarks = def.preventsLandmarks };
            if (Id(def.Worker?.GetType().Name) is string worker) row.Worker = worker;
            row.Categories.Add((def.categories ?? new List<string>()).Where(ProtoBoundary.IsIdentifier).OrderBy(n => n, StringComparer.Ordinal));
            row.AdditionalGameConditions.Add(Names(def.additionalGameConditions));
            if (Finite(def.animalDensityFactor)) row.AnimalDensityFactor = def.animalDensityFactor;
            if (Finite(def.plantDensityFactor)) row.PlantDensityFactor = def.plantDensityFactor;
            if (Finite(def.geyserCountFactor)) row.GeyserCountFactor = def.geyserCountFactor;
            if (Finite(def.fishPopulationFactor)) row.FishPopulationFactor = def.fishPopulationFactor;
            row.BiomeWhitelist.Add(Names(def.biomeWhitelist));
            row.BiomeBlacklist.Add(Names(def.biomeBlacklist));
            return row;
        }

        private static Obs.HackableRow Hackable(ThingDef def)
        {
            var props = def.GetCompProperties<CompProperties_Hackable>();
            var row = new Obs.HackableRow { DefName = def.defName, Label = Label(def), Comp = Id(props.GetType().Name), IntellectualSkillPrerequisite = props.intellectualSkillPrerequisite,
                OnlyRemotelyHackable = props.onlyRemotelyHackable, LockoutPermanently = props.lockoutPermanently, GlowIfHacked = props.glowIfHacked,
                LockoutHoursMin = props.lockoutDurationHoursRange.min, LockoutHoursMax = props.lockoutDurationHoursRange.max };
            if (Finite(props.defence)) row.Defence = props.defence;
            if (Id(props.completedQuest?.defName) is string quest) row.CompletedQuest = quest;
            return row;
        }

        private static Obs.PortalRow Portal(ThingDef def)
        {
            var portal = def.portal;
            var row = new Obs.PortalRow { DefName = def.defName, Label = Label(def), PocketMapSize = portal.pocketMapSize };
            if (Id(portal.pocketMapGenerator?.defName) is string generator) row.PocketMapGenerator = generator;
            if (Id(portal.exitDef?.defName) is string exit) row.ExitDef = exit;
            row.PocketTileMutators.Add(Names(portal.pocketMapGenerator?.pocketMapProperties?.tileMutators));
            return row;
        }

        // The colony map's world-tile mutators; empty without Odyssey.
        internal static IEnumerable<string> MapMutators(Map map) =>
            !ModsConfig.OdysseyActive || map.TileInfo == null ? Enumerable.Empty<string>() : Names(map.TileInfo.Mutators).ToList();

        private static Obs.ReadIssue Failed(string field, Exception ex) => new Obs.ReadIssue { Field = field,
            Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } };

        // The building row block; null without Odyssey or when the building
        // is neither hackable nor a portal. A sub-read that throws leaves its
        // block absent and adds a ReadIssue.
        internal static Obs.OdysseyBuilding? Building(Thing thing)
        {
            if (!ModsConfig.OdysseyActive) return null;
            var row = new Obs.OdysseyBuilding();
            try
            {
                if (thing.TryGetComp<CompHackable>() is CompHackable hack)
                {
                    var state = new Obs.HackableState { Hacked = hack.IsHacked, LockedOut = hack.LockedOut, Autohack = hack.Autohack };
                    if (Finite(hack.ProgressPercent)) state.ProgressPercent = hack.ProgressPercent;
                    if (Finite(hack.defence)) state.Defence = hack.defence;
                    row.Hackable = state;
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("hackable", ex)); }
            try
            {
                if (thing is MapPortal portal)
                {
                    var state = new Obs.PortalState { PocketMapExists = portal.PocketMapExists };
                    if (portal.PocketMapExists) state.PocketMapId = portal.PocketMap.uniqueID;
                    if (portal is AncientHatch hatch)
                    {
                        if (Id(hatch.stockpileType.ToString()) is string type) state.StockpileType = type;
                        if (Id(hatch.layout?.defName) is string layout) state.Layout = layout;
                    }
                    row.Portal = state;
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("portal", ex)); }
            return row.Hackable == null && row.Portal == null && row.Issues.Count == 0 ? null : row;
        }
    }
}
