using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Native;

/// <summary>Read-only native inputs to the joiner trait workers (#3965). No hypothetical pawn, gear or timetable mutation.</summary>
internal static class NativeJoinerTraits
{
    internal static readonly (Type Worker, JoinerSituationKind Kind)[] Workers =
    [
        (typeof(ThoughtWorker_NudistNude), JoinerSituationKind.Nude),
        (typeof(ThoughtWorker_HumanLeatherApparel), JoinerSituationKind.HumanLeather),
        (typeof(ThoughtWorker_IsCarryingRangedWeapon), JoinerSituationKind.RangedWeapon),
        (typeof(ThoughtWorker_IsCarryingIncendiaryWeapon), JoinerSituationKind.IncendiaryWeapon),
        (typeof(ThoughtWorker_PyromaniacNearFlames), JoinerSituationKind.Flames),
        (typeof(ThoughtWorker_IsDayForNightOwl), JoinerSituationKind.Day),
        (typeof(ThoughtWorker_IsNightForNightOwl), JoinerSituationKind.Night),
        (typeof(ThoughtWorker_IsIndoorsForUndergrounder), JoinerSituationKind.Indoors),
        (typeof(ThoughtWorker_IsUndergroundForUndergrounder), JoinerSituationKind.Underground),
        (typeof(ThoughtWorker_IsOutdoorsForUndergrounder), JoinerSituationKind.Outdoors),
    ];

    internal static JoinerSituationThought Stages(Pawn pawn, ThoughtDef def, JoinerSituationKind kind)
    {
        var stages = new List<double>();
        for (var stage = 0; stage < def.stages.Count; stage++)
        {
            double offset;
            if (kind == JoinerSituationKind.Flames)
            {
                // Thought.MoodOffset, before the worker's location-dependent NumFiresNear multiplier.
                // Dividing MoodOffset would lose the per-fire value when the prison has no nearby fire.
                offset = ThoughtUtility.ThoughtNullified(pawn, def) ? 0 : def.stages[stage].baseMoodEffect;
                if (def.effectMultiplyingStat != null)
                    offset *= def.effectMultiplyingStatCurve == null ? pawn.GetStatValue(def.effectMultiplyingStat)
                        : def.effectMultiplyingStatCurve.Evaluate(pawn.GetStatValue(def.effectMultiplyingStat));
            }
            else offset = NativeNeedsForecast.StageOffset(pawn, def, stage);
            if (!double.IsFinite(offset)) throw new InvalidOperationException(def.defName + " trait stage is not finite.");
            stages.Add(offset);
        }
        return new(def.defName, kind, ThoughtUtility.CanGetThought(pawn, def), def.invert, def.stackLimit, EquatableArray.From(stages));
    }

    internal static int FiresAt(Map map, IntVec3 position)
    {
        var room = position.GetRoom(map);
        var fires = 0;
        foreach (var cell in GenRadial.RadialCellsAround(position, 8f, true))
            if (cell.InBounds(map) && !cell.Fogged(map) && cell.GetRoom(map) is { } other && other == room)
                fires += FireUtility.NumFiresAt(cell, map);
        return fires;
    }
}
