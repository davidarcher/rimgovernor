#nullable enable
using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class ConstructionSkillGuard
    {
        private static bool installed;
        private static ConstructionSkillState State => Current.Game.GetComponent<ConstructionSkillState>();
        internal static bool Quality(Thing t) => t.def.entityDefToBuild is ThingDef d && d.HasComp(typeof(CompQuality));
        internal static ConstructionSkillSetting? Setting(Thing t) => Current.Game == null ? null : State.Settings.FirstOrDefault(s => s.Target == t);
        internal static void Set(Thing t, int minimum)
        {
            if (!(t is Blueprint_Build || t is Frame) || !Quality(t) || t.Faction != Faction.OfPlayer || minimum < 0 || minimum > 1000)
                throw new InvalidOperationException("Invalid construction skill target or minimum.");
            var setting = Entry(t);
            if (setting.Minimum != ConstructionSkillSetting.None && setting.Minimum != minimum)
                throw new InvalidOperationException("Construction finishing skill is already set.");
            setting.Minimum = minimum;
        }
        // The tier is mutable while the target is unfinished; the finishing floor is not.
        internal static void SetTier(Thing t, int tier)
        {
            if (!(t is Blueprint_Build || t is Frame) || t.Faction != Faction.OfPlayer || tier < 0 || tier > MaxTier)
                throw new InvalidOperationException("Invalid construction tier target or tier.");
            Entry(t).Tier = tier;
        }
        internal const int MaxTier = 5;
        private static ConstructionSkillSetting Entry(Thing t)
        {
            var existing = Setting(t);
            if (existing != null) return existing;
            State.Settings.RemoveAll(s => s.Target == null || s.Target.Destroyed);
            var created = new ConstructionSkillSetting { Target = t };
            State.Settings.Add(created);
            return created;
        }
        internal static bool Allows(Thing t, Pawn pawn)
        {
            var setting = Setting(t);
            return setting == null || setting.Minimum == ConstructionSkillSetting.None
                || Capable(pawn, Math.Max(setting.Minimum, t.def.entityDefToBuild.constructionSkillPrerequisite));
        }
        internal static bool? Capability(Pawn p)
        {
            if (p.Dead || p.Downed || !p.IsColonist || p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) return false;
            var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading
                .Where(d => d.giverClass != null && typeof(WorkGiver_ConstructFinishFrames).IsAssignableFrom(d.giverClass))
                .Select(d => d.Worker as WorkGiver_ConstructFinishFrames).FirstOrDefault(w => w != null);
            return giver == null ? (bool?)null : giver.MissingRequiredCapacity(p) == null;
        }
        private static bool Capable(Pawn p, int minimum) => Capability(p) == true && p.skills?.GetSkill(SkillDefOf.Construction) is SkillRecord skill
            && !skill.TotallyDisabled && skill.Level >= minimum;
        internal static void Read(Thing t, Obs.ConstructionState row)
        {
            row.QualitySensitive = Quality(t);
            if (t.def.entityDefToBuild == null) return;
            row.NativeFinishingSkill = t.def.entityDefToBuild.constructionSkillPrerequisite;
            var setting = Setting(t);
            if (setting != null && setting.Tier != ConstructionSkillSetting.None) row.Tier = setting.Tier;
            if (setting == null || setting.Minimum == ConstructionSkillSetting.None) { if (row.QualitySensitive) row.FinishingBlocker = "minimum_not_set"; return; }
            row.MinimumFinishingSkill = setting.Minimum;
            row.EligibleFinishers = t.Map.mapPawns.FreeColonistsSpawned.Count(p => Capable(p, Math.Max(setting.Minimum, t.def.entityDefToBuild.constructionSkillPrerequisite)));
            if (row.EligibleFinishers == 0) row.FinishingBlocker = "no_qualified_builder";
        }
        internal static void Install()
        {
            if (installed) return;
            var h = new Harmony("rimgovernor.construction-skill");
            h.Patch(AccessTools.Method(typeof(WorkGiver_ConstructFinishFrames), nameof(WorkGiver_ConstructFinishFrames.HasJobOnThing)),
                postfix: new HarmonyMethod(typeof(ConstructionSkillGuard), nameof(Eligible)));
            h.Patch(AccessTools.Method(typeof(Frame), nameof(Frame.CompleteConstruction)),
                prefix: new HarmonyMethod(typeof(ConstructionSkillGuard), nameof(Complete)));
            h.Patch(AccessTools.Method(typeof(Blueprint_Build), "MakeSolidThing"),
                postfix: new HarmonyMethod(typeof(ConstructionSkillGuard), nameof(Transfer)));
            foreach (var type in new[] { typeof(Frame), typeof(Blueprint_Build) })
                h.Patch(AccessTools.Method(type, nameof(Thing.GetInspectString)),
                    postfix: new HarmonyMethod(typeof(ConstructionSkillGuard), nameof(Inspect)));
            installed = true;
        }
        private static void Eligible(Pawn pawn, Thing t, ref bool __result)
        { if (__result && !Allows(t, pawn)) { __result = false; JobFailReason.Is("construction finishing skill below target minimum"); } }
        private static bool Complete(Frame __instance, Pawn worker)
        {
            if (Allows(__instance, worker)) return true;
            JobFailReason.Is("construction finishing skill below target minimum");
            return false;
        }
        private static void Transfer(Blueprint_Build __instance, Thing __result)
        {
            var setting = Setting(__instance);
            if (setting != null) setting.Target = __result;
        }
        private static void Inspect(Thing __instance, ref string __result)
        {
            var setting = Setting(__instance);
            if (setting == null || setting.Minimum == ConstructionSkillSetting.None || __instance.Map == null) return;
            var count = __instance.Map.mapPawns.FreeColonistsSpawned.Count(p => Allows(__instance, p));
            __result += "\nMinimum finishing Construction: " + setting.Minimum
                + (count == 0 ? " (pending: no qualified builder)" : " (qualified builders: " + count + ")");
        }
    }
}
