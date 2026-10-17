#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

/// <summary>Disposable preconditions and passive evidence only. Never installs a selected activity.</summary>
public sealed class SupportPortfolioFixture
{
    private const int CareServiceBudgetTicks = 2500;
    private const int ConditionedBurnSeed = 312735;
    private static int initialInfectionCheckTicks;
    private static string routineTendGiver = "";
    private static Map map = null!;
    private static Pawn patient = null!;
    private static Building_WorkTable? bench;
    private static Bill_Production? bill;
    private static Thing? source;
    private static ThingDef? product;
    private static string scenario = "";
    private static IntVec3 staging;
    private static int start, tendAt, required, initialSource, lastTick, cleanTick, recipeTick, careTick;
    private static bool patched, active, pickedUp, produced, cleaned;
    private static readonly List<Thing> filth = new();
    private static readonly HashSet<string> methods = new(StringComparer.Ordinal);
    private static readonly Dictionary<string,int> criticalJobs = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> participantTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> obligations = new(StringComparer.Ordinal);
    private static float initialCleanliness;

    [Tool("test/support_bench_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare dirty enclosed cooking station, ingredients and hunger; install no chosen work.")]
    public Task<object> Bench(IRimBridgeContext ctx, CancellationToken ct) => Prepare(ctx,ct,"bench");
    [Tool("test/support_care_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare dirty care room and bleeding patient with no-medicine care; install no chosen treatment.")]
    public Task<object> Care(IRimBridgeContext ctx, CancellationToken ct) => Prepare(ctx,ct,"care");
    [Tool("test/support_routine_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare nonbleeding tendable injury and useful room cleaning for normal coupled treatment.")]
    public Task<object> Routine(IRimBridgeContext ctx, CancellationToken ct) => Prepare(ctx,ct,"routine");

