using Identity = RimGovernor.Brain.GameAccess.ObservationIdentity;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using Cell = RimGovernor.Brain.Domain.Cell;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Brain.Bridge;

// The construction census is derived from the building table: a frame-to-frame state that applies only the rows
// that changed between two table versions (Table.Changed), instead of projecting every building each step. Its
// outputs retain the canonical changed rows; policy derives physical values directly from them.

/// <summary>The census derived from a building table.</summary>
internal sealed class ConstructionState
{
    private Buildings _rows;
    private readonly Dictionary<string, BuiltEntry> _built = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SiteEntry> _sites = new(StringComparer.Ordinal);

    // The standing sites' needs summed.
    private readonly Dictionary<Resource, long> _deficit = [];

    // Dirty marks a changed built or site projection since the outputs.
    private bool _dirty = true;

    // Outputs: the census as last rebuilt, and the error that failed the rebuild.
    public CurrentConstruction Census { get; private set; } = CurrentConstruction.Empty;

    public DomainException? Error { get; private set; }

    /// <summary>
    /// Applies <paramref name="rows"/> to the state derived from the previous version (null derives from scratch)
    /// and returns the state at rows. The outputs are fresh values: a state is only ever mutated inside the
    /// memo's lock.
    /// </summary>
    public static ConstructionState Next(ConstructionState? state, Buildings rows)
    {
        if (state is null)
        {
            state = new ConstructionState();
            foreach (var (id, row) in rows.Table.All())
            {
                state.Set(id, row);
            }
        }
        else
        {
            var prev = state._rows;
            rows.Table.Changed(
                prev.Table,
                (id, row) =>
                {
                    if (prev.Table.TryGetValue(id, out var old))
                    {
                        state.DropNeeds(id, old);
                    }
                    state.Set(id, row);
                },
                id =>
                {
                    if (prev.Table.TryGetValue(id, out var old))
                    {
                        state.DropNeeds(id, old);
                    }
                    state.Unset(id);
                });
        }
        state._rows = rows;
        if (state._dirty)
        {
            state.Rebuild();
        }
        state.RebuildDeficit();
        return state;
    }

    // Projects row into the state, marking the lists dirty only when the row's projection differs from the one held.
    private void Set(string id, RimGovernor.Brain.GameFacts.Observations.BuildingState row)
    {
        switch (row.Status)
        {
            case RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built:
            {
                var next = BuiltEntryOf(row);
                if (!_built.TryGetValue(id, out var old) || old != next)
                {
                    _dirty = true;
                }
                _built[id] = next;
                _sites.Remove(id);
                break;
            }
            case RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Blueprint:
            case RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Frame:
            {
                if (_built.Remove(id))
                {
                    _dirty = true;
                }
                var (site, ok, error) = ConstructionReads.ConstructionSiteOf(row);
                if (ok || error is not null)
                {
                    var next = new SiteEntry(site, error);
                    if (!_sites.TryGetValue(id, out var old) || old != next)
                    {
                        _dirty = true;
                    }
                    _sites[id] = next;
                }
                else
                {
                    UnsetSite(id);
                }
                foreach (var n in ConstructionReads.SiteNeeds(row))
                {
                    _deficit[n.Resource] = _deficit.GetValueOrDefault(n.Resource) + n.Count;
                }
                break;
            }
        }
    }

    private static BuiltEntry BuiltEntryOf(RimGovernor.Brain.GameFacts.Observations.BuildingState row)
    {
        try
        {
            return new BuiltEntry(ConstructionReads.CurrentBuildingOf(row), null);
        }
        catch (DomainException e)
        {
            return new BuiltEntry(new CurrentBuilding { Observed = row }, e);
        }
    }

    // Forgets id's projections.
    private void Unset(string id)
    {
        if (_built.Remove(id))
        {
            _dirty = true;
        }
        UnsetSite(id);
    }

    private void UnsetSite(string id)
    {
        if (_sites.Remove(id))
        {
            _dirty = true;
        }
    }

    // Removes a replaced or removed row's share of the deficit.
    private void DropNeeds(string id, RimGovernor.Brain.GameFacts.Observations.BuildingState row)
    {
        foreach (var n in ConstructionReads.SiteNeeds(row))
        {
            var left = _deficit.GetValueOrDefault(n.Resource) - n.Count;
            if (left == 0)
            {
                _deficit.Remove(n.Resource);
            }
            else
            {
                _deficit[n.Resource] = left;
            }
        }
    }

    // Recomputes the outputs from the projections, in id order.
    private void Rebuild()
    {
        _dirty = false;
        Error = null;
        var buildings = new List<CurrentBuilding>();
        foreach (var id in _built.Keys.Order(StringComparer.Ordinal))
        {
            var e = _built[id];
            if (e.Error is not null)
            {
                Error = e.Error;
                return;
            }
            buildings.Add(e.Building);
        }
        var sites = new List<ConstructionSite>();
        foreach (var id in _sites.Keys.Order(StringComparer.Ordinal))
        {
            var e = _sites[id];
            if (e.Error is not null)
            {
                Error = e.Error;
                return;
            }
            sites.Add(e.Site);
        }
        Census = Census with { Buildings = EquatableArray.From(buildings), Sites = EquatableArray.From(sites) };
    }

    private void RebuildDeficit() => Census = Census with { Deficit = new Dictionary<Resource, long>(_deficit) };

    private sealed record BuiltEntry(CurrentBuilding Building, DomainException? Error);

