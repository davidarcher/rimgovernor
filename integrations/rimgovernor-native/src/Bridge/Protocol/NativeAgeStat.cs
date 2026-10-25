using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

/// <summary>Read-only integer-age substitution before native stat finalization. No pawn, definition or cache is rewritten.
/// The inspected 1.7 StatWorker pipeline applies parts, postprocess curve/factors, scenario, rounding, then clamps.</summary>
internal static class NativeAgeStat
{
    // StatPart_Age exposes no typed route to its private curve or flags; these fields were checked against the installed 1.7 assembly.
    private static T Field<T>(StatPart_Age part, string name) =>
        BridgeCommon.PrivateInstanceField(typeof(StatPart_Age), name)?.GetValue(part) is T value ? value
            : throw new InvalidOperationException("StatPart_Age." + name + " is not readable");

    private static bool Active(Pawn pawn, StatPart_Age part) => !Field<bool>(part, "humanlikeOnly") || pawn.RaceProps.Humanlike;

    internal static int LastAge(Pawn pawn, StatDef stat) => Math.Max(
        stat.parts?.OfType<StatPart_Age>().Where(p => Active(pawn, p)).Select(p =>
            (int)Math.Ceiling(Field<SimpleCurve>(p, "curve").Points.Max(c => c.x)
                * (Field<bool>(p, "useBiologicalYears") ? 1 : pawn.RaceProps.lifeExpectancy))).DefaultIfEmpty(0).Max() ?? 0,
        (stat.statFactors ?? []).Concat(stat.postProcessStatFactors ?? []).Select(s => LastAge(pawn, s)).DefaultIfEmpty(0).Max());

    internal static float Value(Pawn pawn, StatDef stat, int age)
    {
        if (stat.Worker.IsDisabledFor(pawn)) return 0;
        var value = Unfinalized(pawn, stat, age);
        Finalize(pawn, stat, age, ref value);
        return value;
    }

    internal static float Unfinalized(Pawn pawn, StatDef stat, int age)
    {
        // Only inspected workers inheriting the base value pipeline: arbitrary overrides can read age anywhere.
        if (!NativeSkillStat.HasBaseValuePipeline(stat))
            throw new InvalidOperationException("Age counterfactual needs base StatWorker: " + stat.defName);
        if (stat.parts?.Any(p => !NativeSkillStat.AllowedPart(pawn, p)) == true)
            throw new InvalidOperationException("Age counterfactual has unsupported stat part: " + stat.defName);
        if (pawn.Inspired && pawn.InspirationDef.statOffsets.GetStatOffsetFromList(stat) != 0
            || pawn.AllComps.Any(c => c.GetStatOffset(stat) != 0))
            throw new InvalidOperationException("Age counterfactual has offset after age-dependent factors: " + stat.defName);
        var value = stat.Worker.GetValueUnfinalized(StatRequest.For(pawn));
        var stage = pawn.RaceProps.lifeStageAges.Last(s => s.minAge <= age).def;
        var observed = pawn.ageTracker.CurLifeStage;
        if (stage.statOffsets.GetStatOffsetFromList(stat) != observed.statOffsets.GetStatOffsetFromList(stat))
            throw new InvalidOperationException("Age counterfactual has changing life-stage offset: " + stat.defName);
        value = Replace(value, observed.statFactors.GetStatFactorFromList(stat), stage.statFactors.GetStatFactorFromList(stat), stat);
        foreach (var factor in stat.statFactors ?? []) value = Replace(value, pawn.GetStatValue(factor), Value(pawn, factor, age), stat);
        return value;
    }

    private static float Replace(float value, float before, float after, StatDef stat)
    {
        if (before == after) return value;
        if (before == 0) throw new InvalidOperationException("Age counterfactual has zero observed factor: " + stat.defName);
        return (float)(value / (double)before * after);
    }

    internal static void Finalize(Pawn pawn, StatDef stat, int age, ref float value)
    {
        var request = StatRequest.For(pawn);
        foreach (var part in stat.parts ?? [])
        {
            if (part is StatPart_Age aged)
            {
                if (Active(pawn, aged)) value *= Field<SimpleCurve>(aged, "curve").Evaluate(
                    Field<bool>(aged, "useBiologicalYears") ? age : age / pawn.RaceProps.lifeExpectancy);
            }
            else part.TransformValue(request, ref value);
        }
        if (stat.postProcessCurve != null) value = stat.postProcessCurve.Evaluate(value);
        foreach (var factor in stat.postProcessStatFactors ?? []) value *= Value(pawn, factor, age);
        if (Find.Scenario != null) value *= Find.Scenario.GetStatFactor(stat);
        if (Math.Abs(value) > stat.roundToFiveOver) value = (float)Math.Round(value / 5f) * 5f;
        if (stat.roundValue) value = (float)Math.Round(value);
        value = Math.Clamp(value, stat.minValue, stat.maxValue);
    }
}
