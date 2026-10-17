using Placement = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Observation;

// The construction census is derived from the building table: a frame-to-frame state that applies only the rows
// that changed between two table versions (Table.Changed), instead of projecting every building each step. Its
// outputs are rebuilt only when a projection changed (a hit-point change alters none), else the last ones stand.

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

    // Outputs.
    public Fact<CurrentConstruction> Census { get; private set; }

    public DomainException? Error { get; private set; }

    public Fact<IReadOnlyDictionary<Resource, long>> DeficitFact { get; private set; }

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
    private void Set(string id, O.BuildingState row)
    {
        switch (row.Status)
        {
            case O.BuildingStatus.Built:
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
            case O.BuildingStatus.Blueprint:
            case O.BuildingStatus.Frame:
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
            default:
                Unset(id);
                break;
        }
    }

    private static BuiltEntry BuiltEntryOf(O.BuildingState row)
    {
        try
        {
            return new BuiltEntry(ConstructionReads.CurrentBuildingOf(row), null);
        }
        catch (DomainException e)
        {
            return new BuiltEntry(new CurrentBuilding(), e);
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
    private void DropNeeds(string id, O.BuildingState row)
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
        Census = Unknown<CurrentConstruction>();
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
                Census = Unknown<CurrentConstruction>();
                return;
            }
            sites.Add(e.Site);
        }
        Census = Known(new CurrentConstruction
        {
            Colony = true,
            Requested = EquatableArray.From(new List<string>()),
            Buildings = EquatableArray.From(buildings),
            Sites = EquatableArray.From(sites),
        });
    }

    private void RebuildDeficit() => DeficitFact = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>(_deficit));

    private sealed record BuiltEntry(CurrentBuilding Building, DomainException? Error);

    private sealed record SiteEntry(ConstructionSite Site, DomainException? Error);
}

internal readonly record struct SiteNeed(Resource Resource, long Count);

public static class ConstructionReads
{
    /// <summary>Maps a native cardinal rotation onto the domain's; any other value is refused by the building constructor.</summary>
    internal static Rotation RotationOf(Placement.Rotation r) => r switch
    {
        Placement.Rotation.North => Rotation.North,
        Placement.Rotation.East => Rotation.East,
        Placement.Rotation.South => Rotation.South,
        Placement.Rotation.West => Rotation.West,
        _ => (Rotation)(-1),
    };

    private static string? SiteStage(O.BuildingStatus status) => status switch
    {
        O.BuildingStatus.Blueprint => "blueprint",
        O.BuildingStatus.Frame => "frame",
        _ => null,
    };

    /// <summary>Projects one built row for the construction census.</summary>
    internal static CurrentBuilding CurrentBuildingOf(O.BuildingState row)
    {
        var building = new Building(row.Building.DefName, ColonyDecodeReads.CellOf(row.Building.Position), RotationOf(row.Rotation), row.Stuff ?? "");
        return new CurrentBuilding { Id = row.Building.Id, Building = building, Cells = EquatableArray.From(CellsRules.RectCells(row.Occupied)) };
    }

    /// <summary>Projects one blueprint or frame row; ok is false for any other row.</summary>
    internal static (ConstructionSite Site, bool Ok, DomainException? Error) ConstructionSiteOf(O.BuildingState row)
    {
        var site = new ConstructionSite();
        try
        {
            var stage = SiteStage(row.Status);
            if (stage is null || row.BuildDefName is not { } buildDefName)
            {
                return (site, false, null);
            }
            var b = new Building(buildDefName, ColonyDecodeReads.CellOf(row.Building.Position), RotationOf(row.Rotation), row.Stuff ?? "");
            var state = row.Construction;
            site = new ConstructionSite { Building = b, Stage = stage, Id = row.Building.Id };
            if (state is not null)
            {
                site = site with
                {
                    ResourcesComplete = Known(state.ResourcesComplete),
                    QualitySensitive = ColonyDecodeReads.Optional(state.QualitySensitive is not null, state.QualitySensitive.GetValueOrDefault()),
                    MaterialsOwed = Known(EquatableArray.From(SiteNeeds(row).Select(n => new Amount(n.Resource, n.Count)))),
                };
                if (state.WorkLeft is { } work)
                {
                    site = site with { WorkLeft = Known(work) };
                }
                if (state.MinimumFinishingSkill is not null)
                {
                    site = site with { MinimumFinishingSkill = Known(state.MinimumFinishingSkill.GetValueOrDefault()) };
                }
                if (state.NativeFinishingSkill is not null)
                {
                    site = site with { NativeFinishingSkill = Known(state.NativeFinishingSkill.GetValueOrDefault()) };
                }
                if (state.EligibleFinishers is not null)
                {
                    site = site with { EligibleFinishers = Known(state.EligibleFinishers.GetValueOrDefault()) };
                }
                if (state.Tier is not null)
                {
                    var tier = (ConstructionTier)state.Tier.GetValueOrDefault();
                    if (!Enum.IsDefined(tier))
                    {
                        throw new ObservationContractException("invalid construction tier");
                    }
                    site = site with { Tier = Known(tier) };
                }
                site = site with { FinishingBlocker = state.FinishingBlocker ?? "" };
            }
            return (site, true, null);
        }
        catch (DomainException e)
        {
            return (site, false, e);
        }
    }

