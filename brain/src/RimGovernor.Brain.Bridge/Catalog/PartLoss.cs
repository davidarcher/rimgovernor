using System.Reflection;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The part-loss tables of the harm model's raid forecast (#3847 of #3650), read by P.RaidPartLoss:
//   - each DamageDef's rule: which class declares its worker's ChooseHitPart and ApplySpecialEffectsToPart (DamageWorker_AddInjury,
//     Stab, Cut, Blunt, Scratch or Bite; a worker that is no DamageWorker_AddInjury adds no injury, one with another override is
//     foreign), overkillPctToDestroyPart, stabChanceOfForcedInternal, the cut and blunt damage splits, and every BodyPartDef's
//     GetHitChanceFactorFor it where that is not 1;
//   - per BodyPartDef, the tier of each install recipe that puts a part of it back (as the illness's organs read theirs).

public sealed partial class DefinitionCatalog
{
    // The midpoint grid DamageWorker_Cut's Rand.Value draw of cutExtraTargetsCurve and DamageWorker_Blunt's
    // bluntInnerHitDamageFractionToConvert range are expected over.
    private const int SplitGrid = 1000;
    private const int RangeGrid = 4;

    private Lazy<IReadOnlyDictionary<string, P.PartHitRule>>? _partHitRules;
    private Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>? _partRecipes;

    /// <summary>The rule of every damage def a raid's hit can deal (#3847), by def name.</summary>
    public IReadOnlyDictionary<string, P.PartHitRule> PartHitRules()
    {
        _partHitRules ??= new(() => Rows<DamageDef>().Values.ToDictionary(d => d.defName, PartHitRuleOf, StringComparer.Ordinal));
        return _partHitRules.Value;
    }

    /// <summary>Per body part def with an install recipe, each such recipe's tier (#3847), by def name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> PartReplacementRecipes()
    {
        _partRecipes ??= new(() => Rows<BodyPartDef>().Values
            .Select(part => (part.defName, Tiers: ReplacementTiers(part)))
            .Where(p => p.Tiers.Count > 0)
            .ToDictionary(p => p.defName, p => (IReadOnlyDictionary<string, double>)p.Tiers, StringComparer.Ordinal));
        return _partRecipes.Value;
    }

    private P.PartHitRule PartHitRuleOf(DamageDef damage)
    {
        var worker = damage.workerClass;
        if (worker is null || !typeof(DamageWorker_AddInjury).IsAssignableFrom(worker))
        {
            return new P.PartHitRule { Draw = P.PartHitDraw.NoInjury };
        }

        var choose = Declaring(worker, "ChooseHitPart");
        var apply = Declaring(worker, "ApplySpecialEffectsToPart");
        var draw = choose == typeof(DamageWorker_AddInjury) || choose == typeof(DamageWorker_Stab) ? P.PartHitDraw.HitDepth
            : choose == typeof(DamageWorker_Cut) || choose == typeof(DamageWorker_Blunt) || choose == typeof(DamageWorker_Scratch) || choose == typeof(DamageWorker_Bite)
                ? P.PartHitDraw.Outside : P.PartHitDraw.ForeignWorker;
        P.PartHitCase[]? cases = apply == typeof(DamageWorker_AddInjury) || apply == typeof(DamageWorker_Stab) ? [new(1, 1, true)]
            : apply == typeof(DamageWorker_Scratch) ? [new(1, Decimal32(damage.scratchSplitPercentage), true)]
            : apply == typeof(DamageWorker_Cut) ? CutCases(damage)
            : apply == typeof(DamageWorker_Blunt) ? BluntCases(damage)
            : null;
        if (cases is null || draw == P.PartHitDraw.ForeignWorker)
        {
            return new P.PartHitRule { Draw = P.PartHitDraw.ForeignWorker };
        }

        return new P.PartHitRule
        {
            Draw = draw,
            Cases = Domain.EquatableArray.From(cases),
            OverkillMin = Decimal32(damage.overkillPctToDestroyPart.min),
            OverkillMax = Decimal32(damage.overkillPctToDestroyPart.max),
            ForcedInternal = choose == typeof(DamageWorker_Stab) ? Decimal32(damage.stabChanceOfForcedInternal) : 0,
            StabSplit = apply == typeof(DamageWorker_Stab),
            HitFactors = Domain.EquatableArray.From(Rows<BodyPartDef>().Values.OrderBy(p => p.defName, StringComparer.Ordinal)
                .Select(p => new P.PartHitFactor(p.defName, Decimal32(p.GetHitChanceFactorFor(damage)))).Where(f => f.Factor != 1)),
        };
    }

    // The class that declares the worker's override of a DamageWorker_AddInjury method (the methods are protected; the name is the game's).
    private static Type Declaring(Type worker, string method) =>
        worker.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.DeclaringType
            ?? throw new CatalogException($"damage worker {worker.Name} has no {method}");

    // DamageWorker_Cut.ApplySpecialEffectsToPart on an Outside part: GenMath.RoundRandom(cutExtraTargetsCurve(Rand.Value)) extra targets;
    // none keeps the whole damage and ReduceDamageToPreserveOutsideParts, k of them give each part total × (1 + cutCleaveBonus) ÷
    // (k + 1 + cutCleaveBonus) unpreserved. The extra targets' own losses are not read (raid_part_loss_spread_unmodelled).
    private static P.PartHitCase[] CutCases(DamageDef damage)
    {
        if (damage.cutExtraTargetsCurve is not { } curve)
        {
            return [new(1, 1, true)];
        }

        var chances = new SortedDictionary<int, double>();
        for (var i = 0; i < SplitGrid; i++)
        {
            var targets = curve.Evaluate((i + 0.5f) / SplitGrid);
            var whole = (int)Math.Floor(targets);
            var fraction = targets - whole;
            chances[whole] = chances.GetValueOrDefault(whole) + (1 - fraction) / SplitGrid;
            if (fraction > 0)
            {
                chances[whole + 1] = chances.GetValueOrDefault(whole + 1) + fraction / SplitGrid;
            }
        }

        double cleave = Decimal32(damage.cutCleaveBonus);
        return [.. chances.Where(c => c.Value > 0).Select(c => c.Key <= 0 ? new P.PartHitCase(c.Value, 1, true) : new P.PartHitCase(c.Value, (1 + cleave) / (c.Key + 1 + cleave), false))];
    }

    // DamageWorker_Blunt.ApplySpecialEffectsToPart: no ReduceDamageToPreserveOutsideParts; with bluntInnerHitChance a fraction drawn from
    // bluntInnerHitDamageFractionToConvert moves to an inner part, the rest stays. The inner part's and the parent's cascade losses are
    // not read (raid_part_loss_spread_unmodelled).
    private static P.PartHitCase[] BluntCases(DamageDef damage)
    {
        double inner = Math.Clamp(Decimal32(damage.bluntInnerHitChance), 0, 1);
        var cases = new List<P.PartHitCase> { new(1 - inner, 1, false) };
        var range = damage.bluntInnerHitDamageFractionToConvert;
        for (var i = 0; i < RangeGrid && inner > 0; i++)
        {
            double converted = Decimal32(range.min + ((range.max - range.min) * (i + 0.5f) / RangeGrid));
            cases.Add(new(inner / RangeGrid, 1 - converted, false));
        }

        return [.. cases.Where(c => c.Chance > 0)];
    }
}
