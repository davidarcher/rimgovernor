using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One furniture entry of the planned deathrest chamber as the staging resolves it (#3810): the def placed and how many.</summary>
public sealed record DeathrestPiece(string Def, int Count);

/// <summary>
/// The DeathrestChamber credit (#3810, epic #3650 P1/P7): what a planned chamber's caskets and accelerators give each deathrester
/// (<see cref="NeedsForecastPawn.Deathrest"/>), from the game's deathrest mechanics.
/// <list type="bullet">
/// <item>Work time. A deathrest lasts Gene_Deathrest.MinDeathrestTicks (its base over the efficiency, the product of the bound
/// buildings' deathrestEffectivenessFactor) and gains GainPerDay × efficiency a day, so each one restores the same need whatever the
/// efficiency, which the awake days then spend at FallPerDay. The share of days spent deathresting is
/// <see cref="DeathrestShare"/>: FallPerDay / (GainPerDay × efficiency + FallPerDay). A deathresting pawn's consciousness is capped
/// at 0.1 (HediffDefOf.Deathrest), so each such day is a capability-day lost; the room gains the share it removes.</item>
/// <item>Mood. Between deathrests the DeathrestChamber thought holds the stage the last deathrest's room left
/// (Toils_LayDown.UpdateDeathrestThoughtIndex: the room's Impressiveness score stage + 1, none for an ascetic). The room replaces
/// the stage the pawn has now (its chamberThoughtIndex) by the planned room's, over the awake share, through
/// <see cref="ForecastHousing.GainPerDay"/>.</item>
/// </list>
/// The lowest-id planned chamber serves every deathrester; each takes one casket in pawn order and binds the chamber's other
/// buildings, the highest factor first, up to each def's stackLimit and, for those that count, its DeathrestCapacity. A deathrester
/// left without a casket deathrests where it does now and gains nothing. No deathrester earns nothing.
/// Not modelled: a pawn with autoWake off sleeps until woken (the cycle assumes the safe wake), and powered or fuelled buildings
/// are taken as running (CompDeathrestBindable.CanIncreasePresence).
/// </summary>
internal static class ForecastDeathrest
{
    /// <summary>The <see cref="NeedThoughtStage.Role"/> of the DeathrestChamber thought's stage rows (ThoughtWorker_DeathrestChamber).</summary>
    public const string ChamberThoughtRole = "deathrest_chamber";

    /// <summary>One deathrester's change in a chamber: the capability-days of work a day it recovers and its mean chamber mood shift.</summary>
    private sealed record Gain(string Pawn, double Work, double Mood);

    /// <summary>The chamber serving the deathresters (0 none, or no deathrester) and, per stuff it closes in, each one's gain.</summary>
    private sealed record Chamber(int Room, Dictionary<string, Gain[]> ByStuff);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Chamber> Cache = new();

    /// <summary>
    /// The chamber's gains per deathrester (<see cref="RoomCredit"/>): the work days it recovers plus the GainPerDay of its chamber
    /// mood shift. A required fact the read lacks (the deathrest defs, a stage row, the room's closure prediction in the stuff it
    /// serves in, the mood forecast) raises <see cref="RequiredFactException"/>.
    /// </summary>
    internal static IReadOnlyList<RoomGain> ChamberCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        var chamber = Cache.GetValue(context, Of);
        if (chamber.Room != room.Room) return [];
        // The chamber's prediction in its served stuff, else its first by stuff (#3835: a ringless chamber is predicted in "").
        if (PlannedRoomCredits.Prediction(context, room.Room, room.Stuff) is not { Stuff: var stuff }) return [];
        var gains = chamber.ByStuff.GetValueOrDefault(stuff)
            ?? throw new RequiredFactException(FormattableString.Invariant($"closure prediction of deathrest chamber {room.Room} in {stuff}"));
        var output = new List<RoomGain>(gains.Length);
        foreach (var gain in gains)
        {
            var mood = gain.Mood != 0 ? PlannedRoomCredits.MoodGain(gain.Mood, gain.Pawn, outcome, context, horizon) : 0;
            if (gain.Work + mood != 0) output.Add(new(gain.Pawn, gain.Pawn, gain.Work + mood));
        }
        return output;
    }

    /// <summary>Per deathrester, the distinct nonzero chamber mood shifts (mood / 100) its timeline integrates breaks at.</summary>
    internal static IReadOnlyDictionary<string, double[]> Shifts(ForecastCoupledContext context) =>
        Cache.GetValue(context, Of).ByStuff.Values.SelectMany(g => g).Where(g => g.Mood != 0).GroupBy(g => g.Pawn, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Mood / 100).Distinct().Order().ToArray(), StringComparer.Ordinal);

    /// <summary>The share of days a deathrester at <paramref name="efficiency"/> spends deathresting (see <see cref="ForecastDeathrest"/>).</summary>
    internal static double DeathrestShare(DeathrestDefsRead defs, double efficiency) => defs.FallPerDay / (defs.GainPerDay * efficiency + defs.FallPerDay);

    /// <summary>The DeathrestChamber stage an indoor room at <paramref name="impressiveness"/> leaves (RoomStatDef.GetScoreStageIndex + 1).</summary>
    internal static int ChamberStage(DeathrestDefsRead defs, double impressiveness)
    {
        if (defs.ImpressivenessStages.Count == 0) throw new RequiredFactException("impressiveness score stages");
        var stage = 0;
        for (var i = 0; i < defs.ImpressivenessStages.Count && impressiveness >= defs.ImpressivenessStages[i]; i++) stage = i;
        return stage + 1;
    }

    private static Chamber Of(ForecastCoupledContext c)
    {
        var deathresters = c.Needs.Pawns.Where(p => p.Deathrest is not null).DistinctBy(p => p.Pawn).OrderBy(p => p.Pawn, StringComparer.Ordinal).ToArray();
        var chamber = c.PlannedRoomRoles.Where(r => r.Value == PlannedRole.DeathrestChamber).Select(r => r.Key).DefaultIfEmpty(0).Min();
        if (deathresters.Length == 0 || chamber == 0 || !c.RoomCredits.ContainsKey(PlannedRole.DeathrestChamber)) return new(0, []);
        var defs = c.Needs.Deathrest ?? throw new RequiredFactException("deathrest defs");
        var efficiencies = Efficiencies(deathresters, c.DeathrestFurniture, defs);
        var byStuff = new Dictionary<string, Gain[]>(StringComparer.Ordinal);
        foreach (var closure in c.Closures.Where(k => k.Room == chamber))
            byStuff[closure.Stuff] = [.. deathresters.Select(p => GainOf(p, efficiencies.GetValueOrDefault(p.Pawn), closure.Prediction, defs))];
        return new(chamber, byStuff);
    }

    /// <summary>
    /// The efficiency the chamber gives each deathrester it holds a casket for (see <see cref="ForecastDeathrest"/>); a deathrester
    /// without a row gets no casket.
    /// </summary>
    private static Dictionary<string, double> Efficiencies(IEnumerable<NeedsForecastPawn> deathresters, EquatableArray<DeathrestPiece> furniture, DeathrestDefsRead defs)
    {
        var bindables = defs.Bindables.ToDictionary(b => b.Def, StringComparer.Ordinal);
        DeathrestBindableRead Bindable(string def) => bindables.TryGetValue(def, out var b) ? b : throw new RequiredFactException("deathrest bindable " + def);
        var caskets = new Queue<double>(furniture.Where(p => Bindable(p.Def).Casket).SelectMany(p => Enumerable.Repeat(Bindable(p.Def).Factor, p.Count)));
        var left = furniture.Where(p => !Bindable(p.Def).Casket).GroupBy(p => p.Def, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Count), StringComparer.Ordinal);
        var output = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pawn in deathresters)
        {
            if (!caskets.TryDequeue(out var casket)) break;
            var efficiency = casket > 0 ? casket : 1;
            foreach (var (bindable, bound) in Binds(left.Select(l => (Bindable(l.Key), l.Value)), pawn.Deathrest!.Capacity))
            {
                if (bindable.Factor > 0) efficiency *= Math.Pow(bindable.Factor, bound);
                left[bindable.Def] -= bound;
            }
            output[pawn.Pawn] = efficiency;
        }
        return output;
    }

    /// <summary>
    /// What one deathrester binds beside its casket of the <paramref name="offered"/> buildings (Gene_Deathrest's binding; the
    /// credit's furniture and the staging's chamber, #3842): the highest factor first, then by def, each up to its stackLimit
    /// (0 none) and, for one that counts toward the limit, the <paramref name="capacity"/> left. Caskets are not offered here.
    /// </summary>
    internal static List<(DeathrestBindableRead Bindable, int Count)> Binds(IEnumerable<(DeathrestBindableRead Bindable, int Count)> offered, int capacity)
    {
        var output = new List<(DeathrestBindableRead, int)>();
        foreach (var (bindable, count) in offered.Where(o => !o.Bindable.Casket).OrderByDescending(o => o.Bindable.Factor).ThenBy(o => o.Bindable.Def, StringComparer.Ordinal))
        {
            var bound = bindable.StackLimit > 0 ? Math.Min(count, bindable.StackLimit) : count;
            if (bindable.CountsTowardsLimit)
            {
                bound = Math.Min(bound, Math.Max(0, capacity));
                capacity -= bound;
            }
            if (bound > 0) output.Add((bindable, bound));
        }
        return output;
    }

    /// <summary>One deathrester's work and mood change in the chamber <paramref name="prediction"/> predicts at <paramref name="efficiency"/> (0: no casket for it).</summary>
    private static Gain GainOf(NeedsForecastPawn pawn, double efficiency, PlannedRoomPrediction prediction, DeathrestDefsRead defs)
    {
        if (efficiency <= 0) return new(pawn.Pawn, 0, 0);
        var now = pawn.Deathrest!;
        var before = DeathrestShare(defs, now.Efficiency);
        var after = DeathrestShare(defs, efficiency);
        var mood = Offset(pawn, pawn.Ascetic ? -1 : ChamberStage(defs, prediction.Impressiveness)) * (1 - after) - Offset(pawn, now.ChamberStage) * (1 - before);
        return new(pawn.Pawn, before - after, mood);
    }

    /// <summary>The pawn's DeathrestChamber offset at <paramref name="stage"/> (its stage row); none below 0.</summary>
    private static double Offset(NeedsForecastPawn pawn, int stage) => stage < 0 ? 0
        : pawn.ThoughtStages.FirstOrDefault(r => r.Role == ChamberThoughtRole && r.Stage == stage)?.Offset
            ?? throw new RequiredFactException(FormattableString.Invariant($"deathrest chamber thought stage {stage} of {pawn.Pawn}"));
}