    private static async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken ct, string kind)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            active=false; map=Find.CurrentMap;
            if(map==null || !Find.TickManager.Paused || map.Size.x!=100) throw new InvalidOperationException("Paused disposable LabStart required.");
            scenario=kind; start=lastTick=Find.TickManager.TicksGame; tendAt=-1; cleanTick=recipeTick=careTick=-10000;
            initialInfectionCheckTicks=0;
            routineTendGiver="";
            bench=null; bill=null; source=null; product=null; required=0;
            pickedUp=produced=cleaned=false;
            filth.Clear(); methods.Clear(); criticalJobs.Clear(); participantTicks.Clear(); obligations.Clear();
            var pawns=map.mapPawns.FreeColonistsSpawned.OrderBy(p=>p.thingIDNumber).Take(3).ToArray();
            if(pawns.Length!=3) throw new InvalidOperationException("Three colonists required.");
            var center=map.Center;
            foreach(var cell in new CellRect(center.x-18,center.z-12,37,25).Cells)
            {
                foreach(var thing in cell.GetThingList(map).Where(t=>t is not Pawn).ToArray()) thing.Destroy();
                map.areaManager.Home[cell]=true;
                map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);
                map.roofGrid.SetRoof(cell,null);
            }
            foreach(var zone in map.zoneManager.AllZones.ToArray()) map.zoneManager.DeregisterZone(zone);
            foreach(var pawn in pawns)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                pawn.drafter.Drafted=false;
                pawn.Position=center+new IntVec3(-3,0,-3+Array.IndexOf(pawns,pawn)); pawn.Notify_Teleported(true,true);
                pawn.needs.food.CurLevelPercentage=.95f; pawn.needs.rest.CurLevelPercentage=.95f; pawn.needs.joy.CurLevelPercentage=.95f;
                foreach(var type in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t=>!pawn.WorkTypeIsDisabled(t))) pawn.workSettings.SetPriority(type,0);
                foreach(var skill in pawn.skills.skills) skill.Level=pawn==pawns[0]?16:5;
                foreach(var thing in pawn.inventory.innerContainer.ToArray()) thing.Destroy();
            }
            // Closed room with a real door; native room cleanliness, not a fixture benefit constant.
            var room=new CellRect(center.x-3,center.z-3,7,7);
            foreach(var cell in room.Cells)
            {
                if(cell.x==room.minX || cell.x==room.maxX || cell.z==room.minZ || cell.z==room.maxZ)
                {
                    var def=cell==new IntVec3(room.minX,0,center.z)?ThingDefOf.Door:ThingDefOf.Wall;
                    var wall=ThingMaker.MakeThing(def,ThingDefOf.WoodLog); wall.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(wall,cell,map);
                }
                map.roofGrid.SetRoof(cell,RoofDefOf.RoofConstructed);
            }
            patient=pawns[2];
            if(kind is "care" or "routine")
            {
                var bed=(Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed,ThingDefOf.WoodLog);
                bed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bed,center,map); bed.Medical=true;
                patient.playerSettings.medCare=MedicalCareCategory.NoMeds;
                patient.Position=bed.Position; patient.Notify_Teleported(true,true);
                var torso=patient.health.hediffSet.GetNotMissingParts().First(p=>p.def.defName=="Torso");
                if(kind=="routine")
                {
                    // One conditioned native burn, not a claim about infection incidence.
                    // Restore the surrounding simulation RNG; never edit its pending timer.
                    var burn=HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed("Burn"),patient,torso); burn.Severity=5f;
                    Rand.PushState(ConditionedBurnSeed);
                    try { patient.health.AddHediff(burn,torso); }
                    finally { Rand.PopState(); }
                    var infecter=burn.TryGetComp<HediffComp_Infecter>();
                    initialInfectionCheckTicks=infecter==null?0:Traverse.Create(infecter).Field("ticksUntilInfect").GetValue<int>();
                    if(initialInfectionCheckTicks<=CareServiceBudgetTicks || patient.health.hediffSet.BleedRateTotal>0)
                        throw new InvalidOperationException("Conditioned native burn must be nonbleeding with a pending infection check after the care budget.");
                    patient.playerSettings.selfTend=true;
                    // Regression: the first registered tend giver is urgent-other care and
                    // cannot represent this nonurgent self-service capability.
                    var firstTend=DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d=>d.Worker is WorkGiver_Tend);
                    var legalTend=NativeSupportActivities.TendGiver(patient,patient);
                    if(((WorkGiver_Tend)firstTend.Worker).HasJobOnThing(patient,patient,false) || legalTend==null)
                        throw new InvalidOperationException("Routine care requires a legal native self-tend giver beyond the first registered urgent-other giver.");
                    routineTendGiver=legalTend.defName;
                }
                else patient.TakeDamage(new DamageInfo(DamageDefOf.Cut,5f,999f,-1,null,torso));
                patient.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown,bed),JobCondition.InterruptForced);
                if(!patient.health.HasHediffsNeedingTend()) throw new InvalidOperationException("Fixture injury requires tending.");
            }
            {
                var def=ThingDef.Named("FueledStove");
                bench=(Building_WorkTable)ThingMaker.MakeThing(def,def.MadeFromStuff?ThingDefOf.WoodLog:null);
                bench.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bench,kind is "care" or "routine"?center+new IntVec3(1,0,0):center,map);
                var fuel=bench.TryGetComp<CompRefuelable>(); if(fuel!=null) fuel.Refuel(fuel.Props.fuelCapacity);
                var input=ThingDef.Named("RawRice");
                var recipe=DefDatabase<RecipeDef>.GetNamed("CookMealSimple");
                bill=(Bill_Production)recipe.MakeNewBill(); bill.repeatMode=BillRepeatModeDefOf.RepeatCount; bill.repeatCount=1;
                bill.ingredientFilter.SetDisallowAll(); bill.ingredientFilter.SetAllow(input,true);
                bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor,null); bench.BillStack.AddBill(bill);
                required=recipe.ingredients.Sum(i=>i.CountRequiredOfFor(input,recipe,bill));
                product=recipe.products[0].thingDef;
                source=GenSpawn.Spawn(input,center+new IntVec3(kind=="bench"?2:-15,0,1),map);
                source.stackCount=Math.Min(input.stackLimit,required+20); initialSource=source.stackCount;
                source.SetForbidden(false,false); staging=center+new IntVec3(-2,0,1);
                AddStore(staging,input); AddStore(center+new IntVec3(2,0,-2),product);
                foreach(var thing in map.listerThings.AllThings.Where(t=>t.def.IsNutritionGivingIngestible && t!=source && t is not Plant).ToArray()) thing.Destroy();
                pawns[0].needs.food.CurLevelPercentage=.45f;
                foreach(var pawn in pawns) pawn.foodRestriction.GetCurrentRespectedRestriction(pawn).filter.SetAllow(input,false);
            }
            foreach(var cell in room.Cells.Where(c=>c.x>room.minX && c.x<room.maxX && c.z>room.minZ && c.z<room.maxZ && c.GetEdifice(map)==null).Take(8))
            {
                if(!FilthMaker.TryMakeFilth(cell,map,ThingDef.Named("Filth_Blood"),1,FilthSourceFlags.None)) continue;
                var dirt=cell.GetThingList(map).OfType<Filth>().First(); filth.Add(dirt);
                AccessTools.Field(typeof(Filth),"growTick").SetValue(dirt,-10000);
            }
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            if(!patched)
            {
                var harmony=new Harmony("rimgovernor.fixture.support-portfolio");
                harmony.Patch(AccessTools.Method(typeof(NativeTendOperations),"Apply"),postfix:new HarmonyMethod(typeof(SupportPortfolioFixture),nameof(CriticalApplied)));
                harmony.Patch(AccessTools.Method(typeof(NativePawnActivities),"Apply"),postfix:new HarmonyMethod(typeof(SupportPortfolioFixture),nameof(Applied)));
                harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker),"JobTrackerTick"),postfix:new HarmonyMethod(typeof(SupportPortfolioFixture),nameof(Tick)));
                patched=true;
            }
            initialCleanliness=bench?.GetRoom()?.GetStat(RoomStatDefOf.Cleanliness) ?? 0;
            if((bench?.GetRoom()?.GetStat(RoomStatDefOf.FoodPoisonChance) ?? 0)<=0)
                throw new InvalidOperationException("Prepared room must have actual native cleanliness-related food contamination risk.");
            active=true;
            return new {success=true,tick=start,scenario,careServiceBudgetTicks=CareServiceBudgetTicks,required,source=source?.GetUniqueLoadID(),bill=bill?.GetUniqueLoadID(),patient=patient.GetUniqueLoadID()};
        },ct).ConfigureAwait(false);

    private static void AddStore(IntVec3 cell,ThingDef def)
    {
        var zone=new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile,map.zoneManager); map.zoneManager.RegisterZone(zone);
        zone.GetStoreSettings().filter.SetDisallowAll(); zone.GetStoreSettings().filter.SetAllow(def,true); zone.AddCell(cell);
    }
    private static void Applied(PawnActivities batch)
    {
        if(!active) return;
        foreach(var order in batch.Orders.Where(o=>o.Method.StartsWith("coupled-",StringComparison.Ordinal)))
        {
            methods.Add(order.Method);
            if(!participantTicks.ContainsKey(order.Pawn)) { participantTicks.Add(order.Pawn,0); obligations.Add(order.Pawn,"selected_not_started"); }
        }
    }
    private static void CriticalApplied(JobOrder intent)
    {
        if(!active || scenario!="care" || intent.TargetId!=patient.GetUniqueLoadID()) return;
        var doctor=map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p=>p.GetUniqueLoadID()==intent.PawnId);
        if(doctor?.CurJobDef!=JobDefOf.TendPatient || doctor.CurJob.targetA.Thing!=patient) return;
        criticalJobs[intent.PawnId]=doctor.CurJob.loadID;
        if(!participantTicks.ContainsKey(intent.PawnId)) { participantTicks.Add(intent.PawnId,0); obligations.Add(intent.PawnId,"critical_tend_dispatched"); }
    }
    private static void Tick(Pawn ___pawn)
    {
        if(!active || ___pawn.Map!=map) return;
        var execution=NativePawnActivities.Read(___pawn);
        var critical=criticalJobs.TryGetValue(___pawn.GetUniqueLoadID(),out var jobId) && ___pawn.CurJob?.loadID==jobId;
        if(execution?.Method.StartsWith("coupled-",StringComparison.Ordinal)==true || critical)
        {
            var id=___pawn.GetUniqueLoadID(); participantTicks.TryGetValue(id,out var ticks); participantTicks[id]=ticks+1;
            obligations[id]=critical?"critical_tend_active":execution!.State+":"+execution.Reason;
            if(___pawn.CurJobDef==JobDefOf.Clean) cleanTick=Find.TickManager.TicksGame;
            if(___pawn.CurJobDef==JobDefOf.DoBill) recipeTick=Find.TickManager.TicksGame;
            if((___pawn.CurJobDef==JobDefOf.TendPatient || ___pawn.jobs.curDriver is JobDriver_GovernorTend)
                && ___pawn.CurJob?.targetA.Thing==patient) careTick=Find.TickManager.TicksGame;
            if(___pawn.carryTracker.CarriedThing?.def==source?.def) pickedUp=true;
        }
        var now=Find.TickManager.TicksGame;
        if(now==lastTick) return; lastTick=now;
        if(scenario is "care" or "routine" && tendAt<0 && now-careTick<=2 && !patient.health.HasHediffsNeedingTend()) tendAt=now;
        cleaned|=now-cleanTick<=2 && filth.Count>0 && filth.Any(t=>t.Destroyed);
        produced|=now-recipeTick<=2 && product!=null && map.listerThings.AllThings.Any(t=>t.Spawned && t.def==product);
    }
    }
    [Tool("test/support_read", Description="UNSAFE FOR MODEL EXECUTION. Read actual support outcomes, all participant occupied ticks and still unfinished native obligations; writes no orders.")]
    public async Task<object> Read(IRimBridgeContext ctx,CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(()=>new
    {
        success=active,tick=Find.TickManager.TicksGame,elapsed=Find.TickManager.TicksGame-start,scenario,
        patient=patient.GetUniqueLoadID(),
        methods=methods.OrderBy(x=>x).ToArray(),participants=participantTicks.OrderBy(x=>x.Key).Select(x=>new{pawn=x.Key,occupiedTicks=x.Value,obligation=obligations[x.Key]}).ToArray(),
        unfinished=map.mapPawns.FreeColonistsSpawned.Select(p=>new{pawn=p.GetUniqueLoadID(),execution=NativePawnActivities.Read(p)}).Where(x=>x.execution!=null && methods.Contains(x.execution.Method)).ToArray(),
        participantAccounting=participantTicks.Count>0 && participantTicks.Values.All(v=>v>=0) && participantTicks.Values.Any(v=>v>0),
        cleaned=cleaned && (bench?.GetRoom()?.GetStat(RoomStatDefOf.Cleanliness) ?? initialCleanliness)>initialCleanliness,initialCleanliness,currentCleanliness=bench?.GetRoom()?.GetStat(RoomStatDefOf.Cleanliness),produced,pickedUp,required,
        tended=tendAt>=0,tendTick=tendAt,careWithinServiceBudget=tendAt>=0 && tendAt-start<=CareServiceBudgetTicks,careServiceBudgetTicks=CareServiceBudgetTicks,
        conditionedBurnSeed=scenario=="routine"?ConditionedBurnSeed:0,initialInfectionCheckTicks,selfTendEnabled=patient.playerSettings.selfTend,routineTendGiver,
        liveOwnedWorkers=map.mapPawns.FreeColonistsSpawned.Where(p=>NativePawnActivities.Read(p)?.Method is string m && methods.Contains(m)).Select(p=>new{pawn=p.GetUniqueLoadID(),drafted=p.Drafted,dead=p.Dead}).ToArray(),
        remainingSource=source is {Destroyed:false}?source.stackCount:0,initialSource
    },ct).ConfigureAwait(false);
}
