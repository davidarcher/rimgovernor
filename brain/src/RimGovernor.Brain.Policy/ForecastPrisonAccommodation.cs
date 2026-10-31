using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// The prison accommodation term (RG-HABITAT-001 "Guests or prisoners need accommodation"), beside the recruitment term and
// never derived from it. Confirmed with ilspycmd on Assembly-CSharp.dll (1.7):
// - CompAssignableToPawn_Bed.CanAssignTo: a prisoner owns only a bed set for prisoners (ForPrisoners), so a prisoner owning
//   no bed has no legal slot; WorkGiver_Warden_TakeToBed carries an unbedded prisoner to a free prison bed.
// - A bedless prisoner keeps renewing the memories of sleeping on the ground and eating without a table
//   (MemoryGroupRead.BedlessRenewal, #3966); bedding it ends that renewal.
// - WorkGiver_Warden_DeliverFood feeds only a prisoner whose position IsInPrisonCell (Room.IsPrisonCell: RoomCanBePrisonCell
//   and a prison bed in or beside the room), so an unbedded prisoner outside a cell is not fed by wardens; the hunger and
//   mortality course that follows is not priced here (named in AccommodationNotModelled).

public static partial class ForecastPrison
{
    /// <summary>The known-zero state of a prisoner already owning a prison bed: the room accommodates nobody new for it.</summary>
    public const string Accommodated = "prison_accommodated:";

    /// <summary>The known-zero state of a bedless prisoner whose bedless memories cost it no mood (none renewed, or no mood need).</summary>
    public const string AccommodationNoMoodCost = "prison_accommodation_no_mood_cost:";

    /// <summary>The state of a bedless prisoner no free prison bed position of the served prisons is left for.</summary>
    public const string AccommodationNoBed = "prison_accommodation_no_bed:";

    /// <summary>The accommodation mechanics the term leaves out, named with each served prison.</summary>
    public static readonly EquatableArray<string> AccommodationNotModelled =
    [
        "prisoner_unfed_outside_cell_not_modelled", // WorkGiver_Warden_DeliverFood: the hunger course of an unbedded prisoner (task 6.3)
    ];

    /// <summary>The Prison room's credit: the recruitment term (<see cref="PrisonCredit"/>) and the accommodation term (<see cref="AccommodationCredit"/>), additive.</summary>
    public static IReadOnlyList<RoomGain> PrisonRoomCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        [.. PrisonCredit(room, outcome, commitments, context, horizon), .. AccommodationCredit(room, outcome, commitments, context, horizon)];

    /// <summary>The Prison room's findings: both terms' named states and what each leaves out.</summary>
    public static IEnumerable<string> PrisonRoomFindings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        Findings(room, outcome, commitments, context, horizon).Concat(AccommodationFindings(room, outcome, commitments, context, horizon)).Distinct();

    /// <summary>
    /// The accommodation term: one row, <c>accommodate/&lt;pawn&gt;</c>, per held prisoner owning no bed that the room gives a
    /// free prison bed position its warden reaches (native scan order, over the served prisons in service order, a position a
    /// bedded prisoner owns never taken), worth a day the mood its bedless memories cost (<see cref="BedlessMoodCost"/>) at
    /// <see cref="ForecastValuePolicy.WellbeingWeight"/> capability-days per mood-day. No recruitment value enters it.
    /// </summary>
    public static IReadOnlyList<RoomGain> AccommodationCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        AccommodationRows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Gains ?? [];

    /// <summary>The accommodation term's findings: the known-zero and unbedded states, and what it leaves out.</summary>
    public static IEnumerable<string> AccommodationFindings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        AccommodationRows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Findings ?? [.. AccommodationNotModelled];

    /// <summary>The mood <paramref name="prisoner"/>'s bedless memories cost it while it stays bedless: the renewed groups' negative offsets; null without a mood need.</summary>
    public static double? BedlessMoodCost(PrisonerRecruitRead prisoner) => prisoner.Mood is not { } mood ? null
        : mood.Memories.Sum(g => g.BedlessRenewal is { } renewed ? Math.Max(0, -MoodGroupAt(g with { Members = [renewed] }, 0)) : 0);

    private static readonly PerOutcome<Dictionary<int, RoomRows>> AccommodationRows = new(ComputeAccommodation);

    private static Dictionary<int, RoomRows> ComputeAccommodation(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        var days = context.InvestmentHorizonDays;
        var served = outcome.ServedRooms
            .Where(s => PlannedRoomCredits.Serves(context, s.Room, PlannedRole.Prison) && RingClosure.CreditDays(days, s.ServedAt) > 0)
            .OrderBy(s => s.ServedAt).ThenBy(s => s.Room).ToArray();
        var output = new Dictionary<int, RoomRows>();
        if (served.Length == 0) return output;
        var read = context.Needs.Prison;
        var known = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var bedded in read.Prisoners.Where(p => p.PrisonBed)) known.Add(Accommodated + bedded.Pawn);
        var queue = read.Prisoners.Where(p => !p.PrisonBed).ToList();
        var warden = read.Wardens.OrderByDescending(w => w.NegotiationAbility).ThenBy(w => w.Pawn, StringComparer.Ordinal).FirstOrDefault();
        if (queue.Count > 0 && warden is null) known.Add(NoWarden);
        var taken = read.Prisoners.Where(p => p.PrisonBed && p.OwnedPrisonBedPosition is not null).Select(p => p.OwnedPrisonBedPosition!.Cell).ToHashSet();
        foreach (var room in served)
        {
            var states = new SortedSet<string>(known, StringComparer.Ordinal);
            var gains = new List<RoomGain>();
            var prediction = PlannedRoomCredits.Prediction(context, room.Room, room.Stuff);
            Cell[] positions = prediction is null ? [] : context.PrisonBeds.GetValueOrDefault(room.Room) ?? [];
            foreach (var position in positions)
            {
                if (warden is null || queue.Count == 0) break;
                if (!taken.Add(position)) continue;
                // A warden carries the prisoner to the bed (WorkGiver_Warden_TakeToBed): the bed must be reachable from it.
                var origin = context.Needs.Pawns.FirstOrDefault(p => p.Pawn == warden.Pawn)?.Location ?? throw new RequiredFactException("warden upkeep:" + warden.Pawn);
                var inbound = prediction!.Prediction.Routes.FirstOrDefault(r => r.Pawn == warden.Pawn && r.From == origin && r.To == position && r.Kind == ProjectedRouteKind.PrisonInteraction)
                    ?? throw new RequiredFactException("prison accommodation route:" + warden.Pawn + ":" + position);
                if (inbound.Ticks is not RouteTicks.Reachable)
                {
                    states.Add("prison_accommodation_unreachable:" + position);
                    continue;
                }
                var prisoner = queue[0];
                queue.RemoveAt(0);
                var cost = BedlessMoodCost(prisoner) ?? 0;
                if (cost <= 0)
                {
                    states.Add(AccommodationNoMoodCost + prisoner.Pawn);
                    continue;
                }
                gains.Add(new RoomGain("accommodate/" + prisoner.Pawn, "", context.Value.WellbeingWeight * cost / 100));
            }
            output[room.Room] = new([.. gains], [.. AccommodationNotModelled, .. states]);
        }
        if (queue.Count > 0 && warden is not null)
        {
            var last = served[^1].Room;
            output[last] = output[last] with { Findings = [.. output[last].Findings, .. queue.Select(p => AccommodationNoBed + p.Pawn)] };
        }
        return output;
    }
}
