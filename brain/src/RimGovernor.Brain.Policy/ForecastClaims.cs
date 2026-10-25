namespace RimGovernor.Brain.Policy;

/// <summary>An exact or open material obligation, including native-held units. Empty bill means no native handoff; an unresolved bill link carries open units.</summary>
public sealed record ForecastMaterialClaim(Resource Resource, MaterialUnits Units, string ReservationBill);

/// <summary>Exclusive equipment at one service location. Windows exclude external/native occupancy.</summary>
public sealed record ForecastEquipmentCapacity(string Id, string Location, EquatableArray<ForecastTimeWindow> Windows);

public static class ForecastClaims
{
    /// <summary>One scenario's free stock after native holds and declared promises; unknowns are resource-local.</summary>
    public static IReadOnlyDictionary<Resource, MaterialUnits> ForecastMaterialBudgetOf(
        EquatableArray<Amount> stock, EquatableArray<IngredientReservation> reservations,
        IEnumerable<ForecastCommitment> commitments, double horizonDays)
    {
        if (!double.IsFinite(horizonDays) || horizonDays < 0) throw new DomainException("invalid material horizon");
        var promises = commitments.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var linked = new HashSet<(string, Resource)>();
        foreach (var commitment in promises)
        {
            if (commitment.MaterialClaims.Select(c => c.Resource).Distinct().Count() != commitment.MaterialClaims.Count
                || commitment.MaterialClaims.Any(c => commitment.StockChanges.Any(s => s.Resource == c.Resource)))
                throw new DomainException("material claim duplicates construction or stock effects");
            foreach (var claim in commitment.MaterialClaims)
            {
                if (!AdmissionRules.ValidResource(claim.Resource) || claim.Units is MaterialUnits.Exact(var count) && (!double.IsFinite(count) || count < 0))
                    throw new DomainException("invalid forecast material claim");
                if (claim.ReservationBill is { Length: > 0 } bill && !linked.Add((bill, claim.Resource)))
                    throw new DomainException("native reservation linked to multiple promises");
            }
        }
        var native = reservations;
        var nativeAmounts = new Dictionary<(string Bill, Resource Resource), double>();
        static void ValidateAmounts(IEnumerable<Amount> amounts)
        {
            var totals = new Dictionary<Resource, long>();
            foreach (var amount in amounts)
            {
                var total = totals.GetValueOrDefault(amount.Resource);
                if (!AdmissionRules.ValidResource(amount.Resource) || amount.Count < 0 || amount.Count > long.MaxValue - total)
                    throw new DomainException("invalid or overflowing material amount");
                totals[amount.Resource] = total + amount.Count;
            }
        }
        var observed = stock;
        ValidateAmounts(observed);
        ValidateAmounts(native.SelectMany(r => r.Items));
        foreach (var row in native)
        {
            if (row.Pawn.IsEmpty || string.IsNullOrWhiteSpace(row.Bill)) throw new DomainException("ingredient reservation requires a pawn and a bill");
            foreach (var amount in row.Items)
            {
                if (!AdmissionRules.ValidResource(amount.Resource) || amount.Count < 0) throw new DomainException("invalid native ingredient hold");
                var key = (row.Bill, amount.Resource);
                nativeAmounts[key] = nativeAmounts.GetValueOrDefault(key) + amount.Count;
            }
        }
        var free = MaterialBudgetRules.MaterialBudget(observed, new Dictionary<Resource, long>(), reservations, default);
        var result = free.ToDictionary(p => p.Key, p => (MaterialUnits)new MaterialUnits.Exact(p.Value));
        void Hold(Resource resource, MaterialUnits units)
        {
            if (!AdmissionRules.ValidResource(resource)) throw new DomainException("invalid forecast material resource");
            if (units is MaterialUnits.Exact(var count) && (!double.IsFinite(count) || count < 0))
                throw new DomainException("invalid forecast material claim");
            var before = result.GetValueOrDefault(resource, new MaterialUnits.Exact(0));
            MaterialUnits after = before is MaterialUnits.Exact(var value) && units is MaterialUnits.Exact(var debit) ? new MaterialUnits.Exact(value - debit) : new MaterialUnits.Open();
            if (after is MaterialUnits.Exact(var balance) && !double.IsFinite(balance)) throw new DomainException("material claim overflow");
            result[resource] = after;
        }
        foreach (var commitment in promises)
        {
            foreach (var claim in commitment.MaterialClaims)
            {
                var units = claim.Units;
                if (units is MaterialUnits.Exact(var count) && count == 0) { Hold(claim.Resource, units); continue; }
                var bill = claim.ReservationBill;
                if (bill.Length != 0)
                {
                    // Only the same observed bill can cover part of this promise.
                    if (units is MaterialUnits.Exact(var exact)) units = new MaterialUnits.Exact(Math.Max(0, exact - nativeAmounts.GetValueOrDefault((bill, claim.Resource))));
                }
                Hold(claim.Resource, units);
            }
            foreach (var change in commitment.StockChanges)
            {
                if (change.Day <= horizonDays && change.Units < 0) Hold(change.Resource, new MaterialUnits.Exact(-change.Units));
            }
        }
        return result;
    }

}
