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
    public sealed class BerserkFixture
    {
        [Tool("test/berserk_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage tribal colonist Berserk, two melee responders and an owned bed; ordinary damage and recovery only.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused tribal8 baseline required.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                if (people.Count != 8) throw new InvalidOperationException("Expected eight baseline colonists.");
                // Normalize the saved baseline before the hut selects standing pawns.
                // Once Berserk starts, damage and recovery use ordinary game rules.
                foreach (var p in people) {
                    foreach (var h in p.health.hediffSet.hediffs.Where(h => h.def.isBad).ToList()) p.health.RemoveHediff(h);
                    if (p.InMentalState) p.MentalState.RecoverFromState();
                }
                var hut = FixtureHut.Build(map, 11);
                if (hut.People.Count != 8) throw new InvalidOperationException("Expected eight baseline colonists.");
                var squad = hut.People.Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).Take(2).ToList();
                if (squad.Count != 2) throw new InvalidOperationException("Two melee responders required.");
                var target = hut.People.First(p => !squad.Contains(p));
                foreach (var p in hut.People) {
                    p.drafter.Drafted = false;
                    p.equipment.DestroyAllEquipment();
                    p.jobs.StopAll();
                    p.Position = hut.Interior[0]; p.Notify_Teleported(true, true);
                }
                target.skills.GetSkill(SkillDefOf.Melee).Level = 0;
                // Wimp is a normal trait: pain downs the target sooner, without
                // suppressing damage, healing injuries or ending the break.
                var wimp = DefDatabase<TraitDef>.GetNamed("Wimp");
                if (!target.story.traits.HasTrait(wimp)) target.story.traits.GainTrait(new Trait(wimp));
                target.Position = hut.Interior.Last(); target.Notify_Teleported(true, true);
                for (int i = 0; i < squad.Count; i++) {
                    var pawn = squad[i];
                    pawn.skills.GetSkill(SkillDefOf.Melee).Level = 20;
                    pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MeleeWeapon_Club"), ThingDefOf.WoodLog));
                    pawn.Position = target.Position + (i == 0 ? IntVec3.West : IntVec3.South);
                    pawn.Notify_Teleported(true, true);
                }
                var spot = hut.Interior.SelectMany(c => c.GetThingList(map)).OfType<Building_Bed>().First();
                var bedCell = spot.Position;
                spot.Destroy(DestroyMode.Vanish);
                var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                bed.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(bed, bedCell, map, Rot4.North);
                bed.ForPrisoners = false;
                bed.CompAssignableToPawn.TryAssignPawn(target);
                map.areaManager.Home[bed.Position] = true;
                var wall = (Building)map.listerBuildings.allBuildingsColonist.First(b => b.def.defName == "Wall" && b.Position.DistanceTo(target.Position) < 4);
                wall.HitPoints = wall.MaxHitPoints / 2;
                map.areaManager.Home[wall.Position] = true;
                var audit = map.GetComponent<BerserkAudit>();
                if (audit == null) { audit = new BerserkAudit(map); map.components.Add(audit); }
                audit.People = hut.People; audit.Target = target; audit.Bed = bed;
                if (!target.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Berserk, forced: true, forceWake: true))
                    throw new InvalidOperationException("Berserk did not start.");
                return new { success = true, target = target.GetUniqueLoadID(), bed = bed.GetUniqueLoadID(),
                    squad = squad.Select(p => p.GetUniqueLoadID()).ToArray(), colonists = hut.People.Select(p => p.GetUniqueLoadID()).ToArray(),
                    repair = wall.GetUniqueLoadID(), repairX = wall.Position.x, repairZ = wall.Position.z };
            }, cancellationToken);
        }

        [Tool("test/berserk_audit", Description = "Read-only lifetime survival, custody, downing and owned-bed outcome for berserk acceptance.")]
        public async Task<object> Audit(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var a = Find.CurrentMap.GetComponent<BerserkAudit>();
                a.Observe();
                return new { colonists = a.People.Count, deaths = a.Deaths.ToArray(), prisoners = a.Prisoners.ToArray(),
                    downed = a.SawDowned, delivered = a.Delivered, mental = a.Target?.InMentalState,
                    alive = a.Target != null && !a.Target.Dead, colonist = a.Target?.IsColonist,
                    bed = a.Target?.CurrentBed()?.GetUniqueLoadID(), ownedBed = a.Target?.ownership?.OwnedBed?.GetUniqueLoadID() };
            }, cancellationToken);
        }
    }

    // Observation only. Keep object references so carried, despawned or dead
    // colonists cannot disappear from the verdict. No game state is changed.
    public sealed class BerserkAudit : MapComponent
    {
        public List<Pawn> People = new List<Pawn>();
        public Pawn Target;
        public Building_Bed Bed;
        public HashSet<string> Deaths = new HashSet<string>();
        public HashSet<string> Prisoners = new HashSet<string>();
        public bool SawDowned, Delivered;
        public BerserkAudit(Map map) : base(map) { }
        public override void MapComponentTick() => Observe();
        public void Observe()
        {
            foreach (var p in People) {
                if (p.Dead) Deaths.Add(p.GetUniqueLoadID());
                if (p.IsPrisoner) Prisoners.Add(p.GetUniqueLoadID());
            }
            if (Target == null) return;
            SawDowned |= Target.Downed;
            Delivered |= !Target.Dead && Target.CurrentBed() == Bed && Target.ownership.OwnedBed == Bed && !Target.InMentalState;
        }
    }
}
