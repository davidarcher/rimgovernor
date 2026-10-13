using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_ideology.go and catalog_ideoligion_design.go: the Ideology defs (P.IdeologyDefs is a view over the game's
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

        return new P.IdeologyDefs
        {
            Precepts = precepts,
            Roles = roles,
            Rituals = patternRows.ToDictionary(pair => pair.Key, pair => RitualDef(pair.Value), StringComparer.Ordinal),
        };
    }

    // A precept def with one effect per PreceptComp, in comp order.
    private P.PreceptDef PreceptDef(PreceptDef row) =>
        new(row.defName, [.. row.comps.Seq().Select(comp => PreceptEffect(row.defName, comp))]);

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

        P.PreceptEffect Unwilling(HistoryEventDef? evt, IEnumerable<TraitRequirement>? traits, IEnumerable<HediffDef>? hediffs, Fact<double> chance = default) =>
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
                return Unwilling(chance.eventDef, chance.nullifyingTraits, chance.nullifyingHediffs, Known(Decimal32(chance.chance)));
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

    // A role precept def: how many pawns hold it and the skills each requirement asks for (a RoleRequirement_MinSkillAny
    // lists the skills of which one must meet its minimum; other requirements have none).
    private static P.RoleDef RoleDef(PreceptDef row) =>
        new(
            row.defName,
            row.maxCount,
            [.. row.roleRequirements.Seq().Select(requirement =>
                requirement switch
                {
                    null => throw new CatalogException($"role {row.defName} has a null requirement"),
                    RoleRequirement_MinSkillAny any => new P.RoleRequirement([.. any.skills.Seq().Select(s => new P.SkillRequirement(s.skill.defName, s.minLevel))]),
                    _ => new P.RoleRequirement([]),
                })]);

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
                slots.Add(new P.RitualRoleSlot(
                    role?.id ?? throw new CatalogException($"ritual {row.defName} behavior {behavior.defName}: null role"),
                    role.precept?.defName ?? string.Empty,
                    role.maxCount,
                    role.required));
            }
        }

        return new P.RitualDef
        {
            Name = row.defName,
            IntervalDaysMin = Decimal32(row.ritualFreeStartIntervalDaysRange.min),
            CanStartAnytime = row.canStartAnytime,
            AlwaysStartAnytime = row.alwaysStartAnytime,
            RequiredBuildings = buildings,
            Roles = slots,
        };
    }

    // Whether the def comes from the base game or a first-party expansion.
    private static bool IsCoreContent(Def def) =>
        def.modContentPack?.PackageId.StartsWith(CorePackagePrefix, StringComparison.OrdinalIgnoreCase) == true;

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

            // A meme may unlock specialized roles without naming them in requireOne. Their starting skill/ability
            // feasibility is not a plain-precept cost.
            supported &= !allPrecepts.Any(p => p.preceptClass != typeof(Precept) && p.requiredMemes.Seq().Contains(m));
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
                        default:
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
        };
    }
}
