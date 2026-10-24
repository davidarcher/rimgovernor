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
            var thing = building.ThingDef ?? throw new InvalidOperationException("Building precept " + building.def.defName + " has no ThingDef.");
            var demand = building.presenceDemand ?? throw new InvalidOperationException("Building precept " + building.def.defName + " has no presence demand.");
            var requirements = demand.roomRequirements ?? new List<RoomRequirement>();
            var satisfied = requirements.Count == 0 || demand.AllBuildings(map).Any(b => b.GetRoom() != null
                && demand.GetEffectiveRoom(b) is { } room && requirements.All(r => r.MetOrDisabled(room)));
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
            return new Obs.IdeoBuilding
            {
                Building = Id(thing.defName),
                Applies = demand.AppliesTo(map),
                Present = demand.BuildingPresent(map),
                Satisfied = satisfied,
                MinArea = area,
                MinImpressiveness = impressiveness,
                FloorTags = [.. Sorted(floors.Distinct())],
                Things = [.. things.OrderBy(t => t.Def, StringComparer.Ordinal)],
                ForbiddenBuildingTags = [.. Sorted(forbidden.Distinct())],
                Unread = [.. Sorted(unread)],
            };
        }

        // The player faction's primary ideoligion as it stands; null without
        // Ideology. With Ideology active a colony always holds a primary
        // ideoligion, so its absence, a fluid ideoligion without its
        // development tracker, or a ritual without its pattern, throws.
        internal static Obs.IdeologySnapshot? Build(Map map)
        {
            if (!ModsConfig.IdeologyActive) return null;
            var ideo = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo
                ?? throw new InvalidOperationException("Ideology is active but the player faction has no primary ideoligion.");
            Obs.IdeoDevelopment development = new Obs.IdeoDevelopment.Fixed();
            if (ideo.Fluid)
            {
                var d = ideo.development ?? throw new InvalidOperationException("Fluid ideoligion without its development tracker.");
                development = new Obs.IdeoDevelopment.Fluid(d.CanReformNow, d.Points, d.reformCount, d.NextReformationDevelopmentPoints);
            }
            var precepts = new List<Obs.IdeoPrecept>();
            var roles = new List<Obs.IdeoRole>();
            var rituals = new List<Obs.IdeoRitual>();
            var buildings = new List<Obs.IdeoBuilding>();
            foreach (var precept in ideo.PreceptsListForReading.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)) {
                var id = Id(precept.GetUniqueLoadID());
                var def = Id(precept.def.defName);
                switch (precept) {
                    case Precept_Role role:
                        var holders = role.ChosenPawns().OrderBy(p => p.thingIDNumber).ToList();
                        roles.Add(new Obs.IdeoRole { Id = id, DefName = def, Active = role.Active, Pawns = [.. holders.Select(PawnId)],
                            Converts = NativeConversionOperations.Grants(role.def), ConvertReady = holders.Any(NativeConversionOperations.Charged) });
                        break;
                    case Precept_Ritual ritual:
                        var pattern = ritual.sourcePattern ?? throw new InvalidOperationException("Ritual precept " + def + " has no source pattern.");
                        rituals.Add(new Obs.IdeoRitual { Id = id, Pattern = Id(pattern.defName), LastFinishedTick = ritual.lastFinishedTick,
                            ActiveObligations = ritual.activeObligations?.Count ?? 0, RepeatPenaltyActive = ritual.RepeatPenaltyActive, Running = NativeRitualBegin.Running(ritual) });
                        break;
                    case Precept_Building building:
                        buildings.Add(Building(map, building));
                        break;
                    default:
                        precepts.Add(new Obs.IdeoPrecept { Id = id, DefName = def });
                        break;
                }
            }
            return new Obs.IdeologySnapshot
            {
                IdeoId = Id(ideo.GetUniqueLoadID()),
                Memes = [.. Sorted(Names(ideo.memes))],
                Precepts = [.. precepts],
                Roles = [.. roles],
                Rituals = [.. rituals],
                Buildings = [.. buildings],
                Believers = ideo.ColonistBelieverCountCached,
                BelieverPawns = [.. map.mapPawns.FreeColonistsSpawned.Where(p => !p.IsSlave && p.Ideo == ideo)
                    .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(PawnId)],
                Development = development,
                StyleCategories = [.. Sorted(ideo.thingStyleCategories.Where(c => c?.category != null).Select(c => Id(c.category.defName)))],
                VeneratedAnimals = [.. Sorted(Names(ideo.VeneratedAnimals))],
            };
        }

        private static RimGovernor.Brain.Domain.PawnId PawnId(Pawn pawn) => new(Id(pawn.GetUniqueLoadID()));
    }
}
