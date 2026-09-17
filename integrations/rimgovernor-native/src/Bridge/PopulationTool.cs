#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class PopulationTools
    {
        private static bool? IndoorBed(Pawn p)
        {
            var bed = p.ownership?.OwnedBed;
            if (bed == null) return null;
            return bed.Spawned && bed.Position.Roofed(bed.Map) && bed.GetRoom()?.ProperRoom == true
                && !bed.GetRoom().PsychologicallyOutdoors;
        }

        internal static object Person(Pawn p) => new {
            thingId = p.GetUniqueLoadID(), name = p.LabelShort, dead = p.Dead, downed = p.Downed,
            admitted = p.IsFreeColonist && p.Faction == Faction.OfPlayerSilentFail,
            prisoner = p.IsPrisonerOfColony, guest = p.HostFaction == Faction.OfPlayerSilentFail,
            faction = p.Faction?.GetUniqueLoadID(), hostile = p.HostileTo(Faction.OfPlayerSilentFail),
            recruitable = p.guest == null ? (bool?)null : p.guest.Recruitable,
            interaction = p.guest?.ExclusiveInteractionMode?.defName,
            resistance = p.guest == null ? (float?)null : p.guest.Resistance,
            bed = p.CurrentBed()?.GetUniqueLoadID(), ownedBed = p.ownership?.OwnedBed?.GetUniqueLoadID(),
            ownedBedForPrisoners = p.ownership?.OwnedBed?.ForPrisoners,
            ownedBedIndoors = IndoorBed(p),
            food = p.needs?.food?.CurLevelPercentage,
            nutritionPerDay = p.needs?.food == null ? (float?)null : p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000f,
            needsTend = p.health?.HasHediffsNeedingTend(),
            job = p.CurJob?.def?.defName
        };

        [Tool("home/population", Title = "Population and prisoner policy",
            Description = "Read current human population and custody. Optionally set a colony prisoner's normal Recruit or MaintainOnly interaction with exact prior-setting comparison. Does not recruit instantly or change custody. Other interactions are unsupported. dryRun defaults true.")]
        public async Task<object> Population(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact observed prisoner Thing ID, required for a setting change.")] string? pawn = null,
            [ToolParameter(Description = "Recruit or MaintainOnly; omit for observation.")] string? interaction = null,
            [ToolParameter(Description = "Exact observed prior exclusive interaction; refuses changed player settings.")] string? expectedInteraction = null,
            [ToolParameter(Description = "Preview only.", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return new { success = false, error = "No current map" };
                var people = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike).ToList();
                if (interaction != null)
                {
                    var p = people.SingleOrDefault(x => x.GetUniqueLoadID() == pawn);
                    if (p == null || p.Dead || !p.IsPrisonerOfColony || p.guest == null)
                        return new { success = false, error = "Target is not a living current-map colony prisoner" };
                    if (p.guest.ExclusiveInteractionMode?.defName != expectedInteraction)
                        return new { success = false, error = "Prisoner interaction changed; preserve player decision" };
                    var def = DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(interaction);
                    if (def == null || (def != PrisonerInteractionModeDefOf.AttemptRecruit && def != PrisonerInteractionModeDefOf.MaintainOnly)
                        || def.isNonExclusiveInteraction || (def.hideIfNotRecruitable && !p.guest.Recruitable)
                        || (p.IsWildMan() && !def.allowOnWildMan))
                        return new { success = false, error = "Unsupported or ineligible native prisoner interaction" };
                    if (!dryRun) p.guest.SetExclusiveInteraction(def);
                }
                return new { success = true, dryRun, tick = Find.TickManager.TicksGame, mapId = map.uniqueID,
                    interactions = new[] { PrisonerInteractionModeDefOf.AttemptRecruit, PrisonerInteractionModeDefOf.MaintainOnly }
                        .Select(d => new { name = d.defName, label = d.label }).ToArray(),
                    people = people.Select(Person).ToArray() };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
