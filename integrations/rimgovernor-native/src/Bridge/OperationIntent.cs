#nullable enable
using RimGovernor.Host.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Why the bot is doing what it does. Actions/Apply
    /// scopes each action's purpose around its apply; jobs ordered, and
    /// blueprints, frames and designations placed, in that scope remember it.
    /// A colonist whose current job is one of those, or targets one of those
    /// cells, is on bot work: the "activity" overlay group draws its lines
    /// and target, and a new intent floats above it. Nothing is saved.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class OperationIntent
    {
        private const int MaxLength = 80;
        private const float MoteSeconds = 4f;

        [ThreadStatic] private static string? current;

        private static readonly Dictionary<Pawn, (Job Job, string Intent)> ordered = new Dictionary<Pawn, (Job, string)>();
        private static readonly Dictionary<(Map Map, IntVec3 Cell), string> marks = new Dictionary<(Map, IntVec3), string>();
        private static readonly Dictionary<Pawn, string> shown = new Dictionary<Pawn, string>();
        private static readonly Dictionary<Color, Material> lineMaterials = new Dictionary<Color, Material>();
        private static int nextPrune;

        static OperationIntent()
        {
            try
            {
                var harmony = new Harmony("rimgovernor.operation-intent");
                harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "TryTakeOrderedJob", new[] { typeof(Job), typeof(JobTag?), typeof(bool) })
                        ?? throw new MissingMethodException("Pawn_JobTracker.TryTakeOrderedJob"),
                    postfix: new HarmonyMethod(typeof(OperationIntent), nameof(Ordered)));
                harmony.Patch(AccessTools.Method(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))
                        ?? throw new MissingMethodException("DesignationManager.AddDesignation"),
                    postfix: new HarmonyMethod(typeof(OperationIntent), nameof(Designated)));
                // Blueprint and Frame SpawnSetup chain to Thing's.
                harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.SpawnSetup))
                        ?? throw new MissingMethodException("Thing.SpawnSetup"),
                    postfix: new HarmonyMethod(typeof(OperationIntent), nameof(Spawned)));
                GovernorOverlay.RegisterDrawer("activity", Draw);
            }
            catch (Exception ex)
            {
                ModLog.Error("startup", "Operation intent hooks failed: " + ex);
            }
        }

        public static IDisposable Scope(string? intent) => new IntentScope(Sanitize(intent));

        private sealed class IntentScope : IDisposable
        {
            private readonly string? prior;
            public IntentScope(string? intent) { prior = current; current = intent; }
            public void Dispose() => current = prior;
        }

        /// <summary>Printable ASCII only, whitespace collapsed, capped.</summary>
        public static string? Sanitize(string? intent)
        {
            if (string.IsNullOrEmpty(intent)) return null;
            var text = new StringBuilder(Math.Min(intent!.Length, MaxLength));
            foreach (var c in intent)
            {
                if (text.Length >= MaxLength) break;
                if (c == ' ' || char.IsWhiteSpace(c)) { if (text.Length > 0 && text[text.Length - 1] != ' ') text.Append(' '); }
                else if (c > ' ' && c < 127) text.Append(c);
            }
            var result = text.ToString().Trim();
            return result.Length == 0 ? null : result;
        }

        public enum Family { Build, Harvest, Hunt, Haul, Doctor, Combat, Other }

        /// <summary>The job's family from its JobDef and WorkGiver work type names.</summary>
        public static Family Classify(string? jobDef, string? workType)
        {
            switch (workType)
            {
                case "Construction": return Family.Build;
                case "Growing": case "PlantCutting": case "Mining": return Family.Harvest;
                case "Hunting": return Family.Hunt;
                case "Hauling": return Family.Haul;
                case "Doctor": return Family.Doctor;
            }
            switch (jobDef)
            {
                case "FinishFrame": case "PlaceNoCostFrame": case "Deconstruct": case "Uninstall": case "BuildRoof":
                case "RemoveRoof": case "SmoothFloor": case "SmoothWall": case "RemoveFloor": case "Repair": case "Install":
                    return Family.Build;
                case "CutPlant": case "CutPlantDesignated": case "Harvest": case "HarvestDesignated": case "Sow": case "Mine":
                    return Family.Harvest;
                case "Hunt": case "Slaughter": return Family.Hunt;
                case "HaulToCell": case "HaulToContainer": case "HaulToTransporter": case "CarryToCryptosleepCasket": case "Refuel":
                    return Family.Haul;
                case "TendPatient": case "Rescue": case "Capture": case "Arrest": case "FeedPatient": case "DeliverToBed":
                    return Family.Doctor;
                case "AttackMelee": case "AttackStatic": case "UseVerbOnThing": case "UseVerbOnThingStatic": case "Wait_Combat":
                    return Family.Combat;
            }
            return Family.Other;
        }

        private static Color ColorOf(Family family)
        {
            switch (family)
            {
                case Family.Build: return new Color(1f, 0.65f, 0.15f, 0.8f);
                case Family.Harvest: return new Color(0.35f, 0.85f, 0.3f, 0.8f);
                case Family.Hunt: return new Color(0.75f, 0.35f, 0.15f, 0.8f);
                case Family.Haul: return new Color(0.3f, 0.6f, 1f, 0.8f);
                case Family.Doctor: return new Color(1f, 0.55f, 0.8f, 0.8f);
                case Family.Combat: return new Color(1f, 0.15f, 0.15f, 0.85f);
                default: return new Color(0.8f, 0.8f, 0.8f, 0.7f);
            }
        }

        private static void Ordered(Pawn ___pawn, Job __0, bool __result)
        {
            if (!__result || current == null || ___pawn == null || __0 == null) return;
            ordered[___pawn] = (__0, current);
        }

        private static void Designated(DesignationManager __instance, Designation __0)
        {
            if (current == null || __0 == null || __instance?.map == null) return;
            var target = __0.target;
            if (target.HasThing) { if (target.Thing.Spawned) marks[(__instance.map, target.Thing.Position)] = current; }
            else if (target.Cell.IsValid) marks[(__instance.map, target.Cell)] = current;
        }

        private static void Spawned(Thing __instance, Map map)
        {
            if (current == null || map == null || !(__instance is Blueprint || __instance is Frame)) return;
            foreach (var cell in __instance.OccupiedRect()) marks[(map, cell)] = current;
        }

        // Intent of a pawn's current job, or null when it is not bot work.
        private static string? IntentOf(Pawn pawn, Job job)
        {
            if (ordered.TryGetValue(pawn, out var entry))
            {
                if (ReferenceEquals(entry.Job, job)) return entry.Intent;
                ordered.Remove(pawn);
            }
            var a = job.targetA;
            if (!a.IsValid) return null;
            var cell = a.HasThing ? a.Thing.Position : a.Cell;
            return marks.TryGetValue((pawn.Map, cell), out var intent) ? intent : null;
        }

        // A mark lives while its cell still holds a blueprint, frame or designation.
        private static void Prune()
        {
            if (Time.frameCount < nextPrune) return;
            nextPrune = Time.frameCount + 120;
            foreach (var key in marks.Keys.ToList())
            {
                var (map, cell) = key;
                if (map.Disposed || !cell.InBounds(map)) { marks.Remove(key); continue; }
                if (cell.GetThingList(map).Any(t => t is Blueprint || t is Frame || map.designationManager.HasMapDesignationOn(t))) continue;
                if (map.designationManager.AllDesignationsAt(cell).Count > 0) continue;
                marks.Remove(key);
            }
            foreach (var pawn in ordered.Keys.Where(p => p.Destroyed || p.CurJob != ordered[p].Job).ToList()) ordered.Remove(pawn);
            foreach (var pawn in shown.Keys.Where(p => p.Destroyed).ToList()) shown.Remove(pawn);
        }

        private static void Draw(Map map)
        {
            Prune();
            if (ordered.Count == 0 && marks.Count == 0) return;
            var view = Find.CameraDriver.CurrentViewRect.ExpandedBy(3);
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                var job = pawn.CurJob;
                var intent = job == null ? null : IntentOf(pawn, job);
                if (intent == null || job == null) continue;
                if (!shown.TryGetValue(pawn, out var last) || last != intent)
                {
                    shown[pawn] = intent;
                    MoteMaker.ThrowText(pawn.DrawPos + new Vector3(0f, 0f, 0.75f), map, intent, MoteSeconds);
                }
                var a = job.targetA;
                if (!view.Contains(pawn.Position) && !(a.IsValid && view.Contains(a.Cell))) continue;
                var material = LineMaterial(ColorOf(Classify(job.def?.defName, job.workGiverDef?.workType?.defName)));
                var from = pawn.DrawPos;
                foreach (var target in Targets(job))
                {
                    var to = target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
                    GenDraw.DrawLineBetween(from, to, material);
                    from = to;
                }
                if (a.IsValid) GenDraw.DrawTargetHighlight(a);
            }
        }

        // TargetA then the queued targets, as vanilla draws a selected pawn's lines.
        private static IEnumerable<LocalTargetInfo> Targets(Job job)
        {
            if (job.targetA.IsValid) yield return job.targetA;
            if (job.targetQueueA != null) foreach (var t in job.targetQueueA) if (t.IsValid) yield return t;
            if (job.targetQueueB != null) foreach (var t in job.targetQueueB) if (t.IsValid) yield return t;
        }

        private static Material LineMaterial(Color color)
        {
            if (!lineMaterials.TryGetValue(color, out var material))
                lineMaterials[color] = material = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.Transparent, color);
            return material;
        }
    }
}
