namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Declares the surgery bills of the care purposes through the rounds review.</summary>
public sealed class RoundsCareBillDeclarer : IOrderDeclarer
{
    private readonly Rounder _reviewer;
    private readonly object _native;
    private readonly bool _surgery;

    public RoundsCareBillDeclarer(Rounder reviewer, object native, bool surgery)
    {
        _reviewer = reviewer;
        _native = native;
        _surgery = surgery;
    }

    internal List<ConcernId> Concerns()
    {
        var output = new List<ConcernId>();
        if (_surgery)
        {
            output.Add(PolicyApi.SurgeryNeed.FilingKey);
        }
        return output;
    }

    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var journal = _reviewer.Player.Journal;
        var review = journal.LoadRounds(ct);
        var output = new Declared();
        if (!review.Enabled || review.Snapshot != snapshot)
        {
            foreach (var c in Concerns())
            {
                output.Merge(PolicyApi.Abstaining(Cause.UnreadReview).For(c.Value));
            }
            return Task.FromResult(output);
        }
        if (_surgery)
        {
            // The medical/surgery Need's part bill (#3258): declared while the Need is unmet, nothing while it is met,
            // an abstain while the care census is unread.
            var standing = PolicyApi.SurgeryNeedOf(MedicalFacts.From(projection.Facts, _reviewer.Policy, review.Latches.MedicalReserve)).Satisfaction;
            var one = standing switch
            {
                Satisfaction.Unknown => PolicyApi.Abstaining(Cause.UnreadMedicine),
                Satisfaction.Met => new Declared(),
                _ => SurgeryPartBill(projection, benches),
            };
            output.Merge(one.For(PolicyApi.SurgeryNeed.FilingKey.Value));
        }
        return Task.FromResult(output);
    }

    /// <summary>The surgery part bill: one batch bill fabricating a part a restore operation lacks.</summary>
    private static Declared SurgeryPartBill(ColonyProjection projection, IReadOnlyList<GearBench> benches)
    {
        var (parts, productions) = Rounder.SurgeryPartsOn(projection.Facts.MedicalPawns, projection.SurgeryContext(), benches);
        var (selected, gap) = PolicyApi.SelectSurgeryPartBill(productions, parts);
        return PolicyApi.DeclareSelection(selected, gap == default, benches);
    }
}