    private sealed record SiteEntry(ConstructionSite Site, DomainException? Error);
}

internal readonly record struct SiteNeed(Resource Resource, long Count);

public static class ConstructionReads
{
    /// <summary>Projects one built row for the construction census.</summary>
    internal static CurrentBuilding CurrentBuildingOf(RimGovernor.Brain.GameFacts.Observations.BuildingState row)
    {
        var result = new CurrentBuilding { Observed = row };
        _ = result.Building;
        return result;
    }

    /// <summary>
    /// Projects one construction blueprint or frame row; ok is false for an installation blueprint, which is no
    /// construction site. A blueprint or frame without its construction state is a contract error.
    /// </summary>
    internal static (ConstructionSite Site, bool Ok, DomainException? Error) ConstructionSiteOf(RimGovernor.Brain.GameFacts.Observations.BuildingState row)
    {
        var site = new ConstructionSite { Observed = row };
        try
        {
            if (row.Status == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built)
            {
                throw new ObservationContractException("a built row is no construction site");
            }
            if (row.Construction is RimGovernor.Brain.GameFacts.Observations.ConstructionState.Installation)
            {
                return (site, false, null);
            }
            if (row.Construction is not RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build)
            {
                throw new ObservationContractException("a blueprint or frame row carries no construction state");
            }
            _ = site.Building;
            _ = site.Stage;
            return (site, true, null);
        }
        catch (DomainException e)
        {
            return (site, false, e);
        }
    }

    /// <summary>What one construction blueprint or frame row is still owed, nothing for any other row.</summary>
    internal static List<SiteNeed> SiteNeeds(RimGovernor.Brain.GameFacts.Observations.BuildingState row)
    {
        var needs = new List<SiteNeed>();
        if (row.Construction is not RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build build)
        {
            return needs;
        }
        foreach (var need in build.Resources)
        {
            if (need.StillNeeded > 0)
            {
                needs.Add(new SiteNeed(new Resource(need.DefName), need.StillNeeded));
            }
        }
        return needs;
    }

    /// <summary>
    /// The colony construction census with its standing sites and what they are owed, of a frame's building
    /// table. With the stream's memo it applies only the rows changed since the last frame; without one (a
    /// recording) it projects the whole table. A row the domain refuses fails the census with its error.
    /// </summary>
    public static CurrentConstruction ConstructionFromTables(Tables tables)
    {
        object? Update(object? last) => ConstructionState.Next(last as ConstructionState, tables.Buildings);
        var state = (ConstructionState)(tables.ConstructionMemo is { } memo ? memo.Update(Update) : Update(null))!;
        if (state.Error is { } error)
        {
            throw error;
        }
        return state.Census;
    }

    /// <summary>Bill lifecycle from the same census as ingredient reservations.</summary>
    public static EquatableArray<ProductionBatchState> ProductionBatchStates(RimGovernor.Brain.GameFacts.Observations.BillsSnapshot? snapshot)
    {
        if (snapshot is null) throw new RequiredFactException("bills");
        var rows = new List<ProductionBatchState>();
        foreach (var bill in snapshot.Benches.SelectMany(b => b.Bills))
        {
            if (string.IsNullOrWhiteSpace(bill.Id)) throw new RequiredFactException("bills.id");
            _ = ProductionRules.BillRepeatOf(bill.RepeatMode);
            ValidateProductionUnfinishedWork(bill.Id, bill.UnfinishedWork);
            rows.Add(new(bill));
        }
        return EquatableArray.From(rows);
    }

    /// <summary>The bill's bound unfinished item; a bound item without an id, or an embedded ingredient without a def or a positive count, is a broken read.</summary>
    private static void ValidateProductionUnfinishedWork(string bill, RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork work)
    {
        switch (work)
        {
            case RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.None:
                return;
            case RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.Bound bound:
                if (string.IsNullOrWhiteSpace(bound.Item)) throw new RequiredFactException("unfinished_work.item:" + bill);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var amount in bound.Ingredients)
                {
                    if (string.IsNullOrWhiteSpace(amount.DefName) || amount.Units <= 0 || !seen.Add(amount.DefName))
                        throw new RequiredFactException("unfinished_work.ingredients:" + bill);
                }
                return;
        }
        throw new DomainException("undefined unfinished work " + work.GetType().Name);
    }

    /// <summary>
    /// Reads every bill row's live bill-job reservations (#3881). Native reads the bench census every frame (a failed
    /// section fails the frame), so a missing census, a row without its pawn or bill, or an item without its def or a
    /// non-negative count is an unread fact.
    /// </summary>
    public static EquatableArray<IngredientReservation> BillReservations(RimGovernor.Brain.GameFacts.Observations.BillsSnapshot? v)
    {
        if (v is null) throw new RequiredFactException("bill_reservations");
        var output = new List<IngredientReservation>();
        foreach (var bench in v.Benches)
        {
            foreach (var bill in bench.Bills)
            {
                foreach (var r in bill.Reservations)
                {
                    if (string.IsNullOrWhiteSpace(r.PawnId) || string.IsNullOrWhiteSpace(bill.Id))
                        throw new RequiredFactException("bill_reservations.identity:" + bill.Id);
                    foreach (var item in r.Items)
                    {
                        if (item.DefName == "" || item.Units < 0)
                            throw new RequiredFactException("bill_reservations.items:" + bill.Id);
                    }
                    output.Add(new IngredientReservation { Observed = r, Bill = bill.Id });
                }
            }
        }
        return EquatableArray.From(output);
    }
}
