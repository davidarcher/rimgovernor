namespace RimGovernor.Brain.Policy;

public static partial class ForecastCoupled
{
    internal sealed partial class CoupledSearchCache
    {
        private ScheduledEffect[] medicineTreatments = [];
        private int nextMedicineTreatment;
        private Resource[] medicineKinds = [];
        private EquatableArray<double> initialMedicine;
        private readonly List<ModelEvent> medicineEvents = [];
        private readonly Dictionary<string, SkillTrack> medicineSkills = new(StringComparer.Ordinal);
        private Func<double, EquatableArray<SkillTrack>>? medicineSkillsAt;

        private void InitializeMedicine(ColonyForecastRequest request, ForecastActivityPlan plan)
        {
            medicineTreatments = context.ModelBasis?.Policy.Scheduled.Where(e => e.Kind == ModelEventKind.MedicineTreatment && e.Day <= request.HorizonDays)
                .OrderBy(e => e.Day).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray() ?? [];
            nextMedicineTreatment = 0;
            medicineEvents.Clear(); medicineSkills.Clear(); medicineSkillsAt = null;
            medicineKinds = [.. items.MedicalPotency.OrderByDescending(p => p.Value).ThenBy(p => p.Key.Value, StringComparer.Ordinal).Select(p => p.Key)];
            foreach (var row in census.Values.Where(r => r.Medicine && r.Count > 0))
                if (!items.MedicalPotency.ContainsKey(row.Resource)) throw new RequiredFactException("medicine potency:" + row.Resource);
            initialMedicine = EquatableArray.From(MedicineHeld());
            if (context.ModelBasis is not { } basis) return;
            medicineSkillsAt = day =>
            {
                var gaps = new SortedSet<string>(StringComparer.Ordinal);
                var pawns = ForecastHandoffPolicy.Pawns(basis.Input.Pawns, context.PracticeTasks, plan, finishedAt, day, gaps, completed);
                foreach (var gap in gaps) Finding(gap, "medicine");
                foreach (var track in pawns.SelectMany(p => SkillStock.Of(p, gaps)).Select(s => s.Track()))
                {
                    EquatableArray<SkillLevelDate> dates = medicineSkills.TryGetValue(track.Key, out var previous) ? previous.Dates : [];
                    medicineSkills[track.Key] = track with
                    {
                        StoredLevel = previous?.StoredLevel ?? track.StoredLevel,
                        Dates = [.. dates, new SkillLevelDate(day, track.StoredLevel)],
                    };
                }
                return EquatableArray.From(medicineSkills.Values.OrderBy(s => s.Key, StringComparer.Ordinal));
            };
            _ = medicineSkillsAt(0);
        }

        private double[] MedicineHeld() => medicineKinds.Select(kind => lots.Where(l => l.Key.Resource == kind
            && census.TryGetValue(l.Key.Lot, out var row) && row.Medicine && !outsidePhysical.Contains(l.Key.Lot)).Sum(l => l.Value)).ToArray();

        private ColonyTrajectory? MedicineHistory(double day)
        {
            if (medicineSkillsAt is null) return null;
            return new ColonyTrajectory
            {
                Events = [.. medicineEvents, new ModelEvent { Day = day, Kind = ModelEventKind.HorizonEnd, Medicine = EquatableArray.From(MedicineHeld()) }],
                MedicineKinds = EquatableArray.From(medicineKinds), StartMedicine = initialMedicine, Skills = medicineSkillsAt(day),
            };
        }

        private void ApplyMedicineTreatment(ScheduledEffect effect)
        {
            Expire(effect.Day * GameTime.TicksPerDay);
            var before = MedicineHeld();
            var history = MedicineHistory(effect.Day) ?? throw new RequiredFactException("colony_model_treatment_history_unobserved");
            // The current snapshot is before this slot's debit, so the same callback can read it in both engines.
            history = history with { Events = [.. medicineEvents, new ModelEvent { Day = effect.Day, Kind = ModelEventKind.MedicineTreatment, Medicine = EquatableArray.From(before) }] };
            var count = -effect.Amount + (effect.MedicineDemand?.Invoke(history, effect.Day) ?? 0);
            DomainException.Require(double.IsFinite(count) && count >= 0, "invalid scheduled medicine demand");
            var after = before.ToArray();
            ColonyModel.UseMedicine(after, Math.Max(0, before.Sum() - count));
            for (var k = 0; k < medicineKinds.Length; k++)
            {
                var debit = before[k] - after[k];
                if (debit <= 0) continue;
                DebitLots(medicineKinds[k], debit, medicineOnly: true);
                balance[medicineKinds[k]] = balance.GetValueOrDefault(medicineKinds[k]) - debit;
                effects.Add(new(medicineKinds[k], effect.Day, -debit));
            }
            medicineEvents.Add(new ModelEvent
            {
                Day = effect.Day, Kind = ModelEventKind.MedicineTreatment, Id = effect.Id,
                Medicine = EquatableArray.From(after), MedicineConsumed = EquatableArray.From(before.Select((held, k) => held - after[k])),
            });
        }
    }
}
