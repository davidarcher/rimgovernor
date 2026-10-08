using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (#2439, epic #1694): the colony
    // awakens the void monolith and disrupts it at the void node.
    // test/monolith_awaken_prepare stages the precondition the controller's
    // awaken gate (policy/monolith.go) asks for: a void monolith spawned on the
    // paused lab map at the Waking level (MonolithLevelDefOf.Waking), the
    // codex entries the VoidAwakened level def requires already discovered
    // (its own category and count, read from the def), and every colonist who
    // can fight armed with the best player-obtainable ranged weapon the defs
    // offer, so the colony's defense capacity clears the awakening waves. No
    // weapon, category or entry name is listed here. The activation, the
    // waves, the structures, the Gleaming monolith and the node are the
    // controller's and the game's.
    public sealed class MonolithAwakenFixture
    {
        [Tool("test/monolith_awaken_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture (#2439): spawn the void monolith at the Waking level on the paused lab map, discover the codex entries the awakening needs and arm the colonists. Requires Anomaly and a paused lab map.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!ModsConfig.AnomalyActive) return Refuse("Anomaly is not active.");
                var anomaly = Find.Anomaly;
                if (!anomaly.GenerateMonolith || anomaly.AmbientHorrorMode) return Refuse("The Anomaly playstyle generates no monolith.");
                if (anomaly.MonolithSpawned) return Refuse("The lab already has a monolith.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count == 0) return Refuse("The lab has no colonist.");
                var waking = MonolithLevelDefOf.Waking; var awakened = MonolithLevelDefOf.VoidAwakened;
                var category = awakened.entityCatagoryCompletionRequired;
                if (category == null) return Refuse("The awakened level def names no codex category.");

                // The monolith's cell: clear standable ground in the open, off the colonists.
                var def = ThingDefOf.VoidMonolith;
                var cell = IntVec3.Invalid;
                foreach (var c in GenRadial.RadialCellsAround(colonists[0].Position, 40, true)) {
                    if (c.DistanceTo(colonists[0].Position) < 12f) continue;
                    var rect = GenAdj.OccupiedRect(c, Rot4.North, def.size);
                    if (rect.ExpandedBy(4).Cells.All(x => x.InBounds(map) && !x.Fogged(map) && x.Standable(map) && x.GetEdifice(map) == null
                        && x.GetTerrain(map).passability != Traversability.Impassable && !x.GetThingList(map).Any(t => t is Pawn))) { cell = c; break; }
                }
                if (!cell.IsValid) return Refuse("No clear ground for the monolith.");

                // The codex: the awakened level's category, as many entries as it requires.
                var entries = DefDatabase<EntityCodexEntryDef>.AllDefsListForReading.Where(e => e.category == category)
                    .OrderBy(e => e.defName, StringComparer.Ordinal).ToList();
                var required = awakened.entityCountCompletionRequired;
                if (entries.Count < required) return Refuse("The game has " + entries.Count + " codex entries in the category and the awakening needs " + required + ".");
                foreach (var entry in entries.Take(required)) Find.EntityCodex.SetDiscovered(entry);

                // The level is set while no monolith stands, silently (no incident, pall or quest), then it is spawned.
                anomaly.SetLevel(waking, silent: true);
                var monolith = anomaly.SpawnNewMonolith(cell, map);
                if (monolith == null || !monolith.Spawned) return Refuse("The monolith did not spawn.");
                if (anomaly.Level != waking.level) return Refuse("The monolith is at level " + anomaly.Level + ", not " + waking.level + ".");
                if (!monolith.CanActivate(out var why, out _)) return Refuse("The staged monolith cannot be activated: " + why);

                // The colonists: the best ranged weapon the defs offer a colony, by damage per second.
                float Dps(ThingDef weapon) {
                    var verb = weapon.Verbs.FirstOrDefault(v => v.defaultProjectile != null);
                    if (verb == null) return 0f;
                    var cycle = verb.warmupTime + weapon.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown) + Math.Max(0, verb.burstShotCount - 1) * verb.ticksBetweenBurstShots / 60f;
                    return cycle > 0f ? verb.defaultProjectile.projectile.GetDamageAmount(1f, null) * verb.burstShotCount / cycle : 0f;
                }
                var weaponDef = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(d => d.IsRangedWeapon && d.equipmentType == EquipmentType.Primary && d.techLevel <= TechLevel.Industrial && !d.destroyOnDrop
                        && d.tradeability != Tradeability.None && d.Verbs.All(v => v.defaultProjectile != null && !v.ai_IsBuildingDestroyer && !v.onlyManualCast && v.defaultProjectile.projectile.explosionRadius <= 0f))
                    .OrderByDescending(Dps).ThenBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault();
                if (weaponDef == null) return Refuse("The game has no ranged weapon for the colony.");
                var armed = 0; var perWeaponDps = Dps(weaponDef);
                foreach (var pawn in colonists) {
                    if (pawn.WorkTagIsDisabled(WorkTags.Violent) || pawn.equipment == null) continue;
                    pawn.equipment.DestroyAllEquipment();
                    var weapon = (ThingWithComps)ThingMaker.MakeThing(weaponDef, weaponDef.MadeFromStuff ? GenStuff.DefaultStuffFor(weaponDef) : null);
                    pawn.equipment.AddEquipment(weapon);
                    armed++;
                }
                if (armed == 0) return Refuse("No colonist can fight.");

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, monolithId = monolith.GetUniqueLoadID(), level = anomaly.Level, levelDef = anomaly.LevelDef.defName,
                    nextLevelDef = anomaly.NextLevelDef?.defName, category = category.defName, entriesDiscovered = required,
                    monolithCell = new { x = cell.x, z = cell.z }, weaponDef = weaponDef.defName, weaponDps = perWeaponDps, armed,
                    raidPoints = StorytellerUtility.DefaultThreatPointsNow(map),
                    colonistIds = colonists.Select(p => p.GetUniqueLoadID()).ToArray(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/monolith_awaken_inspect", Description = "Read-only postcondition for the monolith awakening fixture: the monolith level, the questline, the void things and each colonist's state.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string colonistIds)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || !ModsConfig.AnomalyActive) return Refuse("A running Anomaly game is required.");
                var anomaly = Find.Anomaly;
                var all = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).Concat(Find.WorldPawns.AllPawnsAliveOrDead).Distinct().ToDictionary(p => p.GetUniqueLoadID(), p => p);
                var colonists = (colonistIds ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Select(id =>
                    all.TryGetValue(id, out var p) ? (object)new { id, found = true, dead = p.Dead } : new { id, found = false, dead = false }).ToArray();
                var structures = Find.Maps.SelectMany(m => m.listerThings.ThingsOfDef(ThingDefOf.VoidStructure)).ToList();
                return new {
                    success = true, tick = Find.TickManager.TicksGame, spawned = anomaly.MonolithSpawned, level = anomaly.Level,
                    levelDef = anomaly.LevelDef.defName, highestLevelReached = anomaly.HighestLevelReached, questlineEnded = anomaly.QuestlineEnded,
                    disrupted = anomaly.LevelDef == MonolithLevelDefOf.Disrupted, embraced = anomaly.LevelDef == MonolithLevelDefOf.Embraced,
                    voidStructures = structures.Count, voidStructuresActivated = structures.Count(t => t.TryGetComp<CompVoidStructure>()?.Active ?? false),
                    colonists,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
