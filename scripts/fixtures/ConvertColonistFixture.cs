using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (#3501): a colony whose ideoligion grants the Convert ability through a role
    // has that role held by one colonist (the guide) and one other colonist holding another ideoligion (the target).
    // test/convert_colonist_prepare picks both from the existing colonists and the game's own role requirements; no
    // role, ideoligion or def name is listed here. test/convert_colonist_inspect reads the target's ideoligion and
    // certainty and the guide's cooldown. Everything after the staging (the order, the cast and its outcome) is the
    // controller's and the game's.
    public sealed class ConvertColonistFixture
    {
        [Tool("test/convert_colonist_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture (#3501): hold the Convert-granting role with one colonist and give another colonist a different ideoligion. Requires Ideology.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || player == null || !Find.TickManager.Paused) return Refuse("A paused disposable colony map is required.");
                if (!ModsConfig.IdeologyActive) return Refuse("Ideology is not active.");
                var ideo = player.ideos?.PrimaryIdeo;
                if (ideo == null) return Refuse("The player faction has no primary ideoligion.");
                var other = Find.IdeoManager.IdeosListForReading.Where(i => i != ideo).OrderBy(i => i.GetUniqueLoadID(), StringComparer.Ordinal).FirstOrDefault();
                if (other == null) return Refuse("The world holds no second ideoligion.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.ideo != null)
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count < 2) return Refuse("At least two existing colonists are required.");
                var roles = ideo.PreceptsListForReading.OfType<Precept_Role>()
                    .Where(r => NativeConversionOperations.Grants(r.def)).OrderBy(r => r.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                if (roles.Count == 0) return Refuse("The colony ideoligion holds no role that grants the Convert ability.");
                foreach (var role in roles)
                {
                    if (!role.Active) continue;
                    var guide = colonists.FirstOrDefault(p => p.Ideo == ideo && role.GetFirstUnmetRequirement(p) == null);
                    var target = colonists.FirstOrDefault(p => p != guide);
                    if (guide == null || target == null) continue;
                    role.ideo.GetRole(guide)?.Unassign(guide, true);
                    role.Assign(guide, true);
                    if (!role.IsAssigned(guide)) continue;
                    target.ideo.SetIdeo(other);
                    return new {
                        success = true, guideId = guide.GetUniqueLoadID(), targetId = target.GetUniqueLoadID(),
                        ideoId = ideo.GetUniqueLoadID(), targetIdeoId = target.Ideo.GetUniqueLoadID(),
                        certainty = target.ideo.Certainty, roleId = role.GetUniqueLoadID(),
                    };
                }
                return Refuse("No active Convert-granting role could be assigned to a colonist who meets its requirements.");
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/convert_colonist_inspect", Description = "Read-only conversion postcondition for the convert-colonist fixture: the target's ideoligion and certainty.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string targetId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var target = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned).FirstOrDefault(p => p.GetUniqueLoadID() == targetId);
                if (target?.ideo == null) return Refuse("The target colonist is not on a map.");
                return new { success = true, targetIdeoId = target.Ideo?.GetUniqueLoadID() ?? "", certainty = target.ideo.Certainty };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
