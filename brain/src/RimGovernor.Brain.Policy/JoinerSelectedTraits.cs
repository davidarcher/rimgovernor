using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A verified selected service exposure, derived from its native cell and the existing outfit timeline.</summary>
public sealed record JoinerTraitService(bool Awake, double LocalHour, PawnTraitSituation TraitSituation);
public sealed record PawnTraitSituation(bool Clothed, int HumanLeather, bool RangedWeapon, bool IncendiaryWeapon,
    bool Indoor, bool NaturalRoof, int Fires);

public static partial class JoinerMoodRules
{
    /// <summary>Joiner trait exposure served as each colonist's selected schedule and outfit, then the existing median.
    /// Unlocated transit/idle contributes no asserted location effect and is explicitly conditional in findings.</summary>
    public static double SelectedSituationTraits(IReadOnlyList<JoinerSituationThought> rows, ForecastCoupledOutcome outcome,
        IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon, ISet<string> findings)
    {
        if (rows.Count == 0) return 0;
        if (!(horizon > 0)) throw new RequiredFactException("joiner trait horizon");
        var pawns = context.Needs.Pawns.DistinctBy(p => p.Pawn, StringComparer.Ordinal).ToArray();
        if (pawns.Length == 0) throw new RequiredFactException("colony pawns");
        var terminal = outcome.Terminal ?? throw new RequiredFactException("selected trait terminal");
        var rooms = ForecastRoofConsequences.PlannedClosedRooms(context, terminal.Finished);
        var tasks = context.Activities.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var sums = new List<double>();
        foreach (var pawn in pawns)
        {
            if (!context.Outfits.TryGetValue(pawn.Pawn, out var worn)) throw new RequiredFactException("joiner outfit:" + pawn.Pawn);
            var outfits = HarmLedger.OutfitsOf(pawn.Pawn, worn, commitments, outcome.OutfitTicks, horizon)
                ?? throw new RequiredFactException("joiner selected outfit tick:" + pawn.Pawn);
            var services = outcome.Needs.TryGetValue(pawn.Pawn, out var needs) ? needs.Services
                : throw new RequiredFactException("joiner selected services:" + pawn.Pawn);
            var wakes = ForecastNeeds.SleepServiceEnds(pawn, services, null, new SortedSet<string>(StringComparer.Ordinal));
            var spans = new List<(double Start, double End, bool Awake, NeedCell? Cell, Cell Location)>();
            foreach (var choice in services)
            {
                var target = pawn.Targets.FirstOrDefault(t => t.Id == choice.Target);
                var retainedSleep = choice.Kind == "rest" && choice.Start == 0 && !pawn.Awake && pawn.LyingIn?.Place.Bed == choice.Target;
                var cell = retainedSleep ? pawn.LyingIn!.Cell : target?.Cell;
                var location = retainedSleep ? pawn.Location : target?.Location ?? default;
                if (choice.Kind == "rest")
                {
                    if (wakes is null || !wakes.TryGetValue(choice.Id, out var wake)) { findings.Add("joiner_trait_sleep_unprojected"); continue; }
                    spans.Add((choice.Start, Math.Min(wake, choice.End), false, cell, location));
                    if (choice.End > wake) spans.Add((wake, choice.End, true, cell, location));
                }
                else if (choice.Kind == "joy") spans.Add((choice.Start, choice.End, true, cell, location));
                // Eating's chosen item is not a verified chew location.
                else spans.Add((choice.Start, choice.End, true, null, default));
            }
            foreach (var a in outcome.Plan.Allocations.Where(a => a.Pawn == pawn.Pawn && !context.NeedTasks.Any(n => n.Task == a.Activity)))
            {
                if (!terminal.Finished.ContainsKey(a.Activity) || !tasks.TryGetValue(a.Activity, out var task)) continue;
                var cell = task.Location == task.EndLocation
                    ? context.Thermal?.Cells.FirstOrDefault(c => Methods.Generation.ForecastLocations.Of(c.Cell) == task.Location) : null;
                spans.Add((a.Arrive, a.End, true, cell?.MoodCell, cell?.Cell ?? default));
            }
            var cuts = new SortedSet<double> { 0, horizon };
            void Cut(double time) { if (time > 0 && time < horizon) cuts.Add(time); }
            foreach (var span in spans) { Cut(span.Start); Cut(span.End); }
            foreach (var room in rooms) { Cut(room.ClosedAt); Cut(room.RoofedAt); }
            double at = 0;
            foreach (var outfit in outfits) { at += outfit.Share * horizon; Cut(at); }
            for (var hour = Math.Floor(pawn.LocalHour) + 1; (hour - pawn.LocalHour) * GameTime.TicksPerHour < horizon; hour++)
                Cut((hour - pawn.LocalHour) * GameTime.TicksPerHour);
            var points = cuts.ToArray();
            double sum = 0;
            for (var i = 0; i + 1 < points.Length; i++)
            {
                var start = points[i]; var end = points[i + 1]; var mid = (start + end) / 2;
                var span = spans.FirstOrDefault(s => s.Start <= mid && s.End > mid);
                var located = span.Cell is not null;
                var awakeKnown = span.End > span.Start;
                double outfitEnd = 0;
                var outfit = outfits.First(o => { outfitEnd += o.Share * horizon; return outfitEnd > mid; }).Outfit;
                var primary = outfit.FirstOrDefault(o => o.Slot == GearSlot.Primary);
                var apparel = outfit.Where(o => o.Slot != GearSlot.Primary).ToArray();
                var state = located ? ForecastRoofConsequences.CellStateAt(span.Cell!, span.Location, mid, rooms) : default;
                var closureChanged = rooms.Any(r => r.Interior.Contains(span.Location) && (r.ClosedAt <= mid || r.RoofedAt <= mid));
                var fireKnown = located && span.Cell!.NearbyFires is NeedNearbyFires.Counted && !closureChanged;
                // Room closure changes the fire worker's same-room set; the observed radius count cannot prove its new count.
                var projected = new JoinerTraitService(span.Awake, (pawn.LocalHour + mid / GameTime.TicksPerHour) % 24,
                    new(apparel.Any(o => o.CountsAsClothingForNudity && (o.Groups.Contains("Torso") || o.Groups.Contains("Legs"))),
                        apparel.Count(o => o.Stuff == PreferenceRules.HumanLeather), primary?.Weapon.TraitRanged == true,
                        primary?.Weapon.TraitIncendiary == true, state.Roofed && !state.OutdoorTemperature, state.NaturalRoof,
                        fireKnown ? ((NeedNearbyFires.Counted)span.Cell!.NearbyFires).Count : 0));
                foreach (var row in rows)
                {
                    if (row.Kind == JoinerSituationKind.Flames && located && !closureChanged && span.Cell!.NearbyFires is NeedNearbyFires.Unread)
                        throw new RequiredFactException("nearby fires:" + pawn.Pawn);
                    var spatial = row.Kind is JoinerSituationKind.Flames or JoinerSituationKind.Indoors or JoinerSituationKind.Underground or JoinerSituationKind.Outdoors;
                    var awakeDependent = row.Kind is JoinerSituationKind.Day or JoinerSituationKind.Night or JoinerSituationKind.Indoors or JoinerSituationKind.Underground or JoinerSituationKind.Outdoors;
                    if (awakeDependent && !awakeKnown) { findings.Add("joiner_trait_awake_unprojected"); continue; }
                    if (spatial && (row.Kind == JoinerSituationKind.Flames ? !fireKnown : !located))
                    { if (!awakeDependent || span.Awake) findings.Add("joiner_trait_location_unprojected"); continue; }
                    sum += SituationOffset(row, projected) * (end - start) / horizon;
                }
            }
            sums.Add(sum);
        }
        return Median(sums);
    }
}
