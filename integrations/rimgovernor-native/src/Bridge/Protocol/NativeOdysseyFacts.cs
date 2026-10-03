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
    // Odyssey facts (#1708): the catalog's derived part (each biome's wild
    // animal tables and the stockpile types; the defs themselves ride the def
    // mirror, #1791), the building row block (hack progress, portal state) and
    // the colony map's tile mutators. Everything is read from the game defs and
    // objects, never from name lists, and is absent without Odyssey.
    internal static class NativeOdysseyFacts
    {
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static IEnumerable<T> Sorted<T>(IEnumerable<T> defs) where T : Def =>
            defs.Where(d => ProtoBoundary.IsIdentifier(d.defName)).OrderBy(d => d.defName, StringComparer.Ordinal);
        private static IEnumerable<string> Names(IEnumerable<Def>? defs) =>
            (defs ?? Enumerable.Empty<Def>()).Select(d => Id(d?.defName)).Where(n => n != null).Select(n => n!).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);

        internal static Obs.OdysseyCatalog? Catalog()
        {
            if (!ModsConfig.OdysseyActive) return null;
            var catalog = new Obs.OdysseyCatalog();
            var animals = Sorted(DefDatabase<PawnKindDef>.AllDefsListForReading.Where(k => k.RaceProps != null && k.RaceProps.Animal)).ToList();
            foreach (var def in Sorted(DefDatabase<BiomeDef>.AllDefsListForReading)) catalog.BiomeAnimals.Add(Biome(def, animals));
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

        private static Obs.BiomeAnimals Biome(BiomeDef def, List<PawnKindDef> animals)
        {
            var row = new Obs.BiomeAnimals { Biome = def.defName };
            Animals(row.WildAnimals, animals, def.CommonalityOfAnimal);
            Animals(row.PollutionWildAnimals, animals, def.CommonalityOfPollutionAnimal);
            Animals(row.CoastalWildAnimals, animals, def.CommonalityOfCoastalAnimal);
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
