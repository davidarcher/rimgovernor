using System.Collections.Concurrent;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>The tending a wound gets now (#3804, <see cref="ColonyValue.TendingNow"/>): whether anyone tends it, and the tend quality.</summary>
public sealed record ForecastTending(bool Tended, double Quality);

/// <summary>
/// The Hospital role's credit (#3804, epic #3650 P1): what a patient gains from lying in the planned hospital's bed rather than in
/// the bed it owns now (none: the ground, at the roomless scores). The room adds no tend quality (the doctor's, H1). Per colonist,
/// a served day earns:
/// <list type="bullet">
/// <item>Wound infection: raids a day × (the risk-transformed harm of one raid tended in its owned bed's room, less tended in the
/// hospital, HarmLedger.RaidOn), the wound's stored InfectionChanceFactor the only difference (the owned bed room's now,
/// <see cref="NeedBedRoom.InfectionChanceFactor"/>, against the hospital's at its predicted steady cleanliness,
/// <see cref="PlannedRoomCleanlinessRules.InfectionChance"/>), at the tend quality the colony's best doctor gives now
/// (<see cref="ForecastCoupledContext.Tending"/>, first care at once, as ColonyValue's raid loss).</item>
/// <item>Illness: disease incidents a day × (one incident's risk-transformed cost with its immunity gained at the owned bed's
/// ImmunityGainSpeedFactor, less at the hospital bed's, <see cref="ForecastCoupledContext.HospitalBed"/>, #3824).</item>
/// <item>Mood: the patient-days a day (raids a day × one raid's tended days to healed, plus the illness's
/// <see cref="IllnessPawn.PatientDaysPerDay"/>) × <see cref="ForecastHousing.GainPerDay"/> of the sleep thoughts the hospital bed
/// trades: ThoughtWorker_HospitalPatientRoomStats at the predicted Impressiveness stage in a Hospital room gained, and the ones
/// Toils_LayDown.ApplyBedThoughts gives in the owned bed lost (SleptOutside, SleptOnGround, the bedroom or barracks thought; a bed
/// a pawn does not own gives none of those).</item>
/// <item>Surgery, once (<see cref="RoomGain.Once"/>): each queued Bill_Medical on the colonist the schedule does not finish before
/// the hospital serves fails less often in the hospital bed: (the failure cost at the owned bed's SurgerySuccessChanceFactor, less at
/// the hospital bed's def factor × the room's SurgerySuccessChanceCleanlinessFactor) at the best surgeon's quality. A failure costs
/// its outcomes as SurgeryOutcomeEffectDef.GetOutcome tries them: death the rest of the horizon and the terminal value; a failure's
/// totalDamage dealt as wounds (HarmConsequences.Wounds), tended in the room the surgery was in.</item>
/// </list>
/// Throws a RequiredFactException when a fact the credit reads is unread: the room's prediction or layout room, the filth model's
/// curves, the threat or disease schedule, a colonist's harm reads, outfit or owned bed room, the tending, the hospital bed, a
/// thought stage row or a patient's mood.
/// </summary>
internal static class ForecastHospital
{
    /// <summary>The Hospital RoomRoleDef the patient thought requires.</summary>
    internal const string HospitalRole = "Hospital";

    /// <summary>The role of ThoughtWorker_HospitalPatientRoomStats's thought rows (NativeNeedsForecast).</summary>
    internal const string PatientRole = "hospital_patient";

    /// <summary>
    /// The bleeding share of GiveRandomSurgeryInjuries' wounds against the harm model's 0.06 a severity: its four damage defs equally, the
    /// Cut, Scratch and Stab injuries bleeding at 0.06 and Crush at 0.01 (injuryProps.bleedRate), (3 x 0.06 + 0.01) / (4 x 0.06).
    /// </summary>
    internal const double SurgeryBleedShare = 19d / 24;

    /// <summary>
    /// The mechanics the credit leaves out, named with every served hospital: the surgeries a raid's part losses queue (HarmLedger
    /// does not forecast part loss, #3847), the vitals monitor and other facilities a planned bed would link, the BotchedMySurgery
    /// thought, the temperature sleep thoughts, the medical beds the colony already has (#3817), the surgery outcome classes other
    /// than success, death and failure, and the surgeries on non-colonists.
    /// </summary>
    internal static readonly string[] Unmodelled =
    [
        "hospital_raid_surgery_unmodelled", "hospital_bed_facilities_unmodelled", "surgery_botched_thought_unmodelled",
        "hospital_sleep_temperature_unmodelled", "hospital_standing_medical_beds_unmodelled", "surgery_other_outcomes_unmodelled",
        "hospital_noncolonist_surgery_unmodelled",
    ];

    /// <summary>One colonist's per-day parts of a hospital's credit.</summary>
    private sealed record Patient(string Pawn, double InfectionPerDay, double IllnessPerDay, double PatientDaysPerDay, double MoodDelta);

    /// <summary>One queued surgery's once gain, and the patient it is booked to.</summary>
    private sealed record Operation(string Demand, string Pawn, double Once);

    private sealed record Part(Patient[] Patients, Operation[] Operations);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, ConcurrentDictionary<(int Room, string Stuff), Part>> Cache = new();

    /// <summary>The served hospital's gains (<see cref="RoomCredit"/>, see the class summary): a row per colonist under its id, and a row per queued surgery under <c>surgery:{demand}</c>.</summary>
    internal static IReadOnlyList<RoomGain> HospitalCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        var part = Cache.GetValue(context, _ => new()).GetOrAdd((room.Room, room.Stuff), key => PartOf(context, key.Room, key.Stuff));
        var gains = new List<RoomGain>();
        foreach (var patient in part.Patients)
        {
            var mood = 0.0;
            if (patient.PatientDaysPerDay > 0 && patient.MoodDelta != 0)
            {
                mood = ForecastHousing.GainPerDay(patient.MoodDelta, outcome.Needs.GetValueOrDefault(patient.Pawn)?.Mood, context.MoodBreaks,
                    context.Value.WellbeingWeight, horizon / GameTime.TicksPerDay) ?? throw new RequiredFactException("mood:" + patient.Pawn);
            }

            var perDay = patient.InfectionPerDay + patient.IllnessPerDay + (patient.PatientDaysPerDay * mood);
            if (perDay != 0)
            {
                gains.Add(new(patient.Pawn, patient.Pawn, perDay));
            }
        }

        foreach (var operation in part.Operations)
        {
            // A bill the schedule finishes before the hospital serves gains nothing from it.
            var done = outcome.Plan.Allocations.Any(a => a.End <= room.ServedAt && context.Bindings.TryGetValue((a.Activity, a.Pawn), out var binding)
                && binding.SupportDemand == operation.Demand && binding.Job == "DoBill");
            if (!done && operation.Once != 0)
            {
                gains.Add(new("surgery:" + operation.Demand, operation.Pawn, 0) { Once = operation.Once });
            }
        }

        return gains;
    }

    /// <summary>
    /// Per colonist, the distinct nonzero hospital mood shift (delta / 100) its mood timeline integrates breaks at, for each priced
    /// planned hospital's closures. A delta that cannot be read yet registers no shift; the credit then throws for it.
    /// </summary>
    internal static IReadOnlyDictionary<string, double[]> Shifts(ForecastCoupledContext context)
    {
        var shifts = new Dictionary<string, SortedSet<double>>(StringComparer.Ordinal);
        if (!context.RoomCredits.ContainsKey(PlannedRole.Hospital))
        {
            return new Dictionary<string, double[]>();
        }

        foreach (var closure in context.Closures)
        {
            if (!context.PlannedRoomRoles.TryGetValue(closure.Room, out var role) || role != PlannedRole.Hospital)
            {
                continue;
            }

            foreach (var pawn in context.Needs.Pawns.DistinctBy(p => p.Pawn))
            {
                double delta;
                try
                {
                    delta = MoodDelta(pawn, closure.Prediction);
                }
                catch (RequiredFactException)
                {
                    continue;
                }

                if (delta == 0)
                {
                    continue;
                }

                if (!shifts.TryGetValue(pawn.Pawn, out var set))
                {
                    shifts[pawn.Pawn] = set = [];
                }

                set.Add(delta / 100);
            }
        }

        return shifts.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>
    /// The mood delta of one patient-day in the hospital <paramref name="prediction"/> predicts (see the class summary). Throws when
    /// the predicted stage, a thought stage row or the owned bed's room is unread.
    /// </summary>
    internal static double MoodDelta(NeedsForecastPawn pawn, PlannedRoomPrediction prediction)
    {
        if (prediction.Role.Length == 0)
        {
            throw new RequiredFactException("planned_room_prediction:" + prediction.Room);
        }

        var stages = pawn.ThoughtStages;
        double Row(string role) => stages.FirstOrDefault(r => r.Role == role && r.Stage == 0)?.Offset ?? throw new RequiredFactException(role + " thought stage");
        var gained = 0.0;
        if (prediction.Role == HospitalRole)
        {
            var stage = ForecastHousing.PlannedStage(prediction);
            var rows = stages.Where(r => r.Role == PatientRole && r.Stage == stage).DistinctBy(r => r.Thought).ToArray();
            if (rows.Length == 0)
            {
                throw new RequiredFactException(PatientRole + " thought stage");
            }

            gained = rows.Sum(r => r.Offset);
        }

        if (pawn.OwnedBed.Length == 0)
        {
            return gained - Row("slept_on_ground");
        }

        var room = OwnedRoom(pawn);
        var now = (room.PsychologicallyOutdoors ? Row("slept_outside") : 0) + (room.Costless ? Row("slept_on_ground") : 0);
        if (!pawn.Ascetic)
        {
            now += ForecastHousing.OwnedRoomThought(room, stages) ?? throw new RequiredFactException("owned room thought stage:" + pawn.Pawn);
        }

        return gained - now;
    }

    private static NeedBedRoom OwnedRoom(NeedsForecastPawn pawn) => pawn.OwnedBedRoom ?? throw new RequiredFactException("owned_bed_room:" + pawn.Pawn);

    private static Part PartOf(ForecastCoupledContext context, int room, string stuff)
    {
        var closures = context.Closures.Where(c => c.Room == room).OrderBy(c => c.Stuff, StringComparer.Ordinal).ToArray();
        var closure = closures.FirstOrDefault(c => c.Stuff == stuff) ?? closures.FirstOrDefault() ?? throw new RequiredFactException("planned_room_prediction:" + room);
        if (!context.LayoutRooms.TryGetValue(room, out var planned))
        {
            throw new RequiredFactException("planned_room_layout:" + room);
        }

        var prediction = closure.Prediction;
        var runs = planned.FilthRuns.Values;
        var filth = context.Filth;
        var pawns = context.Needs.Pawns.DistinctBy(p => p.Pawn).OrderBy(p => p.Pawn, StringComparer.Ordinal).ToArray();
        if (pawns.Length == 0)
        {
            return new([], []);
        }

        var days = context.InvestmentHorizonDays;
        var horizon = days * GameTime.TicksPerDay;
        var tending = context.Tending ?? throw new RequiredFactException("tend_quality");
        var bed = context.HospitalBed.Def.Length != 0 ? context.HospitalBed : throw new RequiredFactException("hospital_bed");
        var raidsPerDay = HarmLedger.RaidsPerDay(context.Harm) ?? throw new RequiredFactException(HarmLedger.ThreatScheduleUnknown);
        var schedule = context.Harm.Illness;
        if (!schedule.Assessed)
        {
            throw new RequiredFactException("illness_schedule");
        }

        var incidents = schedule.IncidentsPerDay(days);
        double? hospitalInfection = null;
        double HospitalInfection() => hospitalInfection ??= PlannedRoomCleanlinessRules.InfectionChance(prediction, runs, filth);
        double Roomless(IReadOnlyList<CurvePoint> curve, double score, string unread) => curve.Count != 0 ? score : throw new RequiredFactException(unread);
        double InfectionNow(NeedsForecastPawn pawn) => pawn.OwnedBed.Length == 0
            ? Roomless(filth.InfectionChance, filth.InfectionChanceRoomless, "infection_chance_curve") : OwnedRoom(pawn).InfectionChanceFactor;
        HarmConsequences.Care Care(double roomFactor) => new(tending.Tended, tending.Quality, tending.Tended ? 0 : double.PositiveInfinity, roomFactor);
        double Risk(HarmDistribution distribution)
        {
            var (mean, tail) = HarmLedger.Moments(distribution, context.Value.HarmRiskAlpha);
            return HarmLedger.Risk(mean, tail, context.Value.HarmRiskWeight);
        }

        var patients = new List<Patient>(pawns.Length);
        foreach (var pawn in pawns)
        {
            double infection = 0, patientDays = 0;
            if (raidsPerDay > 0)
            {
                var hospital = HarmLedger.RaidOn(context, pawn, Care(HospitalInfection()), horizon);
                patientDays += raidsPerDay * hospital.PatientDays;
                if (tending.Tended)
                {
                    infection = raidsPerDay * (HarmLedger.RaidOn(context, pawn, Care(InfectionNow(pawn)), horizon).Risk - hospital.Risk);
                }
            }

            double illness = 0;
            if (incidents > 0)
            {
                var immunityNow = pawn.OwnedBed.Length == 0 ? 1 : OwnedRoom(pawn).ImmunityGainSpeedFactor;
                var now = IllnessForecast.Of(schedule, pawn, context.CapacityPenalties, Care(1), immunityNow, context.Value, horizon);
                var hospital = IllnessForecast.Of(schedule, pawn, context.CapacityPenalties, Care(1), bed.ImmunityGainSpeedFactor, context.Value, horizon);
                if (!now.Known || !hospital.Known)
                {
                    throw new RequiredFactException("illness_harm:" + pawn.Pawn);
                }

                illness = incidents * (Risk(now) - Risk(hospital));
                patientDays += incidents * hospital.PatientDays;
            }

            patients.Add(new(pawn.Pawn, infection, illness, patientDays, patientDays > 0 ? MoodDelta(pawn, prediction) : 0));
        }

        var rows = pawns.ToDictionary(p => p.Pawn, StringComparer.Ordinal);
        var operations = new List<Operation>();
        double? hospitalSurgery = null;
        foreach (var demand in context.Support.Demands.Where(d => d.Kind == "surgery").OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            // A surgery on a prisoner, a slave or an animal is no colonist's (named in Unmodelled); one without an outcome effect cannot
            // fail, and one that ignores its environment fails alike in either bed.
            if (!rows.TryGetValue(demand.Target, out var patient) || demand.SurgeryOutcomes.Count == 0 || !demand.SurgeryEnvironment)
            {
                continue;
            }

            var surgeons = context.Support.Workers.Where(w => w.Demand == demand.Id).Select(w => w.SurgeryQuality).ToArray();
            if (surgeons.Length == 0)
            {
                continue;
            }

            var quality = surgeons.Max();
            var now = patient.OwnedBed.Length == 0
                ? Roomless(filth.SurgeryCleanliness, filth.SurgeryCleanlinessRoomless, "surgery_cleanliness_curve") : OwnedRoom(patient).SurgerySuccessFactor;
            hospitalSurgery ??= bed.SurgerySuccessChanceFactor * PlannedRoomCleanlinessRules.SurgeryCleanliness(prediction, runs, filth);
            var once = FailureCost(demand, quality * now, patient, context, Care(InfectionNow(patient)), horizon, days)
                - FailureCost(demand, quality * hospitalSurgery.Value, patient, context, Care(HospitalInfection()), horizon, days);
            operations.Add(new(demand.Id, patient.Pawn, once));
        }

        return new([.. patients], [.. operations]);
    }

    /// <summary>
    /// The expected cost of one surgery at success quality <paramref name="quality"/> (before the clamp): its outcomes walked in order,
    /// each taking its chance of what the earlier ones left (success at the clamped quality), death costing the rest of the horizon and
    /// the terminal value, a failure its wounds' risk-transformed harm under <paramref name="care"/>.
    /// </summary>
    private static double FailureCost(SupportDemand demand, double quality, NeedsForecastPawn patient, ForecastCoupledContext context,
        HarmConsequences.Care care, double horizon, double days)
    {
        var left = 1.0;
        var cost = 0.0;
        foreach (var outcome in demand.SurgeryOutcomes)
        {
            var chance = Math.Clamp(outcome.Kind == "success" ? Math.Clamp(quality, demand.SurgeryQualityMin, demand.SurgeryQualityMax) : outcome.Chance, 0, 1);
            var share = left * chance;
            left -= share;
            if (share <= 0)
            {
                continue;
            }

            if (outcome.Kind == "death")
            {
                cost += share * (days + context.Value.DeathTerminalValue);
            }
            else if (outcome.Kind == "failure" && outcome.Damage > 0)
            {
                var wounds = HarmConsequences.Wounds(outcome.Damage, outcome.WoundSeverity, SurgeryBleedShare, patient, context.CapacityPenalties, care, context.Value, horizon);
                if (!wounds.Known)
                {
                    throw new RequiredFactException("surgery_harm:" + patient.Pawn);
                }

                var (mean, tail) = HarmLedger.Moments(wounds, context.Value.HarmRiskAlpha);
                cost += share * HarmLedger.Risk(mean, tail, context.Value.HarmRiskWeight);
            }
        }

        return cost;
    }
}
