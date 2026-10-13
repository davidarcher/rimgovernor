namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Declares the surgery, baby feeding and mech bills of the care purposes through the rounds review.</summary>
public sealed class RoundsCareBillDeclarer : IOrderDeclarer
{
    private readonly Rounder _reviewer;
    private readonly object _native;
    private readonly bool _surgery;
    private readonly bool _baby;
    private readonly bool _mech;

    public RoundsCareBillDeclarer(Rounder reviewer, object native, bool surgery, bool baby, bool mech)
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

    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var journal = _reviewer.Player.Journal;
        var review = journal.LoadRounds(ct);
        var output = new Declared();
        if (!review.Enabled || review.Snapshot != snapshot)
        {
            foreach (var c in Concerns())
            {
                output.Merge(PolicyApi.Abstaining(Cause.UnreadReview).For(c));
            }
            return output;
        }

        async Task Collect(ConcernId need, bool enabled, Func<Task<Declared>> declare)
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
            var one = await declare().ConfigureAwait(false);
            output.Merge(one.For(need));
        }

        await Collect(PolicyApi.MaintainSurgery, _surgery, () =>
        {
            var (parts, productions) = SurgeryPartsOn(projection.Facts.MedicalPawns, projection.SurgeryContext(), benches);
            var (selected, gap) = PolicyApi.SelectSurgeryPartBill(productions, parts);
            return Task.FromResult(PolicyApi.DeclareSelection(selected, gap == default, benches));
        }).ConfigureAwait(false);
        await Collect(PolicyApi.MaintainBabyFeeding, _baby, () =>
        {
            if (!projection.Facts.BabyFeeding.TryGetValue(out var babies))
            {
                return Task.FromResult(PolicyApi.Abstaining(Cause.UnreadBabyFeeding));
            }
            var (selected, ok) = PolicyApi.SelectProductionBill(PolicyApi.BabyFoodBill, projection.ProductionBenches, projection.Facts.Colonists, default(Fact<double>), default(Fact<double>), 1, new ProductionBillContext { BabyFeeding = babies });
            return Task.FromResult(PolicyApi.DeclareSelection(selected, ok, benches));
        }).ConfigureAwait(false);
        await Collect(PolicyApi.MaintainMechs, _mech, async () =>
        {
            MechGestation? gestation;
            try
            {
                gestation = await MechGestationAsync(_native, Boundary.Identity(snapshot), projection, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return AbstainOnRead(Cause.UnreadMechs, ex, ct);
            }
            if (gestation is null)
            {
                return PolicyApi.Abstaining(Cause.UnreadMechs);
            }
            var (selected, gap) = PolicyApi.SelectMechGestationBill(MechBenches(benches), gestation);
            return PolicyApi.DeclareSelection(selected, gap == default, benches);
        }).ConfigureAwait(false);
        return output;
    }
}
