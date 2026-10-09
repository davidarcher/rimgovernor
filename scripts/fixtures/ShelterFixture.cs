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
    // Disposable test setup only. Stages the shelter/* sheltering
    // cases on the blank lab: a roofed hut at the map centre (the room the
    // controller's Safe area covers), the colonists and one tame pen-free
    // dog standing outside it, and the trigger: a long ToxicFallout
    // condition or a manhunter pack at the west edge. test/shelter_stage
    // ends the trigger (the fallout ends, the pack leaves the map) and
    // reports every tracked pawn's allowed area, whether it stands in the
    // hut and its injuries. The controller holds the GABP slot while it
    // runs, so the case calls these between service runs.
    public sealed class ShelterFixture
    {
        private static readonly List<Pawn> tracked = new List<Pawn>();
        private static readonly List<Pawn> pack = new List<Pawn>();
        private static GameCondition fallout;
        private static Room hut;

        [Tool("test/shelter_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: build a roofed hut at the lab centre, stand the colonists and a tame dog outside it and start trigger=fallout|manhunter.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, string trigger, string packKind = "Tortoise", int packSize = 3)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused lab colony map is required.");
                if (trigger != "fallout" && trigger != "manhunter") return Refuse("trigger must be fallout or manhunter.");
                tracked.Clear(); pack.Clear(); fallout = null;
                var centre = map.Center;
                FixtureHut.Result built;
                try { built = FixtureHut.Build(map, 9, new IntVec3(centre.x - 4, 0, centre.z - 4), null); }
                catch (InvalidOperationException e) { return Refuse(e.Message); }
                hut = built.Room;
                // Colonists stand in a row ten cells south of the hut.
                var i = 0;
                foreach (var pawn in built.People)
                {
                    var cell = new IntVec3(centre.x - 2 + 2 * i++, 0, centre.z - 10);
                    pawn.jobs?.StopAll();
                    pawn.Position = cell; pawn.Notify_Teleported(true, true);
                    tracked.Add(pawn);
                }
                var dogKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Husky");
                if (dogKind == null) return Refuse("The Husky PawnKindDef is unavailable.");
                var dog = PawnGenerator.GeneratePawn(dogKind, player);
                GenSpawn.Spawn(dog, new IntVec3(centre.x + 4, 0, centre.z - 10), map);
                if (dog.playerSettings == null || dog.RaceProps.Roamer) { dog.Destroy(DestroyMode.Vanish); return Refuse("The dog takes no allowed area or needs a pen."); }
                tracked.Add(dog);
                if (trigger == "fallout")
                {
                    fallout = GameConditionMaker.MakeCondition(DefDatabase<GameConditionDef>.GetNamed("ToxicFallout"), 180000);
                    map.gameConditionManager.RegisterCondition(fallout);
                }
                else
                {
                    var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(packKind);
                    if (kind == null || !kind.RaceProps.Animal) return Refuse("packKind must name an animal PawnKindDef.");
                    for (var n = 0; n < Math.Max(1, packSize); n++)
                    {
                        var animal = PawnGenerator.GeneratePawn(kind, null);
                        GenSpawn.Spawn(animal, CellFinder.StandableCellNear(new IntVec3(3, 0, centre.z + 2 * n), map, 3), map);
                        if (!animal.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true))
                        { animal.Destroy(DestroyMode.Vanish); return Refuse("The pack animal did not turn manhunter."); }
                        pack.Add(animal);
                    }
                }
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, trigger, hut = built.Summary(),
                    colonists = built.People.Select(p => p.GetUniqueLoadID()).ToList(), animal = dog.GetUniqueLoadID(),
                    pack = pack.Select(p => p.GetUniqueLoadID()).ToList(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/shelter_stage", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: op=status reports the tracked pawns' areas, positions and injuries; op=end ends the fallout or takes the manhunter pack off the map.")]
        public async Task<object> Stage(IRimBridgeContext ctx, CancellationToken cancellationToken, string op)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || tracked.Count == 0) return Refuse("Run test/shelter_prepare first.");
                switch (op)
                {
                    case "status": return Status(map);
                    case "end":
                        if (fallout != null && !fallout.Expired) fallout.End();
                        // The pack leaves the map: vanished, as an exit does.
                        foreach (var animal in pack.Where(p => p.Spawned).ToList()) animal.ExitMap(false, Rot4.West);
                        return Status(map);
                    default: return Refuse("Unknown op " + op + ".");
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Status(Map map)
        {
            var pawns = tracked.Select(p => new {
                id = p.GetUniqueLoadID(), animal = p.RaceProps.Animal, spawned = p.Spawned, dead = p.Dead,
                area = p.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label ?? "",
                inHut = p.Spawned && hut != null && p.GetRoom() == hut,
                x = p.Position.x, z = p.Position.z,
                injuries = p.health.hediffSet.hediffs.Count(h => h is Hediff_Injury),
            }).ToList();
            return new {
                success = true, tick = Find.TickManager.TicksGame,
                fallout = map.gameConditionManager.ConditionIsActive(GameConditionDefOf.ToxicFallout),
                packOnMap = pack.Count(p => p.Spawned && !p.Dead), pawns,
            };
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
