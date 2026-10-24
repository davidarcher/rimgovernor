namespace RimGovernor.Brain.Policy;

/// <summary>Exact promised units, including native-held units. Known empty bill means not handed off; unknown link stays unknown.</summary>
public sealed record ForecastMaterialClaim(Resource Resource, Fact<double> Units, Fact<string> ReservationBill);

/// <summary>Exclusive equipment at one service location. Windows exclude external/native occupancy.</summary>
public sealed record ForecastEquipmentCapacity(string Id, string Location, EquatableArray<ForecastTimeWindow> Windows);
/// <summary>One scheduled use of the equipment over [Start, End] ticks.</summary>
public sealed record ForecastEquipmentClaim(string Equipment, double Start, double End);

public static class ForecastClaims
{
    /// <summary>One scenario's free stock after native holds and declared promises; unknowns are resource-local.</summary>
    public static Fact<IReadOnlyDictionary<Resource, Fact<double>>> ForecastMaterialBudgetOf(
        Fact<EquatableArray<Amount>> stock, EquatableArray<IngredientReservation> reservations,
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
                if (!AdmissionRules.ValidResource(claim.Resource) || claim.Units.TryGetValue(out var count) && (!double.IsFinite(count) || count < 0))
                    throw new DomainException("invalid forecast material claim");
                if (claim.ReservationBill.TryGetValue(out var bill) && bill.Length != 0 && !linked.Add((bill, claim.Resource)))
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
        if (stock.TryGetValue(out var observed)) ValidateAmounts(observed);
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
        if (!MaterialBudgetRules.MaterialBudget(stock, Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()), reservations, default)
            .TryGetValue(out var free)) return default;
        var result = free.ToDictionary(p => p.Key, p => Known((double)p.Value));
        void Hold(Resource resource, Fact<double> units)
        {
            if (!AdmissionRules.ValidResource(resource)) throw new DomainException("invalid forecast material resource");
            if (units.TryGetValue(out var count) && (!double.IsFinite(count) || count < 0))
                throw new DomainException("invalid forecast material claim");
            var before = result.GetValueOrDefault(resource, Known(0.0));
            var after = before.TryGetValue(out var value) && units.IsKnown ? Known(value - units.Value) : default;
            if (after.TryGetValue(out var balance) && !double.IsFinite(balance)) throw new DomainException("material claim overflow");
            result[resource] = after;
        }
        foreach (var commitment in promises)
        {
            foreach (var claim in commitment.MaterialClaims)
            {
                var units = claim.Units;
                if (units.TryGetValue(out var count) && count == 0) { Hold(claim.Resource, units); continue; }
                if (!claim.ReservationBill.TryGetValue(out var bill)) units = default;
                else if (bill.Length != 0)
                {
                    // Only the same observed bill can cover part of this promise.
                    if (units.IsKnown) units = Known(Math.Max(0, units.Value - nativeAmounts.GetValueOrDefault((bill, claim.Resource))));
                }
                Hold(claim.Resource, units);
            }
            foreach (var change in commitment.StockChanges)
            {
                if (change.Day <= horizonDays && change.Units < 0) Hold(change.Resource, Known(-change.Units));
            }
        }
        return Known<IReadOnlyDictionary<Resource, Fact<double>>>(result);
    }

    /// <summary>Remove declared equipment occupancy once, rebuilding independently for baseline and delta.</summary>
    public static ForecastEquipmentCapacity[] ForecastEquipmentAvailabilityOf(IEnumerable<ForecastEquipmentCapacity> equipment,
        IEnumerable<ForecastEquipmentClaim> claims)
    {
        var rows = new Dictionary<string, ForecastEquipmentCapacity>(StringComparer.Ordinal);
        foreach (var item in equipment)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Location) || !rows.TryAdd(item.Id, item))
                throw new DomainException("invalid or duplicate equipment capacity");
            double end = 0;
            foreach (var window in item.Windows)
            {
                if (!double.IsFinite(window.Start) || !double.IsFinite(window.End) || window.Start < end || window.End < window.Start)
                    throw new DomainException("invalid equipment availability");
                end = window.End;
            }
        }
        foreach (var claim in claims)
        {
            if (string.IsNullOrWhiteSpace(claim.Equipment)) throw new DomainException("invalid equipment claim identity");
            if (!double.IsFinite(claim.Start) || claim.Start < 0 || !double.IsFinite(claim.End) || claim.End < claim.Start)
                throw new DomainException("invalid equipment claim interval");
            if (!rows.TryGetValue(claim.Equipment, out var row) || claim.Start == claim.End) continue;
            var remaining = new List<ForecastTimeWindow>();
            foreach (var window in row.Windows)
            {
                if (claim.End <= window.Start || claim.Start >= window.End) { remaining.Add(window); continue; }
                if (window.Start < claim.Start) remaining.Add(window with { End = claim.Start });
                if (claim.End < window.End) remaining.Add(window with { Start = claim.End });
            }
            rows[row.Id] = row with { Windows = EquatableArray.From(remaining) };
        }
        return rows.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
    }
}
