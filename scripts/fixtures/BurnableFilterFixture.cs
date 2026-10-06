using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Disposable setup and read-back for the burnable / not-burnable special
    // filters (#2181; RimGovernorBurnable and RimGovernorNotBurnable, the
    // worker rule in RimGovernor.Runtime BurnableRule). On a blank lab it
    // builds two adjacent stockpiles, a Low "dump" disallowing not-burnable
    // and a Normal "store" disallowing burnable, stages loose items outside
    // both, and leaves one colonist hauling while the rest wait, so RimWorld's
    // own hauling decides where each item goes. The read op reports where each
    // item ended and what the filters and MarketValue said about it.
    public sealed class BurnableFilterFixture
    {
        private const int HoldTicks = 120000;
        private const int ZoneWidth = 5, ZoneHeight = 3;

        [Tool("test/burnable_stage", Description = "UNSAFE FOR MODEL EXECUTION. Disposable fixture (#2181): on the blank lab, create a Low dump stockpile disallowing not-burnable beside a Normal stockpile disallowing burnable, stage rotted and fresh rottables, a rotted colonist corpse, a rotted stranger corpse, gear probes of one apparel def varied by hit points, quality and tainted, cheap and valuable weapons and a Steel stack on loose cells, and leave one colonist hauling while the rest wait.")]
        public async Task<object> Stage(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => await ctx.MainThread.InvokeAsync<object>(() =>
            {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded lab map is required.");
                var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
                if (people.Count < 1) throw new InvalidOperationException("At least one colonist is required.");
                var hauler = people[0];
                var origin = hauler.Position;
                var burnable = DefDatabase<SpecialThingFilterDef>.GetNamed("RimGovernorBurnable");
                var notBurnable = DefDatabase<SpecialThingFilterDef>.GetNamed("RimGovernorNotBurnable");

                Zone_Stockpile MakeZone(int firstZ, StoragePriority priority, SpecialThingFilterDef disallowed)
                {
                    var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                    map.zoneManager.RegisterZone(zone);
                    var settings = zone.GetStoreSettings();
                    settings.filter.SetAllowAll(null);
                    settings.filter.SetAllow(disallowed, false);
                    settings.Priority = priority;
                    for (var x = 0; x < ZoneWidth; x++)
                        for (var z = 0; z < ZoneHeight; z++)
                            zone.AddCell(new IntVec3(origin.x + 4 + x, 0, origin.z + firstZ + z));
                    return zone;
                }
                var store = MakeZone(3, StoragePriority.Normal, burnable);
                var dump = MakeZone(3 + ZoneHeight, StoragePriority.Low, notBurnable);

                var items = new List<object>();
                var nextX = origin.x - 10;
                var rowZ = origin.z - 6;
                void Record(string label, Thing thing) => items.Add(new { label, id = thing.GetUniqueLoadID() });
                IntVec3 NextCell() => new IntVec3(nextX++, 0, rowZ);
                Thing Spawn(string label, Thing thing)
                {
                    var cell = NextCell();
                    GenSpawn.Spawn(thing, cell, map);
                    if (!thing.Spawned || thing.Position != cell) throw new InvalidOperationException($"{label} did not spawn at {cell}.");
                    thing.SetForbidden(false, false);
                    Record(label, thing);
                    return thing;
                }
                ThingDef Def(string name) => DefDatabase<ThingDef>.GetNamed(name);
                Thing Make(string defName, string stuff = null, int count = 1)
                {
                    var def = Def(defName);
                    var thing = ThingMaker.MakeThing(def, stuff == null ? (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null) : Def(stuff));
                    thing.stackCount = Math.Max(1, Math.Min(count, def.stackLimit));
                    return thing;
                }
                void Rot(Thing thing, bool rotted)
                {
                    var rot = thing.TryGetComp<CompRottable>() ?? throw new InvalidOperationException($"{thing.def.defName} is not rottable.");
                    rot.RotProgress = rotted ? rot.PropsRot.TicksToRotStart + 5000f : 0f;
                }
                Thing Corpse(string label, string kind, Faction faction, bool rotted)
                {
                    var cell = NextCell();
                    var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed(kind), faction, forceGenerateNewPawn: true,
                        canGeneratePawnRelations: false, allowAddictions: false));
                    GenSpawn.Spawn(pawn, cell, map);
                    pawn.Kill(null);
                    var corpse = pawn.Corpse ?? throw new InvalidOperationException($"{label}: no corpse.");
                    if (corpse.Spawned && corpse.Position != cell)
                    {
                        corpse.DeSpawn(DestroyMode.Vanish);
                        GenSpawn.Spawn(corpse, cell, map);
                    }
                    Rot(corpse, rotted);
                    corpse.SetForbidden(false, false);
                    Record(label, corpse);
                    return corpse;
                }

                Rot(Spawn("potatoes_rotted", Make("RawPotatoes", count: 10)), true);
                Spawn("potatoes_fresh", Make("RawPotatoes", count: 10));
                Corpse("corpse_animal_rotted", "Muffalo", null, true);
                Corpse("corpse_animal_fresh", "Muffalo", null, false);
                Corpse("corpse_stranger_rotted", "Colonist", null, true);
                Corpse("corpse_colonist_rotted", "Colonist", Faction.OfPlayer, true);

                // One apparel def, varied one factor at a time, so the read
                // shows which of hit points, quality and tainted MarketValue folds in.
                Thing Pants(string label, int? hitPoints = null, QualityCategory? quality = null, bool tainted = false)
                {
                    var pants = Make("Apparel_Pants", "Cloth");
                    if (hitPoints.HasValue) pants.HitPoints = hitPoints.Value;
                    if (quality.HasValue) pants.TryGetComp<CompQuality>()?.SetQuality(quality.Value, ArtGenerationContext.Colony);
                    if (tainted)
                    {
                        var field = typeof(Apparel).GetField("wornByCorpseInt", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("Apparel.wornByCorpseInt not found.");
                        field.SetValue(pants, true);
                    }
                    return Spawn(label, pants);
                }
                Pants("pants_normal", quality: QualityCategory.Normal);
                Pants("pants_low_hp", hitPoints: 1, quality: QualityCategory.Normal);
                Pants("pants_awful", quality: QualityCategory.Awful);
                Pants("pants_tainted", quality: QualityCategory.Normal, tainted: true);
                Pants("pants_awful_tainted_low_hp", hitPoints: 1, quality: QualityCategory.Awful, tainted: true);

                var club = Make("MeleeWeapon_Club");
                club.HitPoints = 1;
                club.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Awful, ArtGenerationContext.Colony);
                Spawn("club_awful_low_hp", club);
                var revolver = Make("Gun_Revolver");
                revolver.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Colony);
                Spawn("revolver_normal", revolver);
                Spawn("steel_stack", Make("Steel", count: 40));

                // One hauler; the rest wait, and opportunistic hauling is off for them too.
                foreach (var pawn in people)
                    pawn.workSettings.SetPriority(WorkTypeDefOf.Hauling, pawn == hauler ? 1 : 0);
                foreach (var pawn in people.Skip(1))
                    pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, HoldTicks), JobCondition.InterruptForced);

                return new
                {
                    success = true,
                    hauler = hauler.GetUniqueLoadID(),
                    items,
                    store = store.Cells.Select(c => new { x = c.x, z = c.z }).ToArray(),
                    dump = dump.Cells.Select(c => new { x = c.x, z = c.z }).ToArray(),
                    cutoff = RimGovernor.Runtime.BurnableRule.SilverCutoff,
                };
            }, cancellationToken);

        [Tool("test/burnable_read", Description = "UNSAFE FOR MODEL EXECUTION. Disposable read (#2181): where each staged item (ids, comma separated) stands, which stockpile holds it, and what the burnable filters, MarketValue, hit points, quality and tainted flag say about it.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken, string ids)
            => await ctx.MainThread.InvokeAsync<object>(() =>
            {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded lab map is required.");
                var burnable = DefDatabase<SpecialThingFilterDef>.GetNamed("RimGovernorBurnable");
                var notBurnable = DefDatabase<SpecialThingFilterDef>.GetNamed("RimGovernorNotBurnable");
                var zones = map.zoneManager.AllZones.OfType<Zone_Stockpile>().ToList();
                var wanted = (ids ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                var wornByCorpse = typeof(Apparel).GetField("wornByCorpseInt", BindingFlags.Instance | BindingFlags.NonPublic);
                var rows = new List<object>();
                foreach (var id in wanted)
                {
                    var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == id);
                    if (thing == null) { rows.Add(new { id, present = false }); continue; }
                    var zone = thing.Spawned ? zones.FirstOrDefault(z => z.ContainsCell(thing.Position)) : null;
                    var settings = zone?.GetStoreSettings();
                    var rot = thing.TryGetComp<CompRottable>();
                    QualityCategory q;
                    var hasQuality = thing.TryGetQuality(out q);
                    rows.Add(new
                    {
                        id,
                        present = true,
                        def = thing.def.defName,
                        x = thing.Position.x,
                        z = thing.Position.z,
                        priority = settings == null ? "" : settings.Priority.ToString(),
                        marketValue = thing.GetStatValue(StatDefOf.MarketValue),
                        hitPoints = thing.def.useHitPoints ? thing.HitPoints : -1,
                        maxHitPoints = thing.def.useHitPoints ? thing.MaxHitPoints : -1,
                        quality = hasQuality ? q.ToString() : "",
                        tainted = thing is Apparel apparel && wornByCorpse != null && (bool)wornByCorpse.GetValue(apparel),
                        rotStage = rot == null ? "" : rot.Stage.ToString(),
                        burnable = burnable.Worker.Matches(thing),
                        notBurnable = notBurnable.Worker.Matches(thing),
                        canEverMatch = burnable.Worker.CanEverMatch(thing.def),
                        zoneAllows = settings != null && settings.AllowedToAccept(thing),
                    });
                }
                return new { success = true, rows };
            }, cancellationToken);
    }
}
