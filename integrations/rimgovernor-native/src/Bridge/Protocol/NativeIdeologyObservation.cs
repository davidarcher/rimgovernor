#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    // The primary ideoligion's current state for the snapshot frame's
    // "ideology" section; absent without Ideology. The static defs
    // (memes, precepts, ritual patterns and behaviors) are rows of the
    // definition catalog's generated def mirror.
    internal static class NativeIdeologyObservation
    {
        private static IEnumerable<string> Names<T>(IEnumerable<T>? defs) where T : Def =>
            (defs ?? Enumerable.Empty<T>()).Where(d => d != null).Select(d => Id(d.defName));

        private static IEnumerable<string> Sorted(IEnumerable<string> names) => names.OrderBy(n => n, StringComparer.Ordinal);

        // A building precept with its presence demand on the map (#3808): the game's AppliesTo and BuildingPresent, and
        // RequirementsSatisfied as the game computes it (a building of the precept whose effective room meets every
        // requirement) without filling the demand's met-count cache. The requirements by kind; the derived classes are
        // tested before the base they derive from.
        private static Obs.IdeoBuilding Building(Map map, Precept_Building building)
        {
            var row = new Obs.IdeoBuilding { Building = building.ThingDef == null ? null : Id(building.ThingDef.defName) };
            if (building.presenceDemand is not { } demand || building.ThingDef == null) return row;
            var requirements = demand.roomRequirements ?? new List<RoomRequirement>();
            var satisfied = requirements.Count == 0 || demand.AllBuildings(map).Any(b => b.GetRoom() != null
                && demand.GetEffectiveRoom(b) is { } room && requirements.All(r => r.MetOrDisabled(room)));
            row = row with { Applies = demand.AppliesTo(map), Present = demand.BuildingPresent(map), Satisfied = satisfied };
            int area = 0, impressiveness = 0;
            var floors = new List<string>();
            var things = new List<Obs.IdeoThingCount>();
            var forbidden = new List<string>();
            var unread = new List<string>();
            foreach (var requirement in requirements)
            {
                switch (requirement)
                {
                    case RoomRequirement_Area a: area = Math.Max(area, a.area); break;
                    case RoomRequirement_Impressiveness i: impressiveness = Math.Max(impressiveness, i.impressiveness); break;
                    case RoomRequirement_TerrainWithTags t: floors.AddRange(t.tags ?? new List<string>()); break;
                    case RoomRequirement_ThingCount c when c.thingDef != null: things.Add(new(Id(c.thingDef.defName), c.count)); break;
                    case RoomRequirement_Thing t when t.GetType() == typeof(RoomRequirement_Thing) && t.thingDef != null: things.Add(new(Id(t.thingDef.defName), 1)); break;
                    case RoomRequirement_ForbiddenBuildings f when f.GetType() == typeof(RoomRequirement_ForbiddenBuildings): forbidden.AddRange(f.buildingTags ?? new List<string>()); break;
                    default: unread.Add(requirement?.GetType().Name ?? "null"); break;
                }
            }
            return row with
            {
                MinArea = area,
                MinImpressiveness = impressiveness,
                FloorTags = [.. Sorted(floors.Distinct())],
                Things = [.. things.OrderBy(t => t.Def, StringComparer.Ordinal)],
                ForbiddenBuildingTags = [.. Sorted(forbidden.Distinct())],
                Unread = [.. Sorted(unread)],
            };
        }

        // The player faction's primary ideoligion as it stands; null without
        // Ideology or a primary ideoligion.
        internal static Obs.IdeologySnapshot? Build(Map map)
        {
            if (!ModsConfig.IdeologyActive) return null;
            var ideo = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo;
            if (ideo == null) return null;
            var row = new Obs.IdeologySnapshot { IdeoId = Id(ideo.GetUniqueLoadID()),
                Believers = ideo.ColonistBelieverCountCached,
                BelieverPawns = [.. map.mapPawns.FreeColonistsSpawned.Where(p => !p.IsSlave && p.Ideo == ideo)
                    .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(Ref)] };
            row = row with { Memes = [.. Sorted(Names(ideo.memes))] };
            row = row with { StyleCategories = [.. Sorted(ideo.thingStyleCategories.Where(c => c?.category != null).Select(c => Id(c.category.defName)))] };
            row = row with { VeneratedAnimals = [.. Sorted(Names(ideo.VeneratedAnimals))] };
            row = row with { Fluid = ideo.Fluid };
            if (ideo.Fluid && ideo.development == null) return null;
            row = row with { CanReform = ideo.Fluid && ideo.development!.CanReformNow };
            row = row with { DevelopmentPoints = ideo.Fluid ? ideo.development!.Points : 0 };
            row = row with { ReformCount = ideo.Fluid ? ideo.development!.reformCount : 0 };
            row = row with { NextReformPoints = ideo.Fluid ? ideo.development!.NextReformationDevelopmentPoints : 0 };
            foreach (var precept in ideo.PreceptsListForReading.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)) {
                var id = Id(precept.GetUniqueLoadID());
                var def = Id(precept.def.defName);
                switch (precept) {
                    case Precept_Role role:
                        var held = new Obs.IdeoRole { Id = id, DefName = def, Active = role.Active };
                        var holders = role.ChosenPawns().OrderBy(p => p.thingIDNumber).ToList();
                        held = held with { Pawns = [.. holders.Select(Ref)] };
                        held = held with { Converts = NativeConversionOperations.Grants(role.def), ConvertReady = holders.Any(NativeConversionOperations.Charged) };
                        row = row with { Roles = [.. row.Roles, held] };
                        break;
                    case Precept_Ritual ritual:
                        row = row with { Rituals = [.. row.Rituals, new Obs.IdeoRitual { Id = id, Pattern = ritual.sourcePattern == null ? null : Id(ritual.sourcePattern.defName),
                            LastFinishedTick = ritual.lastFinishedTick, ActiveObligations = ritual.activeObligations?.Count ?? 0, RepeatPenaltyActive = ritual.RepeatPenaltyActive, Running = NativeRitualBegin.Running(ritual) }] };
                        break;
                    case Precept_Building building:
                        row = row with { Buildings = [.. row.Buildings, Building(map, building)] };
                        break;
                    default:
                        row = row with { Precepts = [.. row.Precepts, new Obs.IdeoPrecept { Id = id, DefName = def }] };
                        break;
                }
            }
            return row;
        }
    }
}
