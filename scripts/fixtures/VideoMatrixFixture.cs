using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable acceptance setup for issue #21 stage D: colonists spanning
    // body types, apparel and weapon kinds for the pawn feeds, pawn removal,
    // and a frame-time / tick sampler that measures what live feeds cost.
    public sealed class VideoMatrixFixture
    {
        [Tool("test/video_matrix_fixture", Description = "spawn varied colonists, remove one, or sample frame time and ticks. Test builds only.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "spawn, remove, sample_start or sample_stop")] string mode = "spawn",
            [ToolParameter(Description = "Colonist load id for remove.")] string pawnId = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                switch (mode)
                {
                    case "spawn": return Spawn();
                    case "remove": return Remove(pawnId);
                    case "sample_start": return VideoMatrixSampler.Start();
                    case "sample_stop": return VideoMatrixSampler.Stop();
                    default: throw new ArgumentException("mode must be spawn, remove, sample_start or sample_stop");
                }
            }, cancellationToken);
        }

        // One colonist per row: body type, worn apparel, primary weapon. Each
        // is a different draw for the pawn renderer the feed camera captures.
        static readonly (string body, string[] apparel, string weapon)[] Variants =
        {
            ("Hulk", new[] { "Apparel_FlakVest", "Apparel_SimpleHelmet" }, "Gun_BoltActionRifle"),
            ("Thin", new[] { "Apparel_Parka" }, "MeleeWeapon_LongSword"),
            ("Fat", new[] { "Apparel_Duster", "Apparel_CowboyHat" }, "Gun_Revolver"),
            ("Female", new[] { "Apparel_BasicShirt", "Apparel_Pants" }, ""),
            ("Male", new[] { "Apparel_TribalA" }, "MeleeWeapon_Knife"),
        };

        static object Spawn()
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("No current map");
            var anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
            var spawned = new List<object>();
            foreach (var variant in Variants)
            {
                var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, colonistRelationChanceFactor: 0f, allowDead: false, allowDowned: false,
                    canGeneratePawnRelations: false, mustBeCapableOfViolence: true, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f,
                    fixedGender: variant.body == "Female" ? Gender.Female : variant.body == "Male" ? Gender.Male : (Gender?)null));
                pawn.story.bodyType = DefDatabase<BodyTypeDef>.GetNamed(variant.body);
                foreach (var worn in pawn.apparel.WornApparel.ToList()) worn.Destroy();
                foreach (var name in variant.apparel)
                {
                    var def = DefDatabase<ThingDef>.GetNamed(name);
                    var apparel = (Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Cloth : null);
                    pawn.apparel.Wear(apparel, false);
                }
                pawn.equipment.DestroyAllEquipment();
                if (variant.weapon != "")
                {
                    var def = DefDatabase<ThingDef>.GetNamed(variant.weapon);
                    pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null));
                }
                var cell = GenRadial.RadialCellsAround(anchor, 12, false)
                    .First(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && c.GetFirstPawn(map) == null);
                GenSpawn.Spawn(pawn, cell, map);
                pawn.Drawer.renderer.SetAllGraphicsDirty();
                spawned.Add(new
                {
                    pawnId = pawn.GetUniqueLoadID(), label = pawn.LabelShort, bodyType = variant.body,
                    apparel = pawn.apparel.WornApparel.Select(a => a.def.defName).ToList(),
                    weapon = pawn.equipment.Primary?.def.defName ?? "",
                    x = pawn.Position.x, z = pawn.Position.z,
                });
            }
            return new { success = true, pawns = spawned };
        }

        static object Remove(string pawnId)
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("No current map");
            var pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId)
                ?? throw new InvalidOperationException("Pawn is not spawned on the current map: " + pawnId);
            pawn.Destroy(DestroyMode.Vanish);
            return new { success = true, pawnId, destroyed = pawn.Destroyed };
        }
    }

    // Samples the rendered frame interval and game ticks between start and
    // stop; the difference between configurations is the feeds' cost.
    public sealed class VideoMatrixSampler : MonoBehaviour
    {
        static VideoMatrixSampler instance;
        readonly List<double> frameMs = new List<double>();
        int ticksAtStart;
        float startedAt;

        internal static object Start()
        {
            if (instance == null)
            {
                var host = new GameObject("RimGovernorVideoMatrixSampler");
                DontDestroyOnLoad(host);
                instance = host.AddComponent<VideoMatrixSampler>();
            }
            instance.frameMs.Clear();
            instance.ticksAtStart = Find.TickManager.TicksGame;
            instance.startedAt = Time.realtimeSinceStartup;
            instance.enabled = true;
            return new { success = true, ticks = instance.ticksAtStart };
        }

        internal static object Stop()
        {
            if (instance == null || !instance.enabled) throw new InvalidOperationException("Sampler is not running");
            instance.enabled = false;
            var wall = Time.realtimeSinceStartup - instance.startedAt;
            var ticks = Find.TickManager.TicksGame - instance.ticksAtStart;
            var sorted = instance.frameMs.OrderBy(v => v).ToList();
            double At(double q) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
            return new
            {
                success = true, wallSeconds = wall, frames = sorted.Count, ticks,
                ticksPerSecond = wall > 0 ? ticks / wall : 0, framesPerSecond = wall > 0 ? sorted.Count / wall : 0,
                frameMsMean = sorted.Count == 0 ? 0 : sorted.Average(), frameMsP50 = At(0.5), frameMsP95 = At(0.95), frameMsMax = At(1),
                paused = Find.TickManager.Paused, speed = Find.TickManager.CurTimeSpeed.ToString(),
            };
        }

        void Update()
        {
            frameMs.Add(Time.unscaledDeltaTime * 1000.0);
        }
    }
}
