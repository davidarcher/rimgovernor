using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    // PestFixture stages the alphabeaver incident's precondition without the
    // storyteller (#247): a small wild pack of Alphabeaver spawned on open
    // ground some distance from the colonists, factionless and not hostile,
    // exactly as IncidentWorker_Alphabeavers leaves them. The emergency
    // census ignores them; the ClearPests goal has to notice them in the
    // wild-animal census and hunt them. The hunters are the equip and work
    // families' to stage from the baseline's loose bows (#321); the fixture
    // only reports who could hunt now and what is lying about. Inspect
    // reports each staged beaver's native state (dead, downed, still
    // spawned) so the case asserts the postcondition natively rather than
    // from receipts.
    public sealed class PestFixture
    {
        private const string PestKind = "Alphabeaver";
        private static List<Pawn> fixturePests = new List<Pawn>();
        // The world the pack was staged in: a kept process reloads the save
        // between cases, and a pawn from the old world still answers Spawned
        // through its stale map, so the list is forgotten on a world change.
        private static World fixtureWorld;
        private static List<Pawn> CurrentPests()
        {
            if (fixtureWorld != Find.World) { fixturePests = new List<Pawn>(); fixtureWorld = null; }
            return fixturePests;
        }

        [Tool("test/pest_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: spawn count (default 3) wild Alphabeaver on open reachable ground about distance (default 40) cells from the colonists' center, factionless and not hostile, and report the staged ids with the colonists who could hunt now and the loose ordinary ranged weapons. Nobody is armed or assigned; the equip and work families do that. No game tick changes.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int count = 3, int distance = 40)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused colony map is required.");
                if (count < 1 || count > 8 || distance < 15 || distance > 120) return Refuse("count must be 1..8 and distance 15..120.");
                var staged = CurrentPests();
                if (staged.Any(p => p.Spawned && !p.Dead && p.Map == map))
                    return Refuse("Fixture pests already on the map; inspect them before staging another pack.");
                var kindDef = DefDatabase<PawnKindDef>.GetNamedSilentFail(PestKind);
                if (kindDef == null) return Refuse(PestKind + " PawnKindDef unavailable.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).ToList();
                if (colonists.Count == 0) return Refuse("No standing colonist.");
                var center = new IntVec3((int)colonists.Average(p => p.Position.x), 0, (int)colonists.Average(p => p.Position.z));
                var anchor = GenRadial.RadialCellsAround(center, distance + 6, true).FirstOrDefault(c => c.InBounds(map)
                    && c.DistanceTo(center) >= distance && c.Standable(map) && !c.Fogged(map) && !c.Roofed(map)
                    && map.reachability.CanReach(c, colonists[0].Position, Verse.AI.PathEndMode.Touch, TraverseMode.NoPassClosedDoors, Danger.Deadly));
                if (!anchor.IsValid) return Refuse("No standable open cell " + distance + " cells from the colonists that reaches them.");
                var cells = GenRadial.RadialCellsAround(anchor, 4, true).Where(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && !c.Roofed(map)).Take(count).ToList();
                if (cells.Count < count) return Refuse("Not enough open cells around the anchor for the pack.");
                var pack = new List<Pawn>();
                foreach (var cell in cells)
                {
                    var pest = PawnGenerator.GeneratePawn(kindDef, null);
                    GenSpawn.Spawn(pest, cell, map);
                    // Hungry enough to forage (the pack works through the
                    // trees as the incident's does), not starving.
                    if (pest.needs?.food != null) pest.needs.food.CurLevelPercentage = 0.5f;
                    pack.Add(pest);
                }
                fixturePests = pack; fixtureWorld = Find.World;
                // What the equip and work families have to work with: the
                // loose ordinary ranged weapons on the map (longest range
                // first) and who may hunt at all.
                var weapons = map.listerThings.AllThings.OfType<ThingWithComps>()
                    .Where(t => t.Spawned && t.def.IsRangedWeapon && t.def.equipmentType == EquipmentType.Primary && t.TryGetComp<CompEquippable>() != null
                        && NativeHuntAcquisition.OrdinaryVerbs(t.def.Verbs))
                    .OrderByDescending(t => t.def.Verbs.Where(v => !v.IsMeleeAttack).Max(v => v.range)).ThenBy(t => t.thingIDNumber).ToList();
                if (!colonists.Any(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting))) return Refuse("No colonist may hunt.");
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, kind = kindDef.defName, distance = anchor.DistanceTo(center),
                    center = new { x = center.x, z = center.z },
                    pests = pack.Select(Row).ToArray(),
                    // Who can hunt now, what is lying about, and what every colonist holds.
                    hunters = colonists.Where(Hunter).Select(p => p.GetUniqueLoadID()).ToArray(),
                    looseWeapons = weapons.Select(t => new { id = t.GetUniqueLoadID(), def = t.def.defName, forbidden = t.IsForbidden(Faction.OfPlayer), x = t.Position.x, z = t.Position.z }).ToArray(),
                    colonists = colonists.Select(p => new { id = p.GetUniqueLoadID(), weapon = p.equipment?.Primary?.def.defName,
                        ranged = p.equipment?.Primary?.def.IsRangedWeapon == true,
                        huntingActive = p.workSettings?.WorkIsActive(WorkTypeDefOf.Hunting) == true,
                        huntingDisabled = p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting),
                        shooting = p.skills?.GetSkill(SkillDefOf.Shooting)?.Level }).ToArray(),
                    rangedWeaponsOnMap = map.listerThings.AllThings.Count(t => t.def.IsRangedWeapon && !t.def.IsBuildingArtificial),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/pest_inspect", Description = "Private disposable fixture: report every staged Alphabeaver's native state (spawned, dead, downed, cell, mental state, hunt designation), how many live wild Alphabeaver remain on the map, and the colonists' dead/downed state.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null) return Refuse("A colony map is required.");
                var remaining = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.Faction == null && p.def.defName == PestKind).ToList();
                return new {
                    success = true, tick = Find.TickManager.TicksGame,
                    pests = CurrentPests().Select(Row).ToArray(),
                    remaining = remaining.Count,
                    remainingIds = remaining.Select(p => p.GetUniqueLoadID()).ToArray(),
                    colonists = map.mapPawns.FreeColonists.Select(p => new { id = p.GetUniqueLoadID(), dead = p.Dead, downed = p.Downed,
                        spawned = p.Spawned, mentalState = p.MentalStateDef?.defName, hunter = Hunter(p),
                        job = p.CurJobDef?.defName, x = p.Position.x, z = p.Position.z }).ToArray(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static bool Hunter(Pawn p) => p.equipment?.Primary?.def.IsRangedWeapon == true && NativeHuntAcquisition.OrdinaryVerbs(p.equipment.Primary.def.Verbs)
            && p.workSettings?.WorkIsActive(WorkTypeDefOf.Hunting) == true;

        private static object Row(Pawn pest) => new {
            id = pest.GetUniqueLoadID(), spawned = pest.Spawned, dead = pest.Dead, downed = pest.Downed,
            x = pest.Spawned ? pest.Position.x : -1, z = pest.Spawned ? pest.Position.z : -1,
            mentalState = pest.MentalStateDef?.defName, hostile = pest.Spawned && !pest.Dead && pest.HostileTo(Faction.OfPlayer),
            huntDesignated = pest.Spawned && pest.Map.designationManager.DesignationOn(pest, DesignationDefOf.Hunt) != null,
            // Why the hunt census would not offer it now (null when it would).
            ineligible = pest.Spawned && !pest.Dead ? NativeHuntAcquisition.Ineligible(pest) : null,
        };

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
