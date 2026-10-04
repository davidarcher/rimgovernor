using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (#1747, epic #1694): a colony holds
    // and studies an entity. test/entity_hold_prepare stages a finished
    // containment cell (walls and a door of the strongest stuff the game
    // allows, fully roofed, one holding platform the defs name) and, outside
    // it, one downed hostile entity. The entity is the lowest-need entity
    // race (MinimumContainmentStrength) that the game both studies and holds
    // on a platform and lets the colony capture once downed; the platform is
    // the def with the greatest containmentFactor, as the controller's
    // catalog lookup picks it. No entity, platform or stuff name is listed
    // here. The cell's construction is #1741's planner (policy tests); what
    // is left to the controller and the game is the capture rule, the
    // carrying, the held entity's upkeep and the study work.
    public sealed class EntityHoldFixture
    {
        [Tool("test/entity_hold_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture (#1747): build a finished, roofed containment cell with one holding platform and place one downed, capturable, studiable entity outside it. Requires Anomaly and a paused lab map.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!ModsConfig.AnomalyActive) return Refuse("Anomaly is not active.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count == 0) return Refuse("The lab has no colonist.");
                if (map.listerBuildings.allBuildingsColonist.Count > 0) return Refuse("The disposable colony already has buildings; the fixture needs a blank lab.");
                var study = DefDatabase<WorkTypeDef>.GetNamedSilentFail("DarkStudy");
                if (study == null) return Refuse("The game has no DarkStudy work type.");
                var students = colonists.Where(p => !p.WorkTypeIsDisabled(study)).ToList();
                if (students.Count == 0) return Refuse("No colonist can do DarkStudy.");

                // The platform: the holder def with the greatest containmentFactor.
                var platformDef = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(d => d.thingClass != null && d.GetCompProperties<CompProperties_EntityHolderPlatform>() != null)
                    .OrderByDescending(d => d.GetCompProperties<CompProperties_EntityHolder>()?.containmentFactor ?? 0f)
                    .ThenBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault();
                if (platformDef == null) return Refuse("The game has no holding platform def.");
                foreach (var research in platformDef.researchPrerequisites ?? new List<ResearchProjectDef>())
                    Find.ResearchManager.FinishProject(research, false);

                // The entity kinds, lowest need first.
                float Need(ThingDef race) => race.statBases?.FirstOrDefault(s => s.stat == StatDefOf.MinimumContainmentStrength)?.value ?? float.NaN;
                var kinds = DefDatabase<PawnKindDef>.AllDefsListForReading
                    .Where(k => k.race != null && k.race.race != null && k.race.race.IsAnomalyEntity
                        && k.race.GetCompProperties<CompProperties_HoldingPlatformTarget>() != null && k.race.GetCompProperties<CompProperties_Studiable>() != null
                        && !float.IsNaN(Need(k.race)))
                    .OrderBy(k => Need(k.race)).ThenBy(k => k.defName, StringComparer.Ordinal).ToList();
                if (kinds.Count == 0) return Refuse("The game has no studiable, holdable entity kind.");

                // Clear ground near a colonist for the cell (a 9x9 block: a 7x7 room
                // inside it, the 5x5 interior holding the 3x3 platform).
                var block = CellRect.Empty;
                foreach (var c in GenRadial.RadialCellsAround(colonists[0].Position, 30, true)) {
                    var rect = CellRect.CenteredOn(c, 9, 9);
                    if (rect.ExpandedBy(5).Cells.All(x => x.InBounds(map) && !x.Fogged(map) && x.Standable(map) && x.GetEdifice(map) == null
                        && x.GetTerrain(map).passability != Traversability.Impassable && !x.GetThingList(map).Any(t => t is Pawn))) { block = rect; break; }
                }
                if (block.IsEmpty) return Refuse("No clear ground for the cell.");
                var room = block.ContractedBy(1);
                var inside = room.ContractedBy(1);
                var door = new IntVec3(room.minX, 0, room.CenterCell.z);

                ThingDef Strongest(ThingDef def) => GenStuff.AllowedStuffsFor(def)
                    .OrderByDescending(s => def.GetStatValueAbstract(StatDefOf.MaxHitPoints, s)).ThenBy(s => s.defName, StringComparer.Ordinal).FirstOrDefault();
                var wallStuff = Strongest(ThingDefOf.Wall); var doorStuff = Strongest(ThingDefOf.Door);
                if (wallStuff == null || doorStuff == null) return Refuse("The game allows no stuff for the cell's wall or door.");
                Thing Spawn(ThingDef def, ThingDef stuff, IntVec3 cell) {
                    var thing = ThingMaker.MakeThing(def, stuff);
                    if (def.CanHaveFaction) thing.SetFaction(player);
                    GenSpawn.Spawn(thing, cell, map);
                    return thing;
                }
                foreach (var cell in room.Cells) {
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    if (cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ)
                        if (cell == door) Spawn(ThingDefOf.Door, doorStuff, cell); else Spawn(ThingDefOf.Wall, wallStuff, cell);
                }
                var platform = Spawn(platformDef, null, inside.CenterCell);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                var strength = platform.GetStatValue(StatDefOf.ContainmentStrength);

                // The entity: the first kind the game lets the colony capture once downed.
                var outside = new IntVec3(door.x - 4, 0, door.z);
                var tried = new List<string>();
                Pawn entity = null; PawnKindDef chosen = null;
                foreach (var kind in kinds) {
                    var candidate = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfEntities, forceGenerateNewPawn: true));
                    GenSpawn.Spawn(candidate, outside, map);
                    HealthUtility.DamageUntilDowned(candidate, false);
                    var target = candidate.GetComp<CompHoldingPlatformTarget>();
                    string why = null;
                    if (candidate.Dead) why = "it died when downed";
                    else if (!candidate.Downed) why = "it could not be downed";
                    else if (!candidate.HostileTo(player)) why = "it is not hostile";
                    else if (target == null || !target.CanBeCaptured) why = "the game does not let the colony capture it";
                    if (why == null) { entity = candidate; chosen = kind; break; }
                    tried.Add(kind.defName + ": " + why);
                    if (!candidate.Destroyed) candidate.Destroy(DestroyMode.Vanish);
                }
                if (entity == null) return Refuse("No entity kind can be staged downed and capturable: " + string.Join("; ", tried));
                var need = entity.GetStatValue(StatDefOf.MinimumContainmentStrength);
                if (!(strength > need)) return Refuse("The staged cell holds " + strength + " and " + chosen.defName + " needs " + need + ".");

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, entityId = entity.GetUniqueLoadID(), entityKind = chosen.defName,
                    need, platformId = platform.GetUniqueLoadID(), platformDef = platformDef.defName, platformStrength = strength,
                    wallStuff = wallStuff.defName, doorStuff = doorStuff.defName,
                    room = new { x = room.minX, z = room.minZ, width = room.Width, height = room.Height },
                    entityCell = new { x = outside.x, z = outside.z }, students = students.Count,
                    colonistIds = colonists.Select(p => p.GetUniqueLoadID()).ToArray(), rejected = tried.ToArray(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/entity_hold_inspect", Description = "Read-only postcondition for the entity hold fixture: whether the entity is alive, held on the platform and how far its study has gone.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string entityId, string platformId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || !ModsConfig.AnomalyActive) return Refuse("A running Anomaly game is required.");
                var entity = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.GetUniqueLoadID() == entityId);
                if (entity == null) return new { success = true, found = false, tick = Find.TickManager.TicksGame };
                var target = entity.GetComp<CompHoldingPlatformTarget>();
                var study = entity.GetComp<CompStudiable>();
                var platform = entity.MapHeld?.listerBuildings.allBuildingsColonist.OfType<Building_HoldingPlatform>().FirstOrDefault(b => b.GetUniqueLoadID() == platformId);
                return new {
                    success = true, found = true, tick = Find.TickManager.TicksGame, dead = entity.Dead, downed = entity.Downed,
                    held = target != null && target.CurrentlyHeldOnPlatform, onPlatform = platform != null && platform.HeldPawn == entity,
                    escaping = target != null && target.isEscaping,
                    studyEnabled = study != null && study.studyEnabled, studyInteractions = study?.studyInteractions ?? 0,
                    studyPoints = study?.studyPoints ?? 0f, anomalyKnowledgeGained = study?.anomalyKnowledgeGained ?? 0f,
                    currentlyStudiable = study != null && study.CurrentlyStudiable(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
