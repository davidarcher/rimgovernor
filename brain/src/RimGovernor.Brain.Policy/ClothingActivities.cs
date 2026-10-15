using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Brain.Policy.Methods.Binding;

namespace RimGovernor.Brain.Policy;

public sealed record ClothingActivityRequirement(string Wearer, string Product, string Stuff,
    ForecastRequirementAssessment Assessment, EquatableArray<string> Layers, EquatableArray<string> Groups);
public sealed record ClothingActivityDemand(EquatableArray<ClothingActivityRequirement> Alternatives, EquatableArray<string> Findings);

public static partial class PolicyFunctions
{
    /// <summary>The loadout model owns needs; production retains every eligible shape/material until comparison.</summary>
    public static ClothingActivityDemand ClothingActivityRequirements(GearObservation census)
    {
        if (!census.Stored.IsKnown) return new([], ["unknown_clothing_stock"]);
        var result = new List<ClothingActivityRequirement>();
        var findings = new List<string>();
        foreach (var pawn in census.Pawns.OrderBy(p => p.Pawn.Value, StringComparer.Ordinal))
        {
            if (pawn.Blocked) continue;
            if (!pawn.LoadoutModel.TryGetValue(out var input) || !pawn.Candidates.TryGetValue(out var physical))
            { findings.Add("unknown_clothing_need:" + pawn.Pawn.Value); continue; }
            if (physical.Count != 0) { findings.Add("physical_clothing_available:" + pawn.Pawn.Value); continue; }
            input = input with { Climate = pawn.Climate ?? input.Climate };
            foreach (var trait in input.Role.Work.Traits.ValueOrDefault)
                input = input with { Nudist = input.Nudist || trait.Effects.Nudist, TaintFree = input.TaintFree || trait.Effects.TaintFree };
            foreach (var option in input.Options.Where(o => o.Source == GearSource.Bill && !ArmoryArmor(o.Definition)
                && GearEligible(input, o)).OrderBy(o => o.Definition.Value, StringComparer.Ordinal).ThenBy(o => o.Stuff.Value, StringComparer.Ordinal))
            {
                var effects = ClothingRelief(pawn, input, option);
                if (effects is not { Count: > 0 }) continue;
                result.Add(new(pawn.Pawn.Value, option.Definition.Value, option.Stuff.Value,
                    new(EquatableArray.From(effects), Known(0.0), Known(0.0)), option.Layers, option.Groups));
            }
        }
        return new(EquatableArray.From(result.Distinct()), EquatableArray.From(findings));
    }

    public static PawnActivities? StartClothingActivity(RecipeActivityRead read, ClothingActivityDemand requirements,
        ulong authority, string method, long deadline, WorkResourceView resources,
        out ForecastActivityOutcomeComparison? comparison)
    {
        var candidates = ClothingMethodCandidates.Generate(read, requirements.Alternatives, deadline, resources);
        comparison = null;
        if (candidates.Problem is null || read.Context?.Tick is not { } tick) return null;
        comparison = CompareActivityOutcomes(candidates.Problem, candidates.Outcomes,
            ForecastOutcomeObjective.RequirementRelief, (selected, plan) => Methods.Evaluation.ClothingActivityEvaluation.MaterialsFit(selected, candidates.Bindings, resources),
            candidates.Truncated, generationFindings: requirements.Findings.Concat(read.Findings).Concat(candidates.Findings));
        return CoordinatedActivityBinding.Start(comparison, candidates.Bindings, authority, method, tick);
    }
}
