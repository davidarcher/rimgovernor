namespace RimGovernor.Brain.Policy.Methods.Evaluation;

internal static class WorkMaterialEvaluation
{
    internal static MaterialUnits ExistingBillCredit(WorkResourceView view, string bill, Resource resource, double required)
    {
        if (bill.Length == 0) return new MaterialUnits.Exact(0);
        if (view.Evidence.Promises is not ProductionPromiseSet.Assessed(var promises)) return new MaterialUnits.Open();
        var claims = promises.Concat(view.Claims.Materials).SelectMany(p => p.MaterialClaims)
            .Where(c => c.Resource == resource && c.ReservationBill == bill).ToArray();
        if (claims.Any(c => c.Units is MaterialUnits.Open)) return new MaterialUnits.Open();
        return new MaterialUnits.Exact(Math.Min(required, Math.Max(claims.Select(c => ((MaterialUnits.Exact)c.Units).Count).DefaultIfEmpty(0).Max(),
            view.Evidence.Reservations.Where(r => r.Bill == bill).SelectMany(r => r.Items)
                .Where(i => i.Resource == resource).Sum(i => (double)i.Count))));
    }
}
