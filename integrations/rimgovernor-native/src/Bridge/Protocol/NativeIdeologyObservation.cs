#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    // The Ideology facts (#1654): the static defs for the definition catalog
    // (memes, precepts with typed effects, role precepts, ritual patterns)
    // and the primary ideoligion's current state for the snapshot frame's
    // "ideology" section. No tool answers them: the catalog rides
    // rimgovernor/observations_read_definition_catalog and the section rides
    // the frame. Both are absent without Ideology.
    internal static class NativeIdeologyObservation
    {
        // The PreceptDef booleans that switch an action rule on, named by
        // their game member (PreceptDefinition.flags).
        private static readonly (string Name, Func<PreceptDef, bool> On)[] Flags = {
            ("approvesOfSlavery", d => d.approvesOfSlavery),
            ("approvesOfCharity", d => d.approvesOfCharity),
            ("approvesOfBlindness", d => d.approvesOfBlindness),
            ("approvesOfRaiding", d => d.approvesOfRaiding),
            ("disallowLoggingCamps", d => d.disallowLoggingCamps),
            ("disallowMiningCamps", d => d.disallowMiningCamps),
            ("disallowHuntingCamps", d => d.disallowHuntingCamps),
            ("disallowFarmingCamps", d => d.disallowFarmingCamps),
        };

        private static bool IsRole(PreceptDef def) => def.preceptClass != null && typeof(Precept_Role).IsAssignableFrom(def.preceptClass);

        private static IEnumerable<string> Names<T>(IEnumerable<T>? defs) where T : Def =>
            (defs ?? Enumerable.Empty<T>()).Where(d => d != null).Select(d => Id(d.defName));

        private static IEnumerable<string> Sorted(IEnumerable<string> names) => names.OrderBy(n => n, StringComparer.Ordinal);

        internal static Obs.IdeologyCatalog? Catalog()
        {
            if (!ModsConfig.IdeologyActive) return null;
            var catalog = new Obs.IdeologyCatalog();
            foreach (var def in DefDatabase<MemeDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal))
                catalog.Memes.Add(Meme(def));
            foreach (var def in DefDatabase<PreceptDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal)) {
                if (IsRole(def)) catalog.Roles.Add(Role(def));
                else catalog.Precepts.Add(Precept(def));
            }
            foreach (var def in DefDatabase<RitualPatternDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal))
                catalog.Rituals.Add(Ritual(def));
            return catalog;
        }

        private static Obs.MemeDefinition Meme(MemeDef def)
        {
            var row = new Obs.MemeDefinition { DefName = Id(def.defName), Category = Id(def.category.ToString()), Impact = def.impact };
            row.ExclusionTags.Add(Sorted((def.exclusionTags ?? new List<string>()).Select(Id)));
            foreach (var required in def.requiredRituals ?? new List<RequiredRitualAndBuilding>()) {
                var item = new Obs.RequiredRitual();
                if (required.precept != null) item.Precept = Id(required.precept.defName);
                if (required.pattern != null) item.Pattern = Id(required.pattern.defName);
                if (required.building != null) item.Building = Id(required.building.defName);
                row.RequiredRituals.Add(item);
            }
            row.ConsumableBuildings.Add(Sorted(Names(def.consumableBuildings)));
            row.RitualSeats.Add(Sorted(Names(def.requireAnyRitualSeat)));
            return row;
        }

        private static Obs.PreceptDefinition Precept(PreceptDef def)
        {
            var row = new Obs.PreceptDefinition { DefName = Id(def.defName), Impact = Id(def.impact.ToString()), MaxCount = def.maxCount };
            if (def.preceptClass != null) row.PreceptClass = Id(def.preceptClass.Name);
            if (def.issue != null) { row.Issue = Id(def.issue.defName); row.IssueAllowsMultiple = def.issue.allowMultiplePrecepts; }
            row.RequiredMemes.Add(Sorted(Names(def.requiredMemes)));
            row.ConflictingMemes.Add(Sorted(Names(def.conflictingMemes)));
            row.AssociatedMemes.Add(Sorted(Names(def.associatedMemes)));
            row.ExclusionTags.Add(Sorted((def.exclusionTags ?? new List<string>()).Select(Id)));
            row.Flags.Add(Flags.Where(f => f.On(def)).Select(f => f.Name));
            foreach (var comp in def.comps ?? new List<PreceptComp>()) row.Effects.Add(Effect(comp));
            foreach (var chance in def.buildingDefChances ?? new List<PreceptThingChanceClass>())
                if (chance.def != null) row.Buildings.Add(new Obs.PreceptBuilding { Building = Id(chance.def.defName), Chance = Number(chance.chance) });
            if (def.ritualPatternBase != null) row.RitualPattern = Id(def.ritualPatternBase.defName);
            return row;
        }

        private static Obs.PreceptEffect Effect(PreceptComp comp)
        {
            var row = new Obs.PreceptEffect { CompClass = Id(comp.GetType().Name), Kind = Obs.PreceptEffectKind.Other };
            void Thought(ThoughtDef? thought)
            {
                if (thought == null) return;
                row.Thought = Id(thought.defName);
                row.StageMoods.Add((thought.stages ?? new List<ThoughtStage>()).Select(s => Number(s.baseMoodEffect)));
            }
            switch (comp) {
                case PreceptComp_SelfTookMemoryThought c:
                    row.Kind = Obs.PreceptEffectKind.SelfTookAction;
                    if (c.eventDef != null) row.HistoryEvent = Id(c.eventDef.defName);
                    row.OnlyForNonSlaves = c.onlyForNonSlaves;
                    Thought(c.thought);
                    break;
                case PreceptComp_KnowsMemoryThought c:
                    row.Kind = Obs.PreceptEffectKind.WitnessedAction;
                    if (c.eventDef != null) row.HistoryEvent = Id(c.eventDef.defName);
                    Thought(c.thought);
                    break;
                case PreceptComp_SituationalThought c:
                    row.Kind = Obs.PreceptEffectKind.Situational;
                    Thought(c.thought);
                    break;
                case PreceptComp_BedThought c:
                    row.Kind = Obs.PreceptEffectKind.Bed;
                    Thought(c.thought);
                    break;
                case PreceptComp_UnwillingToDo c:
                    row.Kind = Obs.PreceptEffectKind.Unwilling;
                    if (c.eventDef != null) row.HistoryEvent = Id(c.eventDef.defName);
                    row.NullifyingTraits.Add(Sorted((c.nullifyingTraits ?? new List<TraitRequirement>()).Where(t => t.def != null).Select(t => Id(t.def.defName))));
                    row.NullifyingHediffs.Add(Sorted(Names(c.nullifyingHediffs)));
                    if (c is PreceptComp_UnwillingToDo_Chance chance) row.Chance = Number(chance.chance);
                    if (c is PreceptComp_UnwillingToDo_Gendered gendered) row.Gender = Id(gendered.gender.ToString());
                    if (c is PreceptComp_UnwillingToDo_WithDef withDef && withDef.buildingDef != null) row.Building = Id(withDef.buildingDef.defName);
                    break;
                case PreceptComp_Apparel:
                    row.Kind = Obs.PreceptEffectKind.Apparel;
                    break;
                case PreceptComp_MentalBreak c:
                    row.Kind = Obs.PreceptEffectKind.MentalBreak;
                    if (c.mentalBreakDef != null) row.MentalBreak = Id(c.mentalBreakDef.defName);
                    break;
                case PreceptComp_DevelopmentPoints c:
                    row.Kind = Obs.PreceptEffectKind.DevelopmentPoints;
                    if (c.eventDef != null) row.HistoryEvent = Id(c.eventDef.defName);
                    break;
                case PreceptComp_GoodwillSituation c:
                    row.Kind = Obs.PreceptEffectKind.GoodwillSituation;
                    if (c.goodwillSituation != null) row.GoodwillSituation = Id(c.goodwillSituation.defName);
                    break;
            }
            return row;
        }

        private static IEnumerable<string> Tags(WorkTags tags) =>
            Enum.GetValues(typeof(WorkTags)).Cast<WorkTags>().Where(t => t != WorkTags.None && IsSingle(t) && (tags & t) == t).Select(t => Id(t.ToString()))
                .OrderBy(n => n, StringComparer.Ordinal);

        private static bool IsSingle(WorkTags tag) => ((int)tag & ((int)tag - 1)) == 0;

        private static Obs.RoleDefinition Role(PreceptDef def)
        {
            var row = new Obs.RoleDefinition { DefName = Id(def.defName), Leader = def.leaderRole, MaxCount = def.maxCount,
                ActivationBelieverCount = def.activationBelieverCount, DeactivationBelieverCount = def.deactivationBelieverCount };
            row.DisabledWorkTags.Add(Tags(def.roleDisabledWorkTags));
            row.RequiredWorkTags.Add(Tags(def.roleRequiredWorkTags));
            row.RequiredWorkTagAny.Add(Tags(def.roleRequiredWorkTagAny));
            foreach (var requirement in def.roleRequirements ?? new List<RoleRequirement>()) {
                var item = new Obs.RoleRequirementFact { RequirementClass = Id(requirement.GetType().Name) };
                if (requirement is RoleRequirement_MinSkillAny any)
                    foreach (var skill in any.skills ?? new List<SkillRequirement>())
                        item.Skills.Add(new Obs.SkillRequirementFact { Skill = Id(skill.skill.defName), MinLevel = skill.minLevel });
                row.Requirements.Add(item);
            }
            foreach (var effect in def.roleEffects ?? new List<RoleEffect>()) {
                var item = new Obs.RoleEffectFact { EffectClass = Id(effect.GetType().Name), Bad = effect.IsBad };
                if (effect is RoleEffect_PawnStatModifier stat && stat.statDef != null) { item.Stat = Id(stat.statDef.defName); item.Modifier = Number(stat.modifier); }
                row.Effects.Add(item);
            }
            row.GrantedAbilities.Add(Sorted(Names(def.grantedAbilities)));
            row.Tags.Add(Sorted((def.roleTags ?? new List<string>()).Select(Id)));
            return row;
        }

        private static Obs.RitualDefinition Ritual(RitualPatternDef def)
        {
            var row = new Obs.RitualDefinition { DefName = Id(def.defName), IntervalDaysMin = Number(def.ritualFreeStartIntervalDaysRange.min), IntervalDaysMax = Number(def.ritualFreeStartIntervalDaysRange.max),
                CanStartAnytime = def.canStartAnytime, AlwaysStartAnytime = def.alwaysStartAnytime, IdeoMembersOnly = def.ritualOnlyForIdeoMembers,
                MinTechLevel = Id(def.minTechLevel.ToString()), MaxTechLevel = Id(def.maxTechLevel.ToString()) };
            row.ObligationTriggers.Add((def.ritualObligationTriggers ?? new List<RitualObligationTriggerProperties>()).Where(t => t.triggerClass != null).Select(t => Id(t.triggerClass.Name)));
            if (def.ritualObligationTargetFilter != null) {
                row.ObligationTargetFilter = Id(def.ritualObligationTargetFilter.defName);
                row.RequiredBuildings.Add(Sorted(Names(def.ritualObligationTargetFilter.thingDefs)));
            }
            if (def.ritualTargetFilter != null) row.TargetFilter = Id(def.ritualTargetFilter.defName);
            if (def.ritualBehavior != null) {
                row.Behavior = Id(def.ritualBehavior.defName);
                foreach (var role in def.ritualBehavior.roles ?? new List<RitualRole>()) {
                    var slot = new Obs.RitualRoleSlot { Id = Id(role.id), MaxCount = role.maxCount, Required = role.required };
                    if (role.precept != null) slot.Precept = Id(role.precept.defName);
                    row.Roles.Add(slot);
                }
            }
            return row;
        }

        // The player faction's primary ideoligion as it stands; null without
        // Ideology or a primary ideoligion.
        internal static Obs.IdeologySnapshot? Build(Common.ObservationContext context)
        {
            if (!ModsConfig.IdeologyActive) return null;
            var ideo = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo;
            if (ideo == null) return null;
            var row = new Obs.IdeologySnapshot { Context = context, IdeoId = Id(ideo.GetUniqueLoadID()), ObligationsActive = ideo.ObligationsActive,
                Believers = ideo.ColonistBelieverCountCached, MinBelieversForObligations = Ideo.MinBelieversToEnableObligations };
            row.Memes.Add(Sorted(Names(ideo.memes)));
            foreach (var precept in ideo.PreceptsListForReading.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)) {
                var id = Id(precept.GetUniqueLoadID());
                var def = Id(precept.def.defName);
                switch (precept) {
                    case Precept_Role role:
                        var held = new Obs.IdeoRole { Id = id, DefName = def, Active = role.Active };
                        held.Pawns.Add(role.ChosenPawns().OrderBy(p => p.thingIDNumber).Select(Ref));
                        row.Roles.Add(held);
                        break;
                    case Precept_Ritual ritual:
                        row.Rituals.Add(new Obs.IdeoRitual { Id = id, DefName = def, Pattern = ritual.sourcePattern == null ? null : Id(ritual.sourcePattern.defName),
                            LastFinishedTick = ritual.lastFinishedTick, ActiveObligations = ritual.activeObligations?.Count ?? 0, RepeatPenaltyActive = ritual.RepeatPenaltyActive });
                        break;
                    case Precept_Building building:
                        row.Buildings.Add(new Obs.IdeoBuilding { Id = id, DefName = def, Building = building.ThingDef == null ? null : Id(building.ThingDef.defName) });
                        break;
                    default:
                        row.Precepts.Add(new Obs.IdeoPrecept { Id = id, DefName = def });
                        break;
                }
            }
            return row;
        }
    }
}
