namespace RimGovernor.Brain.Policy;

public static partial class WorkRules
{
    /// <summary>The first viable product wins; otherwise the last research candidate precedes unavailable alternatives.</summary>
    /// <param name="requests">The selected product followed by its alternatives; Runtime always supplies at least that selected product.</param>
    public static (Resource Resource, WorkshopChoice Choice) SelectWorkshopProduct(IEnumerable<WorkshopRequest> requests)
    {
        using var candidates = requests.GetEnumerator();
        if (!candidates.MoveNext()) throw new DomainException("workshop products are empty");
        var resource = candidates.Current.Resource;
        var choice = new WorkshopChoice { Method = WorkshopMethod.Unavailable };
        do
        {
            var request = candidates.Current;
            var candidate = SelectWorkshopBench(request);
            if (candidate.Method == WorkshopMethod.Build || candidate.Method == WorkshopMethod.Existing)
                return (request.Resource, candidate);
            if (candidate.Method == WorkshopMethod.Research || choice.Method == WorkshopMethod.Unavailable)
            {
                choice = candidate;
                resource = request.Resource;
            }
        } while (candidates.MoveNext());
        return (resource, choice);
    }

    public static int WorkshopOrdinal(IReadOnlyList<GearBench> benches, string definition) => benches.Count(b => b.Def == definition);
}
