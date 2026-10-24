using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The Ideology defs (P.IdeologyDefs is a view over the game's
// own PreceptDef, RitualPatternDef and RitualBehaviorDef rows) and the ideoligion design options.

public sealed partial class DefinitionCatalog
{
    private const string CorePackagePrefix = "ludeon.rimworld";

    /// <summary>The Ideology defs of the load, null when the catalog has none (a game without Ideology). A def the rows name and the catalog lacks is an error, never a default.</summary>
    public P.IdeologyDefs? IdeologyDefs() => _ideology.Value;

    private P.IdeologyDefs? BuildIdeologyDefs()
    {
        var preceptRows = Rows<PreceptDef>();
        var patternRows = Rows<RitualPatternDef>();
        if (preceptRows.Count == 0 && patternRows.Count == 0)
        {
            return null;
        }

        var precepts = new Dictionary<string, P.PreceptDef>(StringComparer.Ordinal);
        var roles = new Dictionary<string, P.RoleDef>(StringComparer.Ordinal);
        foreach (var (name, row) in preceptRows)
        {
            // A PreceptDef whose preceptClass is or derives from Precept_Role is a role, not a plain precept.
            if (row.preceptClass is { } kind && typeof(Precept_Role).IsAssignableFrom(kind))
            {
                roles[name] = RoleDef(row);
            }
            else
            {
                precepts[name] = PreceptDef(row);
            }
        }

        var memes = new Dictionary<string, P.MemeDef>(StringComparer.Ordinal);
        foreach (var (name, row) in Rows<MemeDef>())
        {
            memes[name] = new P.MemeDef(
                [.. row.requireOne.Seq().Select(group => (IReadOnlyList<string>)[.. (group ?? throw new CatalogException($"meme {name} has a null required precept group")).Select(p => p.defName).Order(StringComparer.Ordinal)])],
                [.. preceptRows.Values.Where(p => p.conflictingMemes.Seq().Contains(row)).Select(p => p.defName).Order(StringComparer.Ordinal)]);
        }

        return new P.IdeologyDefs
        {
            Precepts = precepts,
            Roles = roles,
            Memes = memes,
            Rituals = patternRows.ToDictionary(pair => pair.Key, pair => RitualDef(pair.Value), StringComparer.Ordinal),
            BuildingMoods = BuildingDemandMoods(),
        };
    }

    // The stage-0 moods of the thoughts a building precept's presence demand gives (Precept_Building.SituationThoughtsToAdd:
    // ThoughtDefOf.IdeoBuildingMissing without the building, ThoughtDefOf.IdeoBuildingDisrespected when its room fails a
    // requirement); null when the catalog lacks either. The DefOf fields are null outside the game, so the rows are looked
    // up by their nameof.
    private P.BuildingDemandMoods? BuildingDemandMoods() =>
        DefRow<ThoughtDef>(nameof(ThoughtDefOf.IdeoBuildingMissing))?.stages.Seq().FirstOrDefault() is { } missing
        && DefRow<ThoughtDef>(nameof(ThoughtDefOf.IdeoBuildingDisrespected))?.stages.Seq().FirstOrDefault() is { } disrespected
            ? new(Decimal32(missing.baseMoodEffect), Decimal32(disrespected.baseMoodEffect))
            : null;

    // A ritual pattern's outcome effect, null for none; one whose worker does not draw the outcome from quality
    // (RitualOutcomeEffectWorker_FromQuality or a class deriving from it) is named by its worker class. Otherwise: startingQuality, the quality bounds, the RitualOutcomeComp_ParticipantCount and
    // RitualOutcomeComp_RoomStat (Impressiveness or Cleanliness) curves, and each outcome's chance, positivity and memory.
    // Other comps take inputs the plan cannot know before the ritual and are left out.
    private P.RitualOutcomeModel? RitualOutcome(RitualPatternDef row)
    {
        if (row.ritualOutcomeEffect is not { } named)
        {
            return null;
        }
        var effect = DefRow<RitualOutcomeEffectDef>(named.defName)
            ?? throw new CatalogException($"ritual {row.defName}: catalog has no outcome effect {named.defName}");
        if (effect.workerClass is not { } worker || !typeof(RitualOutcomeEffectWorker_FromQuality).IsAssignableFrom(worker))
        {
            return new P.RitualOutcomeModel { Unread = effect.workerClass?.Name ?? "null" };
        }
        static List<P.CurvePoint> Curve(SimpleCurve? curve) => [.. curve?.Points.Select(point => new P.CurvePoint(point.x, point.y)) ?? []];
        var comps = new List<P.RitualQualityComp>();
        foreach (var comp in effect.comps.Seq())
        {
            if (comp?.GetType() == typeof(RitualOutcomeComp_ParticipantCount))
            {
                comps.Add(new(P.RitualQualityInput.Participants, Curve(((RitualOutcomeComp_ParticipantCount)comp).curve)));
            }
            else if (comp is RitualOutcomeComp_RoomStat stat && stat.GetType() == typeof(RitualOutcomeComp_RoomStat))
            {
                var name = stat.statDef?.defName;
                if (name == nameof(RoomStatDefOf.Impressiveness))
                {
                    comps.Add(new(P.RitualQualityInput.Impressiveness, Curve(stat.curve)));
                }
                else if (name == nameof(RoomStatDefOf.Cleanliness))
                {
                    comps.Add(new(P.RitualQualityInput.Cleanliness, Curve(stat.curve)));
                }
            }
        }
        var chances = new List<P.RitualOutcomeChance>();
        foreach (var outcome in effect.outcomeChances.Seq())
        {
            _ = outcome ?? throw new CatalogException($"ritual outcome effect {effect.defName} has a null outcome");
            var memory = outcome.memory is { } thought ? RequireRow<ThoughtDef>(thought.defName) : null;
            chances.Add(new(
                Decimal32(outcome.chance),
                outcome.Positive,
                memory?.stages.Seq().FirstOrDefault() is { } stage ? Decimal32(stage.baseMoodEffect) : 0,
                memory is null ? 0 : Decimal32(memory.durationDays)));
        }
        return new P.RitualOutcomeModel
        {
            StartingQuality = Decimal32(effect.startingQuality),
            MinQuality = Decimal32(effect.minQuality),
            MaxQuality = Decimal32(effect.maxQuality),
            Comps = comps,
            Chances = chances,
        };
    }

    // A precept def with one effect per PreceptComp, in comp order.
    private P.PreceptDef PreceptDef(PreceptDef row) =>
        new([.. row.comps.Seq().Select(comp => PreceptEffect(row.defName, comp))])
        {
            Issue = row.issue?.defName ?? string.Empty,
            RequiredMemes = [.. row.requiredMemes.Seq().Select(m => m.defName).Order(StringComparer.Ordinal)],
            ConflictingMemes = [.. row.conflictingMemes.Seq().Select(m => m.defName).Order(StringComparer.Ordinal)],
        };

    // The base mood effect of each stage of the named thought; no thought is no stage. A null stage (the game keeps
    // unused stages null) is a stage of no mood.
    private List<double> ThoughtMoods(string name) =>
        name.Length == 0
            ? []
            : [.. RequireRow<ThoughtDef>(name).stages.Seq().Select(stage => stage is null ? 0 : Decimal32(stage.baseMoodEffect))];

    // Types one PreceptComp by its class: a thought comp carries its thought's stage moods, an event comp the history
    // event it reacts to, an unwilling comp the traits and hediffs that cancel it. The specific unwilling classes are
    // tested before the base they derive from.
    private P.PreceptEffect PreceptEffect(string precept, PreceptComp comp)
    {
        P.PreceptEffect Thought(P.PreceptEffect effect, ThoughtDef? thought) =>
            effect with { Thought = thought?.defName ?? string.Empty, StageMoods = ThoughtMoods(thought?.defName ?? string.Empty) };

        P.PreceptEffect Unwilling(HistoryEventDef? evt, IEnumerable<TraitRequirement>? traits, IEnumerable<HediffDef>? hediffs, double chance = P.PreceptEffect.Certain) =>
            new()
            {
                Kind = P.PreceptEffectKind.Unwilling,
                HistoryEvent = evt?.defName ?? string.Empty,
                NullifyingTraits = [.. traits.Seq().Select(t => t.def.defName).Order(StringComparer.Ordinal)],
                NullifyingHediffs = [.. hediffs.Seq().Select(h => h.defName).Order(StringComparer.Ordinal)],
                Chance = chance,
            };

        switch (comp)
        {
            case PreceptComp_SelfTookMemoryThought self:
                return Thought(new P.PreceptEffect { Kind = P.PreceptEffectKind.SelfTookAction, HistoryEvent = self.eventDef?.defName ?? string.Empty, OnlyForNonSlaves = self.onlyForNonSlaves }, self.thought);
            case PreceptComp_KnowsMemoryThought knows:
                return Thought(new P.PreceptEffect { Kind = P.PreceptEffectKind.WitnessedAction, HistoryEvent = knows.eventDef?.defName ?? string.Empty }, knows.thought);
            case PreceptComp_SituationalThought situational:
                return Thought(new P.PreceptEffect { Kind = P.PreceptEffectKind.Situational }, situational.thought);
            case PreceptComp_BedThought bed:
                return Thought(new P.PreceptEffect { Kind = P.PreceptEffectKind.Bed }, bed.thought);
            case PreceptComp_UnwillingToDo_Chance chance:
                return Unwilling(chance.eventDef, chance.nullifyingTraits, chance.nullifyingHediffs, Decimal32(chance.chance));
            case PreceptComp_UnwillingToDo unwilling:
                return Unwilling(unwilling.eventDef, unwilling.nullifyingTraits, unwilling.nullifyingHediffs);
            case PreceptComp_Apparel:
                return new P.PreceptEffect { Kind = P.PreceptEffectKind.Apparel };
            case PreceptComp_MentalBreak:
                return new P.PreceptEffect { Kind = P.PreceptEffectKind.MentalBreak };
            case PreceptComp_DevelopmentPoints points:
                return new P.PreceptEffect { Kind = P.PreceptEffectKind.DevelopmentPoints, HistoryEvent = points.eventDef?.defName ?? string.Empty };
            case PreceptComp_GoodwillSituation:
                return new P.PreceptEffect { Kind = P.PreceptEffectKind.GoodwillSituation };
            case null:
                throw new CatalogException($"precept {precept} has a null comp");
            default:
                throw new CatalogException($"precept {precept}: precept comp {comp.GetType().Name} has no effect kind");
        }
    }

    // A role precept def: how many pawns hold it, the skills each requirement asks for (a RoleRequirement_MinSkillAny
    // lists the skills of which one must meet its minimum; other requirements have none) and the work types its
    // roleDisabledWorkTags disable, as Precept_Role.DisabledWorkTypes reads them (a type this build does not name is left out).
    private P.RoleDef RoleDef(PreceptDef row) =>
        new(
            row.maxCount,
            [.. row.roleRequirements.Seq().Select(requirement =>
                requirement switch
                {
                    null => throw new CatalogException($"role {row.defName} has a null requirement"),
                    RoleRequirement_MinSkillAny any => new P.RoleRequirement([.. any.skills.Seq().Select(s => new P.SkillRequirement(s.skill.defName, s.minLevel))]),
                    _ => new P.RoleRequirement([]),
                })])
        {
            DisabledWork = row.roleDisabledWorkTags == WorkTags.None ? [] :
                [.. Rows<WorkTypeDef>().Values.Where(w => (row.roleDisabledWorkTags & w.workTags) != WorkTags.None)
                    .Select(w => Wire.TryParse<P.WorkType>(w.defName, out var type) ? type : P.WorkType.None)
                    .Where(t => t != P.WorkType.None).Distinct().Order()],
        };

    // A ritual pattern: its cadence, whether it may start freely, the buildings its obligation target filter accepts and
    // its behavior's role slots.
    private P.RitualDef RitualDef(RitualPatternDef row)
    {
        var buildings = new List<string>();
        if (row.ritualObligationTargetFilter is { } filter)
        {
            var target = DefRow<RitualObligationTargetFilterDef>(filter.defName)
                ?? throw new CatalogException($"ritual {row.defName}: catalog has no obligation target filter {filter.defName}");
            buildings = [.. target.thingDefs.Seq().Select(d => d.defName).Order(StringComparer.Ordinal)];
        }

        var slots = new List<P.RitualRoleSlot>();
        if (row.ritualBehavior is { } behavior)
        {
            var rows = DefRow<RitualBehaviorDef>(behavior.defName)
                ?? throw new CatalogException($"ritual {row.defName}: catalog has no behavior {behavior.defName}");
            foreach (var role in rows.roles.Seq())
            {
                _ = role ?? throw new CatalogException($"ritual {row.defName} behavior {behavior.defName}: null role");
                slots.Add(new P.RitualRoleSlot(
                    role.precept?.defName ?? string.Empty,
                    role.maxCount,
                    role.required));
            }
        }

        return new P.RitualDef
        {
            IntervalDaysMin = Decimal32(row.ritualFreeStartIntervalDaysRange.min),
            CanStartAnytime = row.canStartAnytime,
            AlwaysStartAnytime = row.alwaysStartAnytime,
            RequiredBuildings = buildings,
            Roles = slots,
            Outcome = RitualOutcome(row),
        };
    }

    // Whether the def comes from the base game or a first-party expansion.
    private static bool IsCoreContent(Def def) =>
        def.modContentPack?.PackageId.StartsWith(CorePackagePrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether a non-plain precept that needs <paramref name="meme"/> carries a cost the design cannot value: a ritual with
    /// an obligation trigger, or any precept class other than a role. A ritual the meme only unlocks and nothing obliges
    /// (Cannibal's anytime gladiator duel, #3511) asks nothing of the colony. A role (a specialist's, #3602) is filled by
    /// skill fit (<c>IdeologyRolesRules.RoleAssignments</c>); its abilities, stat offsets and work restrictions are not
    /// priced, so the design values the meme on its other precepts.
    /// </summary>
    internal static bool UnlocksUnvaluedPrecept(MemeDef meme, IEnumerable<PreceptDef> precepts) =>
        precepts.Any(p => p.preceptClass != typeof(Precept) && p.requiredMemes.Seq().Contains(meme)
            && !typeof(Precept_Role).IsAssignableFrom(p.preceptClass)
            && !(p.preceptClass == typeof(Precept_Ritual) && p.ritualPatternBase is { } pattern && !pattern.ritualObligationTriggers.Seq().Any()));

    /// <summary>
    /// The ideoligion selection constraints, projected from the same defs. It deliberately excludes unvalued
    /// stat/ability/apparel/mental-break effects.
    /// </summary>
    public P.DesignOptions IdeoligionOptions(string faction)
    {
        var f = DefRow<FactionDef>(faction) ?? throw new CatalogException("design faction absent from catalog");
        var memes = new List<P.DesignMeme>();
        var allPrecepts = Rows<PreceptDef>().Values.OrderBy(p => p.defName, StringComparer.Ordinal).ToList();
        foreach (var m in Rows<MemeDef>().Values.OrderBy(m => m.defName, StringComparer.Ordinal))
        {
            var allowed = !m.hiddenInChooseMemes
                && !f.disallowedMemes.Seq().Contains(m)
                && (!f.allowedMemes.Seq().Any() || f.allowedMemes.Contains(m))
                && (!m.factionWhitelist.Seq().Any() || m.factionWhitelist.Any(w => w.defName == faction));
            if (f.forcedMemes.Seq().Any())
            {
                allowed = f.forcedMemes.Contains(m);
            }

            if (f.requiredMemes.Seq().Contains(m))
            {
                allowed = true;
            }

            var structure = m.category == MemeCategory.Structure;
            if (structure && f.structureMemeWeights.Seq().Any(w => w.meme == m && w.selectionWeight != 0))
            {
                allowed = true;
            }

            var supported = IsCoreContent(m)
                && !m.requiredRituals.Seq().Any()
                && m.selectOneOrNone is null
                && m.preferredWeaponClasses is null
                && !m.apparelRequirements.Seq().Any()
                && m.veneratedAnimalsCountOffset == 0
                && m.veneratedAnimalsCountOverride <= 0;
            var requireOne = new List<IReadOnlyList<string>>();
            foreach (var group in m.requireOne.Seq())
            {
                requireOne.Add([.. (group ?? throw new CatalogException("null required precept group")).Select(p => p.defName)]);
            }

            foreach (var name in requireOne.SelectMany(group => group))
            {
                var precept = DefRow<PreceptDef>(name) ?? throw new CatalogException("missing required precept");
                supported &= precept.preceptClass == typeof(Precept);
            }

            supported &= !UnlocksUnvaluedPrecept(m, allPrecepts);
            memes.Add(new P.DesignMeme
            {
                Name = m.defName,
                Structure = structure,
                Allowed = allowed,
                InitialFluid = m.impact <= 2,
                Exclusions = [.. m.exclusionTags.Seq()],
                Supported = supported,
                RequireOne = requireOne,
                Obligations = m.requiredRituals.Seq().Count(),
            });
        }

        var designPrecepts = new List<P.DesignPrecept>();
        foreach (var row in allPrecepts.Where(p => p.preceptClass == typeof(Precept)))
        {
            var issue = row.issue ?? throw new CatalogException("missing design issue");
            var supported = !issue.allowMultiplePrecepts
                && row.takeNameFrom is null
                && row.alsoAdds is null
                && !row.statOffsets.Seq().Any()
                && !row.statFactors.Seq().Any()
                && !row.abilityStatFactors.Seq().Any()
                && !row.conditionalStatAffecters.Seq().Any()
                && IsCoreContent(row);
            var thoughts = new List<string>();
            var score = new P.DesignScore();
            double potentialBenefit = 0;
            try
            {
                var def = PreceptDef(row);
                score = score with { Restrictions = row.opposedWorkTypes.Seq().Count() };
                foreach (var effect in def.Effects)
                {
                    if (effect.Thought.Length > 0)
                    {
                        thoughts.Add(effect.Thought);
                    }

                    switch (effect.Kind)
                    {
                        case P.PreceptEffectKind.Unwilling:
                            score = score with { Restrictions = score.Restrictions + 1 };
                            break;
                        case P.PreceptEffectKind.SelfTookAction or P.PreceptEffectKind.WitnessedAction or P.PreceptEffectKind.Situational or P.PreceptEffectKind.Bed:
                            var cost = effect.StageMoods.Select(mood => -mood).Prepend(0).Max();
                            var benefit = effect.StageMoods.Prepend(0).Max();
                            score = score with { MoodCost = score.MoodCost + cost };

                            // A positive stage is a possible benefit, not evidence that the starting colony can earn it.
                            // Preserve it during reform without granting speculative credit to creation candidates.
                            potentialBenefit += benefit;
                            break;
                        case P.PreceptEffectKind.Apparel or P.PreceptEffectKind.MentalBreak or P.PreceptEffectKind.DevelopmentPoints or P.PreceptEffectKind.GoodwillSituation:
                            supported = false;
                            break;
                    }
                }
            }
            catch (CatalogException)
            {
                supported = false;
            }

            designPrecepts.Add(new P.DesignPrecept
            {
                Name = row.defName,
                Issue = issue.defName,
                Default = row.defaultSelectionWeight > 0,
                Allowed = !row.classicModeOnly && !f.disallowedPrecepts.Seq().Contains(row),
                Supported = supported,
                RequiredMemes = [.. row.requiredMemes.Seq().Select(m => m.defName)],
                ConflictingMemes = [.. row.conflictingMemes.Seq().Select(m => m.defName)],
                Exclusions = [.. row.exclusionTags.Seq()],
                Score = score,
                WorkTypes = [.. row.opposedWorkTypes.Seq().Select(w => w.defName)],
                Thoughts = thoughts,
                PotentialMoodBenefit = potentialBenefit,
            });
        }

        return new P.DesignOptions
        {
            Memes = memes,
            Precepts = designPrecepts,
            RequiredMemes = [.. f.requiredMemes.Seq().Select(m => m.defName)],
            // The game's own offer for the venerated-animal precept (animals, no insects or dryads); native rechecks CanUse.
            VeneratedAnimals = [.. PreceptDefOf.AnimalVenerated.Worker.ThingDefs.Select(t => t.def.defName).OrderBy(n => n, StringComparer.Ordinal)],
            MaxVeneratedAnimals = PreceptDefOf.AnimalVenerated.maxCount,
        };
    }
}
