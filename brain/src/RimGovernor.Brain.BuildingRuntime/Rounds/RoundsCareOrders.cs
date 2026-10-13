namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Declares the surgery, baby feeding and mech bills of the care purposes through the rounds review.</summary>
public sealed class RoundsCareBillDeclarer
{
    private readonly Rounder _reviewer;
    private readonly IBillPlannerNative _native;
    private readonly bool _surgery;
    private readonly bool _baby;
    private readonly bool _mech;

    public RoundsCareBillDeclarer(Rounder reviewer, IBillPlannerNative native, bool surgery, bool baby, bool mech)
    {
        _reviewer = reviewer;
        _native = native;
        _surgery = surgery;
        _baby = baby;
        _mech = mech;
    }

    internal List<ConcernId> Concerns()
    {
        var output = new List<ConcernId>();
        if (_surgery)
        {
            output.Add(PolicyApi.MaintainSurgery);
        }
        if (_baby)
        {
            output.Add(PolicyApi.MaintainBabyFeeding);
        }
        if (_mech)
        {
            output.Add(PolicyApi.MaintainMechs);
        }
        return output;
    }

    public Declared DeclareOrders(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var journal = _reviewer.Player.Journal;
        var review = journal.LoadRounds(ct);
        var output = new Declared();
        if (!review.Enabled || review.Snapshot != snapshot)
        {
            foreach (var c in Concerns())
            {
                output.Merge(PolicyApi.Abstaining(PolicyApi.CauseUnreadReview).For(c));
            }
            return output;
        }

        void Collect(ConcernId need, bool enabled, Func<Declared> declare)
        {
            if (!enabled)
            {
                return;
            }
            var (_, workable) = journal.Workable(review, need, ct);
            if (!workable)
            {
                return;
            }
            var one = declare();
            output.Merge(one.For(need));
        }

        Collect(PolicyApi.MaintainSurgery, _surgery, () =>
        {
            var (parts, productions) = SurgeryPartsOn(projection.Facts.MedicalPawns, projection.SurgeryContext(), benches);
            var (selected, gap) = PolicyApi.SelectSurgeryPartBill(productions, parts);
            return PolicyApi.DeclareSelection(selected, gap == default, benches);
        });
        Collect(PolicyApi.MaintainBabyFeeding, _baby, () =>
        {
            if (!projection.Facts.BabyFeeding.TryGetValue(out var babies))
            {
                return PolicyApi.Abstaining(PolicyApi.CauseUnreadBabyFeeding);
            }
            var (selected, ok) = PolicyApi.SelectProductionBill(PolicyApi.BabyFoodBill, projection.ProductionBenches, projection.Facts.Colonists, default(Fact<double>), default(Fact<double>), 1, new ProductionBillContext { BabyFeeding = babies });
            return PolicyApi.DeclareSelection(selected, ok, benches);
        });
        Collect(PolicyApi.MaintainMechs, _mech, () =>
        {
            MechGestation? gestation;
            try
            {
                gestation = MechGestation(_native, BoundaryApi.Identity(snapshot), projection, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return AbstainOnRead(ct, PolicyApi.CauseUnreadMechs);
            }
            if (gestation is null)
            {
                return PolicyApi.Abstaining(PolicyApi.CauseUnreadMechs);
            }
            var (selected, gap) = PolicyApi.SelectMechGestationBill(MechBenches(benches), gestation);
            return PolicyApi.DeclareSelection(selected, gap == default, benches);
        });
        return output;
    }
}
