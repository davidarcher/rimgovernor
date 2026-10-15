namespace RimGovernor.Brain.Policy.Methods.Evaluation;

internal static class WorkMaterialEvaluation
{
    internal static Fact<double> ExistingBillCredit(WorkResourceView view, string bill, Resource resource, double required)
    {
        if (bill.Length == 0) return Known(0.0);
        if (!view.Evidence.ForecastPromises.TryGetValue(out var promises) || !view.Evidence.Reservations.TryGetValue(out var reservations)) return default;
        var claims = promises.Concat(view.Claims.Materials).SelectMany(p => p.MaterialClaims)
            .Where(c => c.Resource == resource && c.ReservationBill.TryGetValue(out var id) && id == bill).ToArray();
        if (claims.Any(c => !c.Units.IsKnown)) return default;
        return Known(Math.Min(required, Math.Max(claims.Select(c => c.Units.Value).DefaultIfEmpty(0).Max(),
            reservations.Where(r => r.Bill.TryGetValue(out var id) && id == bill).SelectMany(r => r.Items)
                .Where(i => i.Resource == resource).Sum(i => (double)i.Count))));
    }
}
