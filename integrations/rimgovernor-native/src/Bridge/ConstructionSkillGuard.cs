#nullable enable
using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using ConstructionTier = RimGovernor.Brain.Domain.ConstructionTier;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class ConstructionSkillGuard
    {
        private static int installed;
        private static ConstructionSkillState State => Current.Game.GetComponent<ConstructionSkillState>();
        internal static bool Quality(Thing t) => t.def.entityDefToBuild is ThingDef d && d.HasComp(typeof(CompQuality));
        internal static ConstructionSkillSetting? Setting(Thing t) => Current.Game == null ? null : State.Find(t);
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
        // A target with no site to gate (an instant building or a zero-work sleeping spot is placed
        // finished, or the cell holds a finished building) has nothing to bind: skipped, not an error (#2814).
        internal static void SetTier(Thing t, int tier)
        {
            if (t.Faction != Faction.OfPlayer || tier < 0 || tier > MaxTier)
                throw new InvalidOperationException("Invalid construction tier target or tier.");
            if (!(t is Blueprint_Build || t is Frame)) return;
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
        internal static bool Capability(Pawn p)
        {
            if (p.Dead || p.Downed || !p.IsColonist || p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) return false;
            var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading
                .Where(d => d.giverClass != null && typeof(WorkGiver_ConstructFinishFrames).IsAssignableFrom(d.giverClass))
                .Select(d => d.Worker as WorkGiver_ConstructFinishFrames).FirstOrDefault(w => w != null);
            return (giver ?? throw new InvalidOperationException("WorkGiver_ConstructFinishFrames is missing from the game's work givers.")).MissingRequiredCapacity(p) == null;
        }
        private static bool Capable(Pawn p, int minimum) => Capability(p) && p.skills?.GetSkill(SkillDefOf.Construction) is SkillRecord skill
            && !skill.TotallyDisabled && skill.Level >= minimum;
        // Read is a blueprint's or frame's build state: its material deficits and remaining work, with the finishing
        // floor and tier this guard holds for it.
        internal static Obs.ConstructionState.Build Read(Thing t, string definition, double workLeft, RimGovernor.Brain.Domain.EquatableArray<Obs.MaterialDeficit> resources)
        {
            var native = t.def.entityDefToBuild.constructionSkillPrerequisite;
            var setting = Setting(t);
            Obs.ConstructionGate gate = new Obs.ConstructionGate.Ungated();
            if (setting != null && setting.Tier != ConstructionSkillSetting.None)
            {
                if (setting.Tier < 0 || setting.Tier > MaxTier) throw new InvalidOperationException("Invalid construction tier.");
                gate = new Obs.ConstructionGate.Tiered { Tier = (ConstructionTier)setting.Tier };
            }
            var minimum = setting == null || setting.Minimum == ConstructionSkillSetting.None ? null : new Obs.FinishingMinimum
            {
                Skill = setting.Minimum,
                EligibleFinishers = t.Map.mapPawns.FreeColonistsSpawned.Count(p => Capable(p, Math.Max(setting.Minimum, native))),
            };
            return new Obs.ConstructionState.Build
            {
                Definition = definition,
                WorkLeft = workLeft,
                Resources = resources,
                QualitySensitive = Quality(t),
                NativeFinishingSkill = native,
                Minimum = minimum,
                Gate = gate,
            };
        }
        internal static void Install()
        {
            if (System.Threading.Interlocked.CompareExchange(ref installed, 1, 0) != 0) return;
            var h = new Harmony("rimgovernor.construction-skill");
            h.Patch(AccessTools.DeclaredMethod(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.HasJobOnThing)),
                postfix: new HarmonyMethod(typeof(ConstructionSkillGuard), nameof(Eligible)));
            h.Patch(AccessTools.Method(typeof(Frame), nameof(Frame.CompleteConstruction)),
                prefix: new HarmonyMethod(typeof(ConstructionSkillGuard), nameof(Complete)));
            h.Patch(AccessTools.Method(typeof(Blueprint_Build), "MakeSolidThing"),
                postfix: new HarmonyMethod(typeof(ConstructionSkillGuard), nameof(Transfer)));
            foreach (var type in new[] { typeof(Frame), typeof(Blueprint_Build) })
                h.Patch(AccessTools.Method(type, nameof(Thing.GetInspectString)),
                    postfix: new HarmonyMethod(typeof(ConstructionSkillGuard), nameof(Inspect)));
        }
        // WorkGiver_ConstructFinishFrames inherits HasJobOnThing, and Harmony only
        // patches a declared virtual, so the base method is patched and filtered.
        private static void Eligible(WorkGiver_Scanner __instance, Pawn pawn, Thing t, ref bool __result)
        {
            var start = TickProfile.Start();
            if (__result && __instance is WorkGiver_ConstructFinishFrames && !Allows(t, pawn)) { __result = false; JobFailReason.Is("construction finishing skill below target minimum"); }
            else if (__result && __instance is WorkGiver_ConstructFinishFrames && NativePawnActivities.SiteHeld(pawn, t)) { __result = false; JobFailReason.Is("a coupled order holds this site"); }
            TickProfile.Stop("guard:skill-eligible", start);
        }
        private static bool Complete(Frame __instance, Pawn worker)
        {
            if (Allows(__instance, worker)) return true;
            JobFailReason.Is("construction finishing skill below target minimum");
            return false;
        }
        private static void Transfer(Blueprint_Build __instance, Thing __result)
        {
            var setting = Setting(__instance);
            if (setting == null) return;
            setting.Target = __result;
            State.Invalidate();
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
