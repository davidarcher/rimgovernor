using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
namespace RimGovernor.Native;

internal static class NativeRitualActivities
{
    /// <summary>
    /// Every ritual precept of the primary ideoligion at every finished colony building its obligation target filter names
    /// (#4057): whether the game lets the ritual use the site now (RitualObligationTargetFilter.CanUseTarget against its
    /// first active obligation, null for a free start) and, for every free colonist, the game's own verdict on each
    /// behavior role (RitualRoleAssignments.PawnNotAssignableReason) and on spectating (CanEverSpectate), over the
    /// candidate set the begin dialog would build (Dialog_BeginRitual.CreateRitualRoleAssignments, no filter, nothing
    /// forced). The site cells join the coupled route locations, so each attendee's walk is priced by the shared leg
    /// cache; the catalog reads the length from the behavior def (#4058). None without Ideology.
    /// </summary>
    internal static RimGovernor.Brain.GameFacts.Observations.RitualActivityRead Read(Map map)
    {
        var sites = new List<RimGovernor.Brain.GameFacts.Observations.RitualSiteRead>();
        var ideo = ModsConfig.IdeologyActive ? Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo : null;
        if (ideo == null) return new() { Sites = EquatableArray.From(sites) };
        var pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        foreach (var ritual in ideo.PreceptsListForReading.OfType<Precept_Ritual>().OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            var filter = ritual.obligationTargetFilter;
            var wanted = filter?.def?.thingDefs;
            if (filter == null || wanted == null || wanted.Count == 0) continue;
            var pattern = ritual.sourcePattern ?? throw new InvalidOperationException("Ritual precept " + ritual.def.defName + " has no source pattern.");
            var obligation = ritual.activeObligations?.FirstOrDefault();
            foreach (var building in map.listerBuildings.allBuildingsColonist.Where(b => wanted.Contains(b.def)).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                var target = new TargetInfo(building);
                var use = filter.CanUseTarget(target, obligation);
                var assignments = Dialog_BeginRitual.CreateRitualRoleAssignments(ritual, target, map, null, null, null, null);
                var candidates = new List<RimGovernor.Brain.GameFacts.Observations.RitualCandidateRead>();
                foreach (var pawn in pawns)
                {
                    var roles = new List<RimGovernor.Brain.GameFacts.Observations.RitualRoleVerdict>();
                    foreach (var role in ritual.behavior.def.roles)
                    {
                        var why = assignments.PawnNotAssignableReason(pawn, role);
                        roles.Add(new(role.id, why == null, why ?? ""));
                    }
                    // CanEverSpectate refuses through the spectator filter or GatheringsUtility without a reason text;
                    // PawnNotAssignableReason names the game's reason when it has one.
                    var spectate = assignments.CanEverSpectate(pawn) ? null : assignments.PawnNotAssignableReason(pawn, null) ?? "spectator_filter";
                    candidates.Add(new(pawn.GetUniqueLoadID()) { Roles = EquatableArray.From(roles), Spectate = new("", spectate == null, spectate ?? "") });
                }
                sites.Add(new(ritual.GetUniqueLoadID(), pattern.defName, building.GetUniqueLoadID(), new(building.Position.x, building.Position.z))
                {
                    Usable = use.canUse, Refusal = use.canUse ? "" : use.failReason ?? "", Candidates = EquatableArray.From(candidates),
                });
            }
        }
        return new() { Sites = EquatableArray.From(sites) };
    }
}