    /// <summary>What one blueprint or frame row is still owed, nothing for any other row.</summary>
    internal static List<SiteNeed> SiteNeeds(O.BuildingState row)
    {
        var needs = new List<SiteNeed>();
        if (row.Status is not (O.BuildingStatus.Blueprint or O.BuildingStatus.Frame))
        {
            return needs;
        }
        foreach (var need in row.Construction?.Resources ?? [])
        {
            if (need.StillNeeded > 0)
            {
                needs.Add(new SiteNeed(new Resource(need.DefName), need.StillNeeded));
            }
        }
        return needs;
    }

    /// <summary>
    /// The colony construction census with its standing sites, and the construction deficit, of a frame's
    /// building table. With the stream's memo it applies only the rows changed since the last frame; without one
    /// (a recording) it projects the whole table. A built row the domain refuses fails the census with its error.
    /// </summary>
    public static (Fact<CurrentConstruction> Construction, Fact<IReadOnlyDictionary<Resource, long>> Deficit) ConstructionFromCensus(BuildingCensus census)
    {
        Fact<CurrentConstruction> construction = default;
        Fact<IReadOnlyDictionary<Resource, long>> deficit = default;
        DomainException? error = null;
        object? Update(object? last)
        {
            var state = ConstructionState.Next(last as ConstructionState, census.Rows);
            construction = state.Census;
            deficit = state.DeficitFact;
            error = state.Error;
            return state;
        }
        if (census.Memo is { } memo)
        {
            memo.Update(Update);
        }
        else
        {
            Update(null);
        }
        if (error is not null)
        {
            throw error;
        }
        return (construction, deficit);
    }

    /// <summary>Bill lifecycle from the same census as ingredient reservations.</summary>
    public static Fact<EquatableArray<ProductionBatchState>> ProductionBatchStates(O.BillsSnapshot? snapshot)
    {
        if (snapshot is null) return default;
        var rows = new List<ProductionBatchState>();
        foreach (var bill in snapshot.Benches.SelectMany(b => b.Bills))
        {
            if (string.IsNullOrWhiteSpace(bill.Id)) return default;
            rows.Add(new(bill.Id, bill.ManagedUnchanged is { } unchanged ? Known(unchanged) : default,
                bill.RepeatMode == RimGovernor.Contracts.Wire.Operations.RepeatMode.Count && bill.RepeatCount is >= 0
                    ? Known(bill.RepeatCount.Value) : default,
                bill.Suspended is { } suspended ? Known(suspended) : default,
                bill.Finished is { } finished ? Known(finished) : default)
            { UnfinishedWork = ProductionUnfinishedWorkOf(bill.UnfinishedWork) });
        }
        return Known(EquatableArray.From(rows));
    }

    private static Fact<ProductionUnfinishedWork> ProductionUnfinishedWorkOf(O.BillUnfinishedWork? work)
    {
        if (work?.Item is not { } id || id.Length != 0 && string.IsNullOrWhiteSpace(id)) return default;
        if (id.Length == 0 && work.Ingredients.Count != 0) return default;
        var items = new List<Amount>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var amount in work.Ingredients)
        {
            if (string.IsNullOrWhiteSpace(amount.DefName) || amount.Units is not > 0 || !seen.Add(amount.DefName)) return default;
            items.Add(new(new Resource(amount.DefName), amount.Units.Value));
        }
        return Known(new ProductionUnfinishedWork(id, EquatableArray.From(items)));
    }

    /// <summary>Reads every bill row's live bill-job reservations. Unknown without the bill census or when any row is malformed.</summary>
    public static Fact<EquatableArray<IngredientReservation>> BillReservations(O.BillsSnapshot? v)
    {
        var unknown = Unknown<EquatableArray<IngredientReservation>>();
        if (v is null)
        {
            return unknown;
        }
        var output = new List<IngredientReservation>();
        foreach (var bench in v.Benches)
        {
            foreach (var bill in bench.Bills)
            {
                foreach (var r in bill.Reservations)
                {
                    if ((r.PawnId ?? "") == "")
                    {
                        return unknown;
                    }
                    var items = new List<Amount>();
                    foreach (var item in r.Items)
                    {
                        if ((item.DefName ?? "") == "" || item.Units is null || item.Units.GetValueOrDefault() < 0)
                        {
                            return unknown;
                        }
                        items.Add(new Amount(new Resource((item.DefName ?? "")), item.Units.GetValueOrDefault()));
                    }
                    output.Add(new IngredientReservation
                    {
                        Pawn = new PawnId((r.PawnId ?? "")), Items = items,
                        Bill = string.IsNullOrWhiteSpace(bill.Id) ? Unknown<string>() : Known(bill.Id),
                    });
                }
            }
        }
        return Known(EquatableArray.From(output));
    }
}
