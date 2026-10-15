#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimGovernor.Runtime;
using RimWorld;
using Verse;
using Verse.AI;
namespace HomeBridge.BridgeTools;

public sealed class MaintenancePracticeFixture
{
    private static Pawn? holder;
    private static Precept_Role? role;
    private static SkillDef skill = null!;
    private static int floor, started;
    private static bool patched, earned, interrupt, drafted, restored, released;
    private static readonly HashSet<string> methods = new(StringComparer.Ordinal);
    private static string realWeapon = "";
    private static Map? map;

    [Tool("test/maintenance_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare an actual assigned native combat role just below its minimum, with practice facilities and no selected activity. Test builds only.")]
    public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken ct, bool melee = false)
    {
        var center = await ctx.MainThread.InvokeAsync(() => Find.CurrentMap.Center, ct);
        if (melee) await new SparringFixture().Prepare(ctx, ct, center.x, center.z, 2, levels: "5,5", markers: 2, far: 2);
        else await new TrainingFixture().Prepare(ctx, ct, center.x, center.z, level: 5);
        return await ctx.MainThread.InvokeAsync<object>(() =>
        {
            map = Find.CurrentMap;
            skill = melee ? SkillDefOf.Melee : SkillDefOf.Shooting;
            var ideology = Faction.OfPlayer.ideos.PrimaryIdeo;
            if (!ModsConfig.IdeologyActive || ideology == null) throw new InvalidOperationException("Ideology required for assigned role evidence.");
            var definition = DefDatabase<PreceptDef>.AllDefs.First(d => d.roleRequirements?.OfType<RoleRequirement_MinSkillAny>()
                .Any(r => r.skills.Any(s => s.skill == skill)) == true);
            role = ideology.PreceptsListForReading.OfType<Precept_Role>().FirstOrDefault(r => r.def == definition)
                ?? (Precept_Role)PreceptMaker.MakePrecept(definition);
            if (!ideology.PreceptsListForReading.Contains(role)) ideology.AddPrecept(role, true);
            holder = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)
                && !p.skills.GetSkill(skill).TotallyDisabled).OrderBy(p => p.thingIDNumber).First();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                pawn.ideo.SetIdeo(ideology);
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                pawn.drafter.Drafted = false;
                foreach (var need in pawn.needs.AllNeeds) need.CurLevel = need.MaxLevel;
                foreach (var work in DefDatabase<WorkTypeDef>.AllDefs.Where(w => !pawn.WorkTypeIsDisabled(w))) pawn.workSettings.SetPriority(work, 0);
            }
            floor = definition.roleRequirements.OfType<RoleRequirement_MinSkillAny>().SelectMany(r => r.skills).First(s => s.skill == skill).minLevel;
            holder.skills.GetSkill(skill).Level = floor;
            role.Assign(holder, true);
            SetDeficit();
            var hour = GenLocalDate.DayPercent(holder) * 24;
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + (int)Math.Round(((12 - hour + 24) % 24) * GenDate.TicksPerHour));
            started = Find.TickManager.TicksGame; realWeapon = holder.equipment.Primary?.def.defName ?? "";
            methods.Clear(); earned = interrupt = drafted = restored = released = false;
            if (!patched)
            {
                new Harmony("rimgovernor.fixture.maintenance").Patch(AccessTools.Method(typeof(JobDriver), "DriverTick"),
                    postfix: new HarmonyMethod(typeof(MaintenancePracticeFixture), nameof(Sample)));
                patched = true;
            }
            return new { ready = true, pawn = holder.GetUniqueLoadID(), role = role.GetUniqueLoadID(), skill = skill.defName, floor };
        }, ct);
    }
    private static void SetDeficit()
    {
        var record = holder!.skills.GetSkill(skill);
        record.Level = Math.Max(0, floor - 1);
        record.xpSinceLastLevel = SkillRecord.XpRequiredToLevelUpFrom(record.levelInt) - 1;
        record.xpSinceMidnight = 0;
    }
    private static void Sample(JobDriver __instance)
    {
        if (holder == null || __instance.pawn != holder || holder.Map != map) return;
        var read = NativePawnActivities.Read(holder);
        if (read?.Method.StartsWith("coupled-", StringComparison.Ordinal) == true && holder.CurJobDef?.defName is "RimGovernor_Spar" or "RimGovernor_TrainShooting")
        {
            methods.Add(read.Method);
            earned |= holder.skills.GetSkill(skill).Level >= floor;
            if (interrupt && holder.equipment.Primary?.def.defName != realWeapon)
            { holder.drafter.Drafted = true; drafted = true; interrupt = false; }
        }
        if (drafted && holder.equipment.Primary?.def.defName == realWeapon) restored = true;
    }
    [Tool("test/maintenance_read", Description="Read normal planner Method attribution, actual capability and weapon restoration. Test builds only.")]
    public async Task<object> Read(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>
    {
        if (holder == null) throw new InvalidOperationException("Prepare first.");
        if (drafted && holder.equipment.Primary?.def.defName == realWeapon) restored = true;
        return new { tick = Find.TickManager.TicksGame, elapsed = Find.TickManager.TicksGame - started,
            methods = methods.ToArray(), earned, level = holder.skills.GetSkill(skill).Level, floor, drafted, restored,
            released, activity = NativePawnActivities.Read(holder)?.State ?? "unowned", job = holder.CurJobDef?.defName ?? "" };
    }, ct);
    [Tool("test/maintenance_disturb", Description="UNSAFE FOR MODEL EXECUTION. Rearm below-floor maintenance and draft during native gear swap, or remove the assigned role. No work is selected. Test builds only.")]
    public async Task<object> Disturb(IRimBridgeContext ctx, CancellationToken ct, string action) => await ctx.MainThread.InvokeAsync<object>(() =>
    {
        if (holder == null || role == null) throw new InvalidOperationException("Prepare first.");
        if (action == "interrupt") { SetDeficit(); interrupt = true; }
        else if (action == "release") { role.Unassign(holder, true); role.ideo.RemovePrecept(role); holder.drafter.Drafted = false; released = true; }
        else throw new ArgumentException("Unknown disturbance.");
        return new { success = true };
    }, ct);
}
